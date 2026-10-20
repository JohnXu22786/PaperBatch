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
        var renderer = new RendererCoordinator(Array.Empty<IRendererAdapter>());
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();

        // These core services are the production composition. Until a qualified renderer and
        // prepared-run orchestration are supplied, the UI reports their absence instead of
        // fabricating prepared items or previews.
        _ = intake;
        _ = renderer;
        var sources = new ProductionPresentationSources(intake, renderer, cache);
        return new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("PRODUCTION MODE",
                "Files enter the shared source snapshot and preflight pipeline. Rendering, final preview, and submission remain unavailable without a renderer.", false));
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
    private readonly List<QueueItemSnapshot> _queue = [];

    public ProductionPresentationSources(SourceIntakeService? intake = null, RendererCoordinator? renderer = null, SourceSnapshotCache? cache = null)
    {
        _intake = intake;
        _renderer = renderer ?? new RendererCoordinator(Array.Empty<IRendererAdapter>());
        _cache = cache;
    }

    public bool IsAvailable => _intake is not null;

    public IReadOnlyList<QueueItemSnapshot> GetQueueItems() => _queue.ToArray();

    public async Task<IReadOnlyList<QueueItemSnapshot>> ImportAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        if (_intake is null) throw new InvalidOperationException("Source intake is not configured.");
        var inputs = paths.Select(path => new SourceInput(path)).ToArray();
        var prepared = await _intake.PrepareAsync(inputs, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var source in prepared.Items)
        {
            var id = source.Snapshot?.SnapshotId ?? Guid.NewGuid().ToString("N");
            var title = Path.GetFileName(source.SourcePath);
            var findings = source.Diagnostics.Select(diagnostic => new PreflightFindingSnapshot(
                diagnostic.Code.ToString(), diagnostic.Severity.ToString(), diagnostic.Message)).ToList();
            var status = source.IsReady ? "Preflight ready · renderer unavailable" : "Needs attention";
            var details = source.Snapshot is { } snapshot
                ? $"{source.Format} · {snapshot.LengthBytes:N0} bytes · SHA-256 {snapshot.Sha256[..12]}…"
                : source.Format?.ToString() ?? "Source was not captured";
            if (source.IsReady && source.Snapshot is { } readySnapshot && source.Format is { } format)
            {
                var result = await _renderer.RenderAsync(new RenderRequest(readySnapshot.SnapshotId, format,
                    RendererExecutionContext.InteractiveDesktop), Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                findings.Add(new PreflightFindingSnapshot("Renderer", result.Status.ToString(),
                    result.Message ?? "No rendered artifact was returned."));
            }
            if (source.Snapshot is { } captured && _cache is not null)
            {
                // No PreparedRun exists yet, so the UI must not keep a retained document copy
                // after reporting preflight and renderer status.
                try { _cache.Release(captured.SnapshotId); }
                catch (IOException) { findings.Add(new("Source cache", "Cleanup failed", "The temporary source snapshot could not be released.")); }
            }
            var item = new QueueItemSnapshot(id, title, details, status);
            _queue.Add(item);
            _findings[id] = findings;
        }
        return _queue.Skip(_queue.Count - prepared.Items.Length).ToArray();
    }

    private readonly Dictionary<string, List<PreflightFindingSnapshot>> _findings = new(StringComparer.Ordinal);
    public IReadOnlyList<EffectiveSettingSnapshot> GetEffectiveSettings(string queueItemId) =>
        [new("Renderer", "Unavailable", "No renderer is configured; no setting is treated as effective."),
         new("Printer", "Not selected", "The UI does not choose a default printer or silently substitute one.")];
    public PreviewSnapshot GetPreview(string queueItemId) =>
        new(queueItemId, "Final preview unavailable: no prepared artifact exists.", 0, 0);
    public IReadOnlyList<PreflightFindingSnapshot> GetFindings(string queueItemId) =>
        _findings.TryGetValue(queueItemId, out var findings) ? findings.ToArray() :
            [new("Rendering", "Unavailable", "No renderer is configured; source content was not rendered.")];
    public IReadOnlyList<HistoryEntrySnapshot> GetHistoryEntries() => [];
    public IReadOnlyList<TemplateSnapshot> GetTemplates() => [];
}
