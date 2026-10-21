using System.Runtime.InteropServices;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Preparation;
using PaperBatch.Core.Recipes;
using PaperBatch.Core.Rendering;
using PaperBatch.Core.Serialization;

namespace PaperBatch.Desktop.Presentation;

/// <summary>Owns the real application dependencies; intentionally registers no demo or fake renderer.</summary>
internal static class DesktopComposition
{
    public static DesktopShellViewModel CreateProduction()
    {
        try
        {
            return CreateAvailableServices();
        }
        catch (Exception exception)
        {
            var sources = new ProductionPresentationSources();
            return new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
                new WorkspaceStatusSnapshot("PRODUCTION MODE · INITIALIZATION FAILED",
                    $"Local application services could not initialize ({exception.GetType().Name}). No files were loaded and no jobs were created.", false,
                    InitializationFailed: true));
        }
    }

    private static DesktopShellViewModel CreateAvailableServices()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            throw new InvalidOperationException("The platform did not provide a local application data directory.");

        var root = Path.Combine(appData, "PaperBatch");
        Directory.CreateDirectory(root);
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions
        {
            RootPath = Path.Combine(root, "SourceCache")
        });
        var intake = new SourceIntakeService(cache);
        var artifactStore = new RenderArtifactStore(Path.Combine(root, "RenderArtifacts"));
        var renderer = new RendererCoordinator([
            new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifactStore)),
            new BuiltInImageRendererAdapter(cache, artifactStore)
        ]);
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();
        IPdfArtifactPreviewRenderer? previewRenderer = null;
        PreparedArtifactCoordinator? preparation = null;
        var pdfiumPath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_PATH");
        var pdfiumHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_SHA256");
        if (!string.IsNullOrWhiteSpace(pdfiumPath) && !string.IsNullOrWhiteSpace(pdfiumHash))
        {
            var pdfium = new PdfiumArtifactInspector(pdfiumPath, pdfiumHash);
            previewRenderer = pdfium;
            preparation = new PreparedArtifactCoordinator(intake, cache, renderer, pdfium, pdfium,
                new PdfiumPageCompositionAdapter(pdfiumPath, pdfiumHash), artifactStore, store);
        }

        // Unsupported formats have no renderer, and there is no print submission backend.
        var sources = new ProductionPresentationSources(intake, renderer, cache, rendererAvailable: true,
            artifacts: artifactStore, previewRenderer: previewRenderer, runStore: store, preparation: preparation);
        return new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("PRODUCTION MODE",
                preparation is null
                    ? "PDF pass-through and supported JPEG/PNG artifact rendering are available. Preparation and final preview require an explicitly configured PDFium runtime; LibreOffice conversion and production print submission remain unavailable."
                    : "PDF, JPEG and PNG queue inputs can be prepared with the pinned PDFium runtime. Printer capabilities, Office conversion and production print submission remain unavailable.", false));
    }
}

internal sealed class ProductionPresentationSources :
    IQueueImportPresentationSource,
    IQueueEditingPresentationSource,
    IEffectiveSettingsPresentationSource,
    IFinalPreviewPresentationSource,
    IPreparedArtifactPresentationSource,
    IPreflightPresentationSource,
    IHistoryPresentationSource,
    IRecipeManagementPresentationSource,
    IQueuePreparationPresentationSource
{
    private readonly SourceIntakeService? _intake;
    private readonly RendererCoordinator _renderer;
    private readonly SourceSnapshotCache? _cache;
    private readonly bool _rendererAvailable;
    private readonly RenderArtifactStore? _artifacts;
    private readonly IPdfArtifactPreviewRenderer? _previewRenderer;
    private readonly SqliteRunStore? _runStore;
    private readonly PreparedArtifactCoordinator? _preparation;
    private readonly Dictionary<string, PreparedRun> _preparedRunsByQueueItem = new(StringComparer.Ordinal);
    private readonly List<QueueItemSnapshot> _queue = [];
    private readonly Dictionary<string, string> _queueLocations = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _resolutionSummaries = [];
    private readonly HashSet<string> _preparableQueueItems = new(StringComparer.Ordinal);

    public event Action<string>? PreparedArtifactChanged;
    public bool IsRecipeStoreAvailable => _runStore is not null && TemplateReadError is null;

    public ProductionPresentationSources(SourceIntakeService? intake = null, RendererCoordinator? renderer = null,
        SourceSnapshotCache? cache = null, bool rendererAvailable = false,
        RenderArtifactStore? artifacts = null, IPdfArtifactPreviewRenderer? previewRenderer = null,
        SqliteRunStore? runStore = null, PreparedArtifactCoordinator? preparation = null)
    {
        _intake = intake;
        _renderer = renderer ?? new RendererCoordinator(Array.Empty<IRendererAdapter>());
        _cache = cache;
        _rendererAvailable = rendererAvailable;
        _artifacts = artifacts;
        _previewRenderer = previewRenderer;
        _runStore = runStore;
        _preparation = preparation;
    }

    public bool IsAvailable => _intake is not null;

    public IReadOnlyList<QueueItemSnapshot> GetQueueItems() => _queue.ToArray();

    public void Move(string queueItemId, int delta)
    {
        var index = _queue.FindIndex(item => string.Equals(item.Id, queueItemId, StringComparison.Ordinal));
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _queue.Count) return;
        var item = _queue[index];
        _queue.RemoveAt(index);
        _queue.Insert(target, item);
    }

    public void Remove(string queueItemId)
    {
        _queue.RemoveAll(item => string.Equals(item.Id, queueItemId, StringComparison.Ordinal));
        _queueLocations.Remove(queueItemId);
        _preparableQueueItems.Remove(queueItemId);
    }

    public async Task<IReadOnlyList<QueueItemSnapshot>> ImportAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        if (_intake is null) throw new InvalidOperationException("Source intake is not configured.");
        var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var inputs = paths.Select(path => new SourceInput(path, IntentionalCopy: !seenPaths.Add(Path.GetFullPath(path)))).ToArray();
        var prepared = await _intake.PrepareAsync(inputs, cancellationToken: cancellationToken).ConfigureAwait(false);
        var queueItemIdBySnapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var existing in _queue) InvalidatePreparedRun(existing.Id);
            foreach (var source in prepared.Items)
            {
                // Queue identity belongs to the row. A source snapshot can be shared or
                // replaced independently and must never alias two intentional copies.
                var id = Guid.NewGuid().ToString("N");
                if (source.Snapshot is { } capturedSnapshot) queueItemIdBySnapshot[capturedSnapshot.SnapshotId] = id;
                var title = Path.GetFileName(source.SourcePath);
                var findings = source.Diagnostics.Select(diagnostic => new PreflightFindingSnapshot(
                    diagnostic.Code.ToString(), diagnostic.Severity.ToString(), diagnostic.Message)).ToList();
                var status = source.IsReady ? "Preflight ready · checking renderer" : "Needs attention";
                var details = source.Snapshot is { } snapshot
                    ? $"{source.Format} · {snapshot.LengthBytes:N0} bytes · SHA-256 {snapshot.Sha256[..12]}…"
                    : source.Format?.ToString() ?? "Source was not captured";
                if (source.IsReady && source.Snapshot is { } readySnapshot && source.Format is { } format)
                {
                    var result = await Task.Run(() => _renderer.RenderAsync(new RenderRequest(readySnapshot.SnapshotId, format,
                        RendererExecutionContext.BackgroundWorker), TimeSpan.FromMinutes(2), cancellationToken), cancellationToken).ConfigureAwait(false);
                    if (result.Status == RenderStatus.Succeeded && result.Artifact is not null)
                    {
                        status = "Ready to prepare · " + format;
                        _preparableQueueItems.Add(id);
                    }
                    else
                    {
                        status = result.Status == RenderStatus.Unsupported ? "Unsupported format" : "Renderer needs attention";
                    }
                    findings.Add(new PreflightFindingSnapshot("Renderer", result.Status.ToString(),
                        result.Message ?? (result.Artifact is { PageCount: 0 }
                            ? $"PDF artifact {result.Artifact.ArtifactId} persisted; page count is not reported."
                            : result.Artifact is { } rendered
                                ? $"Rendered artifact {rendered.ArtifactId} persisted with {rendered.PageCount} page(s)."
                                : "No rendered artifact was returned.")));
                }
                var item = new QueueItemSnapshot(id, title, details, status);
                _queue.Add(item);
                _queueLocations[id] = source.SourcePath;
                _findings[id] = findings;
            }
            return _queue.Skip(_queue.Count - prepared.Items.Length).ToArray();
        }
        finally
        {
            if (_cache is not null)
            {
                foreach (var snapshotId in prepared.Items.Where(item => item.Snapshot is not null)
                    .Select(item => item.Snapshot!.SnapshotId).Distinct(StringComparer.Ordinal))
                {
                    try { _cache.Release(snapshotId); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.SecurityException)
                    {
                        if (queueItemIdBySnapshot.TryGetValue(snapshotId, out var queueItemId) && _findings.TryGetValue(queueItemId, out var findings))
                            findings.Add(new("Source cache", "Cleanup failed", "The temporary source snapshot could not be released."));
                    }
                }
            }
        }
    }

    private readonly Dictionary<string, List<PreflightFindingSnapshot>> _findings = new(StringComparer.Ordinal);
    public bool CanPrepare => _preparation is not null && _queue.Count > 0 &&
        _queue.All(item => _queueLocations.ContainsKey(item.Id) && _preparableQueueItems.Contains(item.Id));
    public IReadOnlyList<string> ResolutionSummaries => _resolutionSummaries;

    public async Task<PreparedArtifactResult> PrepareQueueAsync(PrintSettings requestedSettings, LayoutOptions layoutOptions,
        PaperBatch.Core.Recipes.JobRecipe? recipe = null, CancellationToken cancellationToken = default)
    {
        if (_preparation is null || _runStore is null || _queue.Count == 0)
            return new(null, null, ["A trusted PDFium runtime and a nonempty local queue are required before preparation."], false);
        var now = DateTimeOffset.UtcNow;
        var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var paths = _queue.Select(item => _queueLocations.TryGetValue(item.Id, out var path)
            ? new SourceInput(path, IntentionalCopy: !seenPaths.Add(Path.GetFullPath(path)))
            : throw new InvalidOperationException("A queue source location is no longer available.")).ToArray();
        IReadOnlyList<PrintSettings>? perItemPrintSettings = null;
        IReadOnlyList<LayoutDocumentSettings>? perItemLayoutSettings = null;
        if (recipe is not null)
        {
            var resolved = new List<PaperBatch.Core.Recipes.ResolvedRecipeSettings>(paths.Length);
            var recipeSourcesByPath = new Dictionary<string, Queue<PaperBatch.Core.Recipes.RecipeSource>>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var source in recipe.Sources)
            {
                var sourcePath = Path.GetFullPath(source.Location);
                if (!recipeSourcesByPath.TryGetValue(sourcePath, out var occurrences))
                    recipeSourcesByPath[sourcePath] = occurrences = new Queue<PaperBatch.Core.Recipes.RecipeSource>();
                occurrences.Enqueue(source);
            }
            for (var index = 0; index < paths.Length; index++)
            {
                var path = paths[index].Path;
                var type = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".pdf" => "pdf", ".jpg" or ".jpeg" => "jpeg", ".png" => "png",
                    _ => null
                };
                if (type is null)
                    return new(null, null, [$"{Path.GetFileName(path)}: recipe rules support only PDF, JPEG, and PNG sources."], false);
                var normalizedPath = Path.GetFullPath(path);
                var recipeSource = recipeSourcesByPath.TryGetValue(normalizedPath, out var occurrences) && occurrences.Count > 0
                    ? occurrences.Dequeue()
                    : new PaperBatch.Core.Recipes.RecipeSource("queue-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), path);
                var batchDefaults = new PaperBatch.Core.Recipes.RecipePrintSettings(requestedSettings.Copies,
                    requestedSettings.ColorMode, requestedSettings.DuplexMode, requestedSettings.Orientation,
                    requestedSettings.ScalingMode, requestedSettings.Media, requestedSettings.PrinterId, requestedSettings.TrayId);
                resolved.Add(PaperBatch.Core.Recipes.RecipeSettingsResolver.Resolve(recipe, recipeSource, type, batchDefaults));
            }
            _resolutionSummaries = resolved.Select((item, index) => FormatResolutionSummary(
                Path.GetFileName(paths[index].Path), item)).ToArray();
            var first = resolved[0];
            if (resolved.Skip(1).Any(item => item.Values.ColorMode != first.Values.ColorMode ||
                item.Values.DuplexMode != first.Values.DuplexMode || item.Values.PrinterId != first.Values.PrinterId ||
                item.Values.TrayId != first.Values.TrayId))
            {
                var sources = resolved.Select(item => item.MatchingRuleId is null ? "recipe defaults" : "rule " + item.MatchingRuleId)
                    .Distinct(StringComparer.Ordinal).ToArray();
                return new(null, null, [$"Per-file device requests resolve differently ({string.Join(", ", sources)}). The current prepared-run model requires one device profile per compatible segment and this shell cannot validate per-file device settings; no artifact was created."], false);
            }
            // The layout compiler needs to know whether sheets are duplex so it can
            // keep file boundaries on the correct physical face. This remains a
            // requested layout value; it is not validation of printer capability.
            layoutOptions = layoutOptions with { Duplex = first.Values.DuplexMode != DuplexMode.Simplex };
            if (resolved.Any(item => item.Values.Orientation == Orientation.Auto))
                layoutOptions = layoutOptions with { SheetSize = null };
            var sourceSettings = new List<PrintSettings>(resolved.Count);
            var documentSettings = new List<LayoutDocumentSettings>(resolved.Count);
            foreach (var item in resolved)
            {
                if (item.Values.Media is not ("Letter" or "A4"))
                    return new(null, null, [$"{item.MatchingRuleId ?? "recipe defaults"}: requested media is not supported by the desktop layout controls; no artifact was created."], false);
                PageSize? size = item.Values.Orientation == Orientation.Auto ? null :
                    item.Values.Media == "A4" ? new PageSize(595.28, 841.89) : new PageSize(612, 792);
                if (item.Values.Orientation == Orientation.Landscape && size is not null)
                    size = new PageSize(size.HeightPoints, size.WidthPoints);
                sourceSettings.Add(new PrintSettings(item.Values.Copies, item.Values.ColorMode, item.Values.DuplexMode,
                    item.Values.Media, item.Values.Orientation, item.Values.ScalingMode, item.Values.PrinterId, item.Values.TrayId));
                documentSettings.Add(new LayoutDocumentSettings(item.Values.Copies,
                    item.NUp switch { 1 => NUpMode.One, 2 => NUpMode.Two, 4 => NUpMode.Four, _ => null },
                    item.Values.ScalingMode == ScalingMode.ActualSize ? FitMode.ActualSize : FitMode.ShrinkToFit,
                    size, item.PageRange is null ? default : ParseRecipeRange(item.PageRange)));
            }
            perItemPrintSettings = sourceSettings;
            perItemLayoutSettings = documentSettings;
            requestedSettings = sourceSettings[0];
        }
        else _resolutionSummaries = [];
        var result = await _preparation.PrepareAsync(paths, requestedSettings, layoutOptions,
            "desktop-prepare-" + Guid.NewGuid().ToString("N"),
            new RetentionMetadata(now.AddDays(30), RetainWhileUnresolved: true), now,
            perItemPrintSettings, perItemLayoutSettings, cancellationToken).ConfigureAwait(false);
        if (result.Run is { } run)
        {
            foreach (var item in _queue) RegisterPreparedRun(item.Id, run);
        }
        return result;
    }

    private static string FormatResolutionSummary(string fileName, PaperBatch.Core.Recipes.ResolvedRecipeSettings resolved)
    {
        string Origin(string field, string fallback = "batch/recipe defaults")
        {
            var source = resolved.Provenance.GetValueOrDefault(field, fallback);
            if (source == "file override") return "file override/单文件覆盖";
            if (source.StartsWith("rule ", StringComparison.Ordinal)) return "rule/规则 " + source[5..];
            return "batch/recipe defaults/批次或配方默认值";
        }
        string Entry(string field, string label, string value, string fallback = "batch/recipe defaults") =>
            $"{label}={value} ({Origin(field, fallback)})";
        var ruleOrDefault = resolved.MatchingRuleId is null
            ? "no matching rule/无匹配规则"
            : "first matching rule/首个匹配规则 " + resolved.MatchingRuleId;
        var noValue = "not selected/未选择";
        return $"{fileName}: {ruleOrDefault}; requested/请求值 {Entry("copies", "copies/份数", resolved.Values.Copies.ToString(System.Globalization.CultureInfo.InvariantCulture))}; " +
            $"{Entry("colorMode", "color mode/色彩模式", resolved.Values.ColorMode.ToString())}; " +
            $"{Entry("duplexMode", "duplex mode/双面模式", resolved.Values.DuplexMode.ToString())}; " +
            $"{Entry("orientation", "orientation/方向", resolved.Values.Orientation.ToString())}; " +
            $"{Entry("scalingMode", "scaling/缩放", resolved.Values.ScalingMode.ToString())}; " +
            $"{Entry("media", "media/纸张", resolved.Values.Media)}; " +
            $"{Entry("printerId", "printer/打印机", resolved.Values.PrinterId ?? noValue)}; " +
            $"{Entry("trayId", "tray/纸盒", resolved.Values.TrayId ?? noValue)}; " +
            $"{Entry("nUp", "pages per sheet/每张页数", resolved.NUp?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "batch control/批次控件", "batch layout controls")}; " +
            $"{Entry("pageRange", "page range/页码范围", resolved.PageRange ?? "all pages/全部页面", "batch page-range control")}.";
    }

    private static System.Collections.Immutable.ImmutableArray<PageRange> ParseRecipeRange(string value)
    {
        var parts = value.Split('-');
        var first = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var last = parts.Length == 1 ? first : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        return [new PageRange(first, last)];
    }

    public IReadOnlyList<EffectiveSettingSnapshot> GetEffectiveSettings(string queueItemId) =>
        [new("Renderer", _rendererAvailable ? "Partial" : "Unavailable", _rendererAvailable
            ? (_preparation is null
                ? "Background PDF pass-through and JPEG/PNG artifact rendering are configured; preparation needs the explicitly pinned PDFium runtime."
                : "Pinned PDFium preparation is configured for supported PDF/JPEG/PNG formats; Office and other unregistered formats remain unsupported.")
            : "No renderer is configured; no setting is treated as effective."),
         new("Printer", "Not selected", "The UI does not choose a default printer or silently substitute one.")];
    public void RegisterPreparedRun(string queueItemId, PreparedRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueItemId);
        ArgumentNullException.ThrowIfNull(run);
        if (run.FinalArtifact is null) throw new InvalidOperationException("A prepared run without a final PDF cannot be previewed.");
        if (_runStore is not null)
        {
            var existing = _runStore.GetPreparedRun(run.RunId);
            if (existing is null)
            {
                _runStore.CreateRun(run, CreateRunRequestKey(run.RunId),
                    new RetentionMetadata(run.CreatedAtUtc.AddDays(30), RetainWhileUnresolved: true));
            }
            else if (!string.Equals(PreparedRunJson.Serialize(existing), PreparedRunJson.Serialize(run), StringComparison.Ordinal))
            {
                throw new InvalidDataException("The prepared run ID is already stored with different run metadata.");
            }
        }
        _preparedRunsByQueueItem[queueItemId] = run;
        PreparedArtifactChanged?.Invoke(queueItemId);
    }

    private static string CreateRunRequestKey(string runId) =>
        "desktop-prepared-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();

    public void InvalidatePreparedRun(string queueItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueItemId);
        _preparedRunsByQueueItem.Remove(queueItemId);
        PreparedArtifactChanged?.Invoke(queueItemId);
    }

    public async Task<PreviewSnapshot> GetPreviewAsync(string queueItemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_artifacts is null || _previewRenderer is null ||
            !_preparedRunsByQueueItem.TryGetValue(queueItemId, out var run) || run.FinalArtifact is not { } artifact)
            return new PreviewSnapshot(queueItemId, "Final preview unavailable: no prepared artifact exists.", 0, 0);

        var receipt = _artifacts.GetReceipt(artifact.ArtifactId).Artifact;
        if (receipt.Sha256 != artifact.Sha256 || receipt.LengthBytes != artifact.LengthBytes)
            throw new InvalidDataException("The prepared preview artifact does not match its persisted run identity.");
        using var pdf = _artifacts.OpenRead(artifact.ArtifactId);
        var raster = await _previewRenderer.RenderPageAsync(pdf, 1, 1600, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var image = new WriteableBitmap(new PixelSize(raster.Width, raster.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var framebuffer = image.Lock())
        {
            for (var row = 0; row < raster.Height; row++)
                Marshal.Copy(raster.BgraPixels, row * raster.Stride,
                    IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), raster.Stride);
        }
        return new PreviewSnapshot(queueItemId,
            $"Prepared PDF · page 1 of {artifact.PageCount}", 1, artifact.PageCount,
            image, artifact.ArtifactId, artifact.Sha256);
    }
    public IReadOnlyList<PreflightFindingSnapshot> GetFindings(string queueItemId) =>
        _findings.TryGetValue(queueItemId, out var findings) ? findings.ToArray() :
            [new("Rendering", _rendererAvailable ? "Not run" : "Unavailable", _rendererAvailable
                ? "No preflight or artifact result exists for this queue item."
                : "No renderer is configured; source content was not rendered.")];
    public string? HistoryReadError { get; private set; }

    public IReadOnlyList<HistoryEntrySnapshot> GetHistoryEntries()
    {
        HistoryReadError = null;
        if (_runStore is null) return [];
        try
        {
            return _runStore.GetRecentRuns()
                .Select(run => new HistoryEntrySnapshot(
                    $"Run {run.RunId[..Math.Min(8, run.RunId.Length)]}",
                    $"Created {run.CreatedAtUtc.ToLocalTime():g} · {_runStore.GetJobs(run.RunId).Length} job(s)",
                    run.State.ToString()))
                .ToArray();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException or FormatException or NotSupportedException or System.Data.Common.DbException)
        {
            HistoryReadError = "Saved run history could not be read. The local history data may be damaged or use a newer schema.";
            return [];
        }
    }

    public string? TemplateReadError { get; private set; }

    public IReadOnlyList<TemplateSnapshot> GetTemplates(bool chinese = false)
    {
        TemplateReadError = null;
        if (_runStore is null) return [];
        try
        {
            return _runStore.GetRecipes()
                .Select(saved => new TemplateSnapshot(saved.Recipe.RecipeId, saved.Recipe.Name,
                    chinese ? $"已保存在本地 · {saved.Recipe.Sources.Length} 个来源" : $"Saved locally · {saved.Recipe.Sources.Length} source(s)",
                    chinese
                        ? $"默认值：{saved.Recipe.Settings.Copies} 份 · {Localize(saved.Recipe.Settings.ColorMode, true)} · {Localize(saved.Recipe.Settings.DuplexMode, true)} · {saved.Recipe.Settings.Media}（未应用）"
                        : $"Defaults: {saved.Recipe.Settings.Copies} copies · {saved.Recipe.Settings.ColorMode} · {saved.Recipe.Settings.DuplexMode} · {saved.Recipe.Settings.Media} (not applied)"))
                .ToArray();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException or FormatException or UnsupportedSchemaVersionException or System.Data.Common.DbException)
        {
            TemplateReadError = chinese
                ? "无法读取已保存配方。本地配方数据可能已损坏或使用了更新的架构。"
                : "Saved recipes could not be read. The local recipe data may be damaged or use a newer schema.";
            return [];
        }
    }

    private static string Localize(ColorMode value, bool chinese) => !chinese ? value.ToString() : value switch
    {
        ColorMode.Color => "彩色", ColorMode.Monochrome => "黑白", _ => value.ToString()
    };

    private static string Localize(DuplexMode value, bool chinese) => !chinese ? value.ToString() : value switch
    {
        DuplexMode.Simplex => "单面", DuplexMode.LongEdge => "双面长边翻转", DuplexMode.ShortEdge => "双面短边翻转", _ => value.ToString()
    };

    public JobRecipeSnapshot CreateRecipe(string name, IReadOnlyList<string> queueItemIds, RecipeDefaultsSnapshot defaults)
    {
        if (_runStore is null) throw new InvalidOperationException("Local recipe storage is unavailable.");
        var locations = queueItemIds.Select(id => _queueLocations.TryGetValue(id, out var path)
            ? path : throw new InvalidOperationException("A selected queue source is no longer available.")).ToArray();
        if (locations.Length == 0) throw new InvalidOperationException("Select at least one queue source before saving a recipe.");
        var recipe = new JobRecipe(JobRecipeJson.CurrentSchemaVersion, Guid.NewGuid().ToString("N"), name,
            locations.Select(path => new RecipeSource(Guid.NewGuid().ToString("N"), path)).ToImmutableArray(),
            new RecipePrintSettings(defaults.Copies, defaults.ColorMode, defaults.DuplexMode, defaults.Orientation,
                defaults.ScalingMode, defaults.Media));
        JobRecipeJson.Serialize(recipe);
        return new JobRecipeSnapshot(recipe.SchemaVersion, recipe.RecipeId, recipe.Name, recipe.Sources, recipe.Settings, recipe.Rules);
    }

    public void SaveRecipe(JobRecipeSnapshot snapshot)
    {
        if (_runStore is null) throw new InvalidOperationException("Local recipe storage is unavailable.");
        var existing = _runStore.GetRecipes();
        if (existing.Any(item => !string.Equals(item.Recipe.RecipeId, snapshot.RecipeId, StringComparison.Ordinal) &&
            string.Equals(item.Recipe.Name, snapshot.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A recipe with this name already exists.");
        _runStore.SaveRecipe(new JobRecipe(snapshot.SchemaVersion, snapshot.RecipeId, snapshot.Name, snapshot.Sources, snapshot.Settings)
            { Rules = snapshot.Rules });
        _ = GetTemplates();
    }

    public JobRecipeSnapshot GetRecipe(string recipeId)
    {
        var recipe = _runStore?.GetRecipes().SingleOrDefault(item => item.Recipe.RecipeId == recipeId)?.Recipe ??
            throw new InvalidOperationException("The selected recipe is no longer available.");
        return new JobRecipeSnapshot(recipe.SchemaVersion, recipe.RecipeId, recipe.Name, recipe.Sources, recipe.Settings, recipe.Rules);
    }

    public bool DeleteRecipe(string recipeId) => _runStore?.DeleteRecipe(recipeId) ??
        throw new InvalidOperationException("Local recipe storage is unavailable.");

    public void ImportRecipe(string path)
    {
        if (_runStore is null) throw new InvalidOperationException("Local recipe storage is unavailable.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > JobRecipeJson.MaximumRecipeBytes) throw new InvalidDataException("The recipe file exceeds the supported size limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var recipe = JobRecipeJson.Parse(bytes);
        var existing = _runStore.GetRecipes();
        if (existing.Any(item => string.Equals(item.Recipe.RecipeId, recipe.RecipeId, StringComparison.Ordinal)))
            throw new InvalidOperationException("This recipe is already saved. Existing recipes are never overwritten during import.");
        if (existing.Any(item => string.Equals(item.Recipe.Name, recipe.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A recipe with this name already exists. Rename it before importing.");
        _runStore.SaveRecipe(recipe);
        _ = GetTemplates();
    }

    public void ExportRecipe(string recipeId, string path)
    {
        var recipe = _runStore?.GetRecipes().SingleOrDefault(item => item.Recipe.RecipeId == recipeId)?.Recipe ??
            throw new InvalidOperationException("The selected recipe is no longer available.");
        var bytes = Encoding.UTF8.GetBytes(JobRecipeJson.Serialize(recipe));
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("The export location is invalid.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public IReadOnlyList<string> GetRecipeSourceLocations(string recipeId) =>
        _runStore?.GetRecipes().SingleOrDefault(item => item.Recipe.RecipeId == recipeId)?.Recipe.Sources.Select(source => source.Location).ToArray()
        ?? throw new InvalidOperationException("The selected recipe is no longer available.");
}
