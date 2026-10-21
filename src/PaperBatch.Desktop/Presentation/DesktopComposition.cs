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
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();

        // The registered renderers persist PDF and supported JPEG/PNG artifacts. There is not
        // yet a prepared-run coordinator, so the UI keeps final preview and submission unavailable.
        _ = intake;
        _ = renderer;
        var sources = new ProductionPresentationSources(intake, renderer, cache, rendererAvailable: true);
        return new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("PRODUCTION MODE",
                "PDF pass-through and supported JPEG/PNG artifact rendering are available. Prepared-run orchestration, final preview, LibreOffice conversion, and submission remain unavailable.", false));
    }
}

internal sealed class ProductionPresentationSources :
    IQueueImportPresentationSource,
    IEffectiveSettingsPresentationSource,
    IFinalPreviewPresentationSource,
    IPreflightPresentationSource,
    IHistoryPresentationSource,
    ITemplatePresentationSource
{
    private readonly SourceIntakeService? _intake;
    private readonly RendererCoordinator _renderer;
    private readonly SourceSnapshotCache? _cache;
    private readonly bool _rendererAvailable;
    private readonly List<QueueItemSnapshot> _queue = [];

    public ProductionPresentationSources(SourceIntakeService? intake = null, RendererCoordinator? renderer = null,
        SourceSnapshotCache? cache = null, bool rendererAvailable = false)
    {
        _intake = intake;
        _renderer = renderer ?? new RendererCoordinator(Array.Empty<IRendererAdapter>());
        _cache = cache;
        _rendererAvailable = rendererAvailable;
    }

    public bool IsAvailable => _intake is not null;

    public IReadOnlyList<QueueItemSnapshot> GetQueueItems() => _queue.ToArray();

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
    public PreviewSnapshot GetPreview(string queueItemId) =>
        new(queueItemId, "Final preview unavailable: no prepared artifact exists.", 0, 0);
    public IReadOnlyList<PreflightFindingSnapshot> GetFindings(string queueItemId) =>
        _findings.TryGetValue(queueItemId, out var findings) ? findings.ToArray() :
            [new("Rendering", _rendererAvailable ? "Not run" : "Unavailable", _rendererAvailable
                ? "No preflight or artifact result exists for this queue item."
                : "No renderer is configured; source content was not rendered.")];
    public IReadOnlyList<HistoryEntrySnapshot> GetHistoryEntries() => [];
    public IReadOnlyList<TemplateSnapshot> GetTemplates() => [];
}
