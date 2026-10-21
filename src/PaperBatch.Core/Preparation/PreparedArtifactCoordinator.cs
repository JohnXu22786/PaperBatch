using System.Security.Cryptography;
using System.Text;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Rendering;
using PaperBatch.Core.Serialization;

namespace PaperBatch.Core.Preparation;

/// <summary>Confirms that a rendered PDF contains only static page content before PDFium page composition.</summary>
public interface IStaticPdfContentInspector
{
    Task<bool> HasStaticPageContentAsync(Stream pdf, CancellationToken cancellationToken);
}

public sealed record PreparedArtifactResult(
    PreparedRun? Run,
    RunCreationResult? Persistence,
    IReadOnlyList<string> Diagnostics,
    bool IsCancelled)
{
    public bool Succeeded => Run is not null && Persistence is not null && !IsCancelled;
}

/// <summary>Builds and persists one immutable source-to-layout-to-final-PDF run.</summary>
public sealed class PreparedArtifactCoordinator(
    SourceIntakeService intake,
    SourceSnapshotCache snapshots,
    RendererCoordinator renderers,
    IPdfPageMetadataInspector pageInspector,
    IStaticPdfContentInspector staticContentInspector,
    IPageCompositionAdapter composer,
    RenderArtifactStore artifacts,
    SqliteRunStore runs)
{
    private const long MaximumPreparedSourceBytes = 512L * 1024 * 1024;
    private readonly SemaphoreSlim _prepareGate = new(1, 1);

    public Task<PreparedArtifactResult> PrepareAsync(
        IEnumerable<SourceInput> inputs,
        PrintSettings printSettings,
        LayoutOptions layoutOptions,
        string idempotencyKey,
        RetentionMetadata retention,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default) =>
        PrepareAsync(inputs, printSettings, layoutOptions, idempotencyKey, retention, createdAtUtc,
            null, null, cancellationToken);

    public async Task<PreparedArtifactResult> PrepareAsync(
        IEnumerable<SourceInput> inputs,
        PrintSettings printSettings,
        LayoutOptions layoutOptions,
        string idempotencyKey,
        RetentionMetadata retention,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<PrintSettings>? perItemPrintSettings,
        IReadOnlyList<LayoutDocumentSettings>? perItemLayoutSettings,
        CancellationToken cancellationToken = default)
    {
        await _prepareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PrepareCoreAsync(inputs, printSettings, layoutOptions, idempotencyKey, retention,
                createdAtUtc, perItemPrintSettings, perItemLayoutSettings, cancellationToken).ConfigureAwait(false);
        }
        finally { _prepareGate.Release(); }
    }

    private async Task<PreparedArtifactResult> PrepareCoreAsync(
        IEnumerable<SourceInput> inputs,
        PrintSettings printSettings,
        LayoutOptions layoutOptions,
        string idempotencyKey,
        RetentionMetadata retention,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<PrintSettings>? perItemPrintSettings,
        IReadOnlyList<LayoutDocumentSettings>? perItemLayoutSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(printSettings);
        ArgumentNullException.ThrowIfNull(layoutOptions);
        ArgumentNullException.ThrowIfNull(retention);
        var inputArray = inputs.ToArray();
        var diagnostics = new List<string>();
        var prepared = await intake.PrepareAsync(inputArray, cancellationToken: cancellationToken).ConfigureAwait(false);
        var retainedSnapshots = prepared.Items.Where(item => item.Snapshot is not null)
            .Select(item => item.Snapshot!.SnapshotId).Distinct(StringComparer.Ordinal).ToArray();
        try
        {
            if (perItemPrintSettings is not null && perItemPrintSettings.Count != prepared.Items.Length ||
                perItemLayoutSettings is not null && perItemLayoutSettings.Count != prepared.Items.Length)
                return new(null, null, ["Per-document settings must match the ordered queue length."], false);
            if (prepared.IsCancelled || cancellationToken.IsCancellationRequested)
                return new(null, null, ["Preparation was cancelled; no prepared run was created."], IsCancelled: true);
            if (prepared.Items.IsDefaultOrEmpty || prepared.Items.Any(item => !item.IsReady))
            {
                diagnostics.AddRange(prepared.Items.SelectMany(item => item.Diagnostics).Select(item => item.Message));
                if (diagnostics.Count == 0) diagnostics.Add("Every selected source must pass intake preflight before preparation.");
                return new(null, null, diagnostics, IsCancelled: false);
            }
            var effectiveRequestKey = ComputeEffectiveRequestKey(idempotencyKey, inputArray, prepared.Items,
                printSettings, layoutOptions, perItemPrintSettings, perItemLayoutSettings);
            var existingRun = runs.GetRunByRequestKey(effectiveRequestKey);
            if (existingRun is not null)
            {
                var replayed = new RunCreationResult(existingRun, runs.GetJobs(existingRun.RunId), Replayed: true);
                return new(PreparedRunJson.Parse(System.Text.Encoding.UTF8.GetBytes(existingRun.PreparedRunJson)), replayed, [], false);
            }
            for (var sourceIndex = 0; sourceIndex < inputArray.Length; sourceIndex++)
            {
                var expectedSha256 = inputArray[sourceIndex].ExpectedSha256;
                if (expectedSha256 is not null && !string.Equals(expectedSha256,
                    prepared.Items[sourceIndex].Snapshot!.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add($"{Path.GetFileName(prepared.Items[sourceIndex].SourcePath)} changed after queue preflight. Reimport the source before preparing; no final artifact was created.");
                    return new(null, null, diagnostics, IsCancelled: false);
                }
            }
            var items = new List<PreparedItem>(prepared.Items.Length);
            var layoutDocuments = new List<LayoutDocument>(prepared.Items.Length);
            var compositionSources = new List<PdfCompositionSource>(prepared.Items.Length);
            long sourceBytes = 0;
            for (var sourceIndex = 0; sourceIndex < prepared.Items.Length; sourceIndex++)
            {
                var source = prepared.Items[sourceIndex];
                // Copies are compiled into the immutable PDF. The print settings stored on
                // the prepared item describe the downstream device request, which must be
                // one copy to avoid applying the requested count a second time.
                var sourceSettings = (perItemPrintSettings is null ? printSettings : perItemPrintSettings[sourceIndex])
                    with { Copies = 1 };
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = source.Snapshot!;
                var render = await renderers.RenderAsync(
                    new RenderRequest(snapshot.SnapshotId, source.Format!.Value, RendererExecutionContext.BackgroundWorker),
                    TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
                if (render.Status != RenderStatus.Succeeded || render.Artifact is null)
                    return new(null, null, [$"{Path.GetFileName(source.SourcePath)}: {render.Message ?? render.Status.ToString()}"],
                        render.Status == RenderStatus.Cancelled);

                var receipt = artifacts.GetReceipt(render.Artifact.ArtifactId);
                if (receipt.Artifact.Sha256 != render.Artifact.Sha256 || receipt.Artifact.LengthBytes != render.Artifact.LengthBytes)
                    throw new InvalidDataException("A renderer artifact receipt did not match its reported identity.");

                using var renderedPdf = artifacts.OpenRead(render.Artifact.ArtifactId);
                var pageSizes = await pageInspector.GetPageSizesAsync(renderedPdf, cancellationToken).ConfigureAwait(false);
                if (pageSizes.Count == 0) throw new InvalidDataException("The rendered PDF has no pages.");
                renderedPdf.Position = 0;
                if (!await staticContentInspector.HasStaticPageContentAsync(renderedPdf, cancellationToken).ConfigureAwait(false))
                    return new(null, null, ["PDF forms or interactive content are unsupported by the configured page compositor."], IsCancelled: false);

                sourceBytes = checked(sourceBytes + renderedPdf.Length);
                if (sourceBytes > MaximumPreparedSourceBytes)
                    return new(null, null, ["Combined rendered PDF sources exceed the bounded preparation limit."], IsCancelled: false);
                renderedPdf.Position = 0;
                using var sourceBuffer = new MemoryStream(checked((int)renderedPdf.Length));
                await renderedPdf.CopyToAsync(sourceBuffer, 64 * 1024, cancellationToken).ConfigureAwait(false);
                var itemId = "item_" + Guid.NewGuid().ToString("N");
                var artifactReceipt = receipt.Artifact;
                var rendererResult = new RendererResult(receipt.EngineId, receipt.EngineVersion,
                    artifactReceipt.ArtifactId, artifactReceipt.Sha256, artifactReceipt.LengthBytes, pageSizes.Count,
                    new RendererCapabilities(1, ["application/pdf"]));
                var pageMap = Enumerable.Range(1, pageSizes.Count).Select(page => new PageMapping(page, page)).ToArray();
                items.Add(new PreparedItem(itemId, snapshot, rendererResult, pageMap, sourceSettings));
                layoutDocuments.Add(new LayoutDocument(itemId, pageSizes, sourceSettings.Media,
                    sourceSettings.PrinterId, sourceSettings.TrayId,
                    perItemLayoutSettings is null ? null : perItemLayoutSettings[sourceIndex], Path.GetFileName(source.SourcePath)));
                compositionSources.Add(new PdfCompositionSource(itemId, sourceBuffer.ToArray(), StaticPageContentVerified: true));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var layout = LayoutCompiler.Compile(layoutDocuments, layoutOptions);
            if (!layout.Succeeded)
                return new(null, null, layout.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray(), IsCancelled: false);
            var segments = new List<CompatibleSegment>();
            var segmentedItems = items.Select((item, index) => (Item: item,
                LayoutSettings: perItemLayoutSettings is null ? null : perItemLayoutSettings[index]));
            foreach (var group in segmentedItems.GroupAdjacentBy(entry => (entry.Item.PrintSettings, entry.LayoutSettings)))
                segments.Add(new CompatibleSegment("segment_" + segments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    segments.Count, group.Select(entry => entry.Item.ItemId), "layout-v1", group[0].Item.PrintSettings));
            var finalId = "run_" + Guid.NewGuid().ToString("N");
            var fingerprint = ComputeFingerprint(items, printSettings, layoutOptions, perItemLayoutSettings);
            using var composedPdf = new MemoryStream();
            await composer.ComposePdfAsync(layout, compositionSources, composedPdf, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            composedPdf.Position = 0;
            var finalReceipt = await artifacts.PersistPdfAsync(composedPdf, "pdfium-page-composition", composer is PdfiumPageCompositionAdapter pdfium
                    ? pdfium.RuntimeSha256 : "injected",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["layout"] = "u4-v1" }, cancellationToken).ConfigureAwait(false);
            using (var reopened = artifacts.OpenRead(finalReceipt.Artifact.ArtifactId))
            {
                var savedPageSizes = await pageInspector.GetPageSizesAsync(reopened, cancellationToken).ConfigureAwait(false);
                if (savedPageSizes.Count != layout.Pages.Length)
                    throw new InvalidDataException("The saved final PDF page count does not match the compiled layout.");
            }
            var finalArtifact = new FinalPdfArtifact(finalReceipt.Artifact.ArtifactId,
                finalReceipt.Artifact.Sha256, finalReceipt.Artifact.LengthBytes, layout.Pages.Length);
            var run = PreparedRun.Create(finalId, createdAtUtc, fingerprint, items, segments, finalArtifact);
            RunCreationResult persisted;
            try { persisted = runs.CreateRun(run, effectiveRequestKey, retention); }
            catch (IdempotencyConflictException)
            {
                var replay = runs.GetRunByRequestKey(effectiveRequestKey);
                if (replay is null) throw;
                persisted = new RunCreationResult(replay, runs.GetJobs(replay.RunId), Replayed: true);
                run = PreparedRunJson.Parse(System.Text.Encoding.UTF8.GetBytes(replay.PreparedRunJson));
            }
            return new(run, persisted, diagnostics, IsCancelled: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(null, null, ["Preparation was cancelled; no prepared run was created."], IsCancelled: true);
        }
        finally
        {
            foreach (var snapshotId in retainedSnapshots)
            {
                try { snapshots.Release(snapshotId); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
            }
        }
    }

    private static string ComputeFingerprint(IEnumerable<PreparedItem> items, PrintSettings settings, LayoutOptions options,
        IReadOnlyList<LayoutDocumentSettings>? perItemLayoutSettings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        foreach (var item in items)
        {
            Add(item.Source.SnapshotId);
            Add(item.Source.Sha256);
            Add(item.Render.ArtifactId);
            Add(item.Render.ArtifactSha256);
            Add(System.Text.Json.JsonSerializer.Serialize(item.PrintSettings));
        }
        Add(System.Text.Json.JsonSerializer.Serialize(settings));
        Add(System.Text.Json.JsonSerializer.Serialize(perItemLayoutSettings?.Select(document => document is null ? null :
            document with { Ranges = document.Ranges.IsDefault ? [] : document.Ranges }).ToArray()));
        Add(System.Text.Json.JsonSerializer.Serialize(new
        {
            options.Copies,
            options.CopyOrder,
            options.NUp,
            options.Duplex,
            options.SeparateFilesOnFront,
            options.ReverseOrder,
            options.Fit,
            options.SheetSize,
            Ranges = options.Ranges.IsDefault ? Array.Empty<PageRange>() : options.Ranges.ToArray(),
            options.AddFrontCover,
            options.AddSeparatorsBetweenFiles,
            options.SeparatePacketsOnFront,
            options.MaximumOutputPages
        }));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeEffectiveRequestKey(string idempotencyKey, IReadOnlyList<SourceInput> inputs,
        IReadOnlyList<SourcePreparationItem> preparedItems, PrintSettings settings, LayoutOptions options,
        IReadOnlyList<PrintSettings>? perItemPrintSettings, IReadOnlyList<LayoutDocumentSettings>? perItemLayoutSettings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add(idempotencyKey);
        for (var index = 0; index < inputs.Count; index++)
        {
            Add(Path.GetFullPath(inputs[index].Path));
            Add(inputs[index].IntentionalCopy ? "copy" : "source");
            Add(preparedItems[index].Snapshot!.Sha256);
        }
        Add(System.Text.Json.JsonSerializer.Serialize(settings));
        Add(System.Text.Json.JsonSerializer.Serialize(perItemPrintSettings));
        Add(System.Text.Json.JsonSerializer.Serialize(perItemLayoutSettings?.Select(document => document is null ? null :
            document with { Ranges = document.Ranges.IsDefault ? [] : document.Ranges }).ToArray()));
        Add(System.Text.Json.JsonSerializer.Serialize(new
        {
            options.Copies, options.CopyOrder, options.NUp, options.Duplex, options.SeparateFilesOnFront,
            options.ReverseOrder, options.Fit, options.SheetSize,
            Ranges = options.Ranges.IsDefault ? Array.Empty<PageRange>() : options.Ranges.ToArray(),
            options.AddFrontCover, options.AddSeparatorsBetweenFiles, options.SeparatePacketsOnFront, options.MaximumOutputPages
        }));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

internal static class AdjacentGrouping
{
    internal static IEnumerable<List<T>> GroupAdjacentBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector)
    {
        List<T>? group = null;
        TKey? previous = default;
        foreach (var item in source)
        {
            var key = keySelector(item);
            if (group is null || !EqualityComparer<TKey>.Default.Equals(previous!, key))
            {
                if (group is not null) yield return group;
                group = [];
                previous = key;
            }
            group.Add(item);
        }
        if (group is not null) yield return group;
    }
}
