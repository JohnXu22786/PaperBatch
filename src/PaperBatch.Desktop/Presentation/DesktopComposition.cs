using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Rendering;

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
                    $"Local application services could not initialize ({exception.GetType().Name}). No files were loaded and no jobs were created.", false));
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
        IPdfArtifactPreviewRenderer? previewRenderer = null;
        var pdfiumPath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_PATH");
        var pdfiumHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_SHA256");
        if (!string.IsNullOrWhiteSpace(pdfiumPath) && !string.IsNullOrWhiteSpace(pdfiumHash))
            previewRenderer = new PdfiumArtifactInspector(pdfiumPath, pdfiumHash);
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();

        // Renderer artifacts are available. The desktop composition does not invent print settings
        // or claim that uninspected interactive PDF content is safe for composition.
        _ = intake;
        _ = renderer;
        var sources = new ProductionPresentationSources(intake, renderer, cache, rendererAvailable: true,
            artifacts: artifactStore, previewRenderer: previewRenderer);
        return new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("PRODUCTION MODE",
                "PDF pass-through and supported JPEG/PNG artifact rendering are available. Final preview needs a registered prepared run and explicitly configured PDFium runtime; LibreOffice conversion and production print submission remain unavailable.", false));
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
    ITemplatePresentationSource
{
    private readonly SourceIntakeService? _intake;
    private readonly RendererCoordinator _renderer;
    private readonly SourceSnapshotCache? _cache;
    private readonly bool _rendererAvailable;
    private readonly RenderArtifactStore? _artifacts;
    private readonly IPdfArtifactPreviewRenderer? _previewRenderer;
    private readonly Dictionary<string, PreparedRun> _preparedRunsByQueueItem = new(StringComparer.Ordinal);
    private readonly List<QueueItemSnapshot> _queue = [];

    public event Action<string>? PreparedArtifactChanged;

    public ProductionPresentationSources(SourceIntakeService? intake = null, RendererCoordinator? renderer = null,
        SourceSnapshotCache? cache = null, bool rendererAvailable = false,
        RenderArtifactStore? artifacts = null, IPdfArtifactPreviewRenderer? previewRenderer = null)
    {
        _intake = intake;
        _renderer = renderer ?? new RendererCoordinator(Array.Empty<IRendererAdapter>());
        _cache = cache;
        _rendererAvailable = rendererAvailable;
        _artifacts = artifacts;
        _previewRenderer = previewRenderer;
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

    public void Remove(string queueItemId) => _queue.RemoveAll(item => string.Equals(item.Id, queueItemId, StringComparison.Ordinal));

    public async Task<IReadOnlyList<QueueItemSnapshot>> ImportAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        if (_intake is null) throw new InvalidOperationException("Source intake is not configured.");
        var inputs = paths.Select(path => new SourceInput(path)).ToArray();
        var prepared = await _intake.PrepareAsync(inputs, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var source in prepared.Items)
            {
                var id = source.Snapshot?.SnapshotId ?? Guid.NewGuid().ToString("N");
                var title = Path.GetFileName(source.SourcePath);
                var findings = source.Diagnostics.Select(diagnostic => new PreflightFindingSnapshot(
                    diagnostic.Code.ToString(), diagnostic.Severity.ToString(), diagnostic.Message)).ToList();
                var status = source.IsReady ? "Preflight ready · prepared run unavailable" : "Needs attention";
                var details = source.Snapshot is { } snapshot
                    ? $"{source.Format} · {snapshot.LengthBytes:N0} bytes · SHA-256 {snapshot.Sha256[..12]}…"
                    : source.Format?.ToString() ?? "Source was not captured";
                if (source.IsReady && source.Snapshot is { } readySnapshot && source.Format is { } format)
                {
                    var result = await Task.Run(() => _renderer.RenderAsync(new RenderRequest(readySnapshot.SnapshotId, format,
                        RendererExecutionContext.BackgroundWorker), TimeSpan.FromMinutes(2), cancellationToken), cancellationToken).ConfigureAwait(false);
                    findings.Add(new PreflightFindingSnapshot("Renderer", result.Status.ToString(),
                        result.Message ?? (result.Artifact is { PageCount: 0 }
                            ? $"PDF artifact {result.Artifact.ArtifactId} persisted; page count is not reported."
                            : result.Artifact is { } rendered
                                ? $"Rendered artifact {rendered.ArtifactId} persisted with {rendered.PageCount} page(s)."
                                : "No rendered artifact was returned.")));
                }
                var item = new QueueItemSnapshot(id, title, details, status);
                _queue.Add(item);
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
                        if (_findings.TryGetValue(snapshotId, out var findings))
                            findings.Add(new("Source cache", "Cleanup failed", "The temporary source snapshot could not be released."));
                    }
                }
            }
        }
    }

    private readonly Dictionary<string, List<PreflightFindingSnapshot>> _findings = new(StringComparer.Ordinal);
    public IReadOnlyList<EffectiveSettingSnapshot> GetEffectiveSettings(string queueItemId) =>
        [new("Renderer", _rendererAvailable ? "Partial" : "Unavailable", _rendererAvailable
            ? "Background PDF pass-through and JPEG/PNG artifact rendering are configured; other formats and prepared-run orchestration are unavailable."
            : "No renderer is configured; no setting is treated as effective."),
         new("Printer", "Not selected", "The UI does not choose a default printer or silently substitute one.")];
    public void RegisterPreparedRun(string queueItemId, PreparedRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueItemId);
        ArgumentNullException.ThrowIfNull(run);
        if (run.FinalArtifact is null) throw new InvalidOperationException("A prepared run without a final PDF cannot be previewed.");
        _preparedRunsByQueueItem[queueItemId] = run;
        PreparedArtifactChanged?.Invoke(queueItemId);
    }

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
    public IReadOnlyList<HistoryEntrySnapshot> GetHistoryEntries() => [];
    public IReadOnlyList<TemplateSnapshot> GetTemplates() => [];
}
