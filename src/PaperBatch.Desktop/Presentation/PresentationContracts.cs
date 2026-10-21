using Avalonia.Media.Imaging;
using PaperBatch.Core.Domain;

namespace PaperBatch.Desktop.Presentation;

internal enum ShellSection
{
    Queue,
    History,
    Templates
}

internal sealed record QueueItemSnapshot(string Id, string Title, string Details, string Status);

internal sealed record EffectiveSettingSnapshot(string Name, string Value, string Explanation);

internal sealed record PreviewSnapshot(string ItemId, string Description, int PageNumber, int PageCount,
    Bitmap? PageImage = null, string? ArtifactId = null, string? ArtifactSha256 = null);

internal sealed record PreflightFindingSnapshot(string Title, string Status, string Detail);

internal sealed record HistoryEntrySnapshot(string Title, string Summary, string Status);

internal sealed record TemplateSnapshot(string Name, string Summary, string SettingsSummary);

internal sealed record WorkspaceStatusSnapshot(string Title, string Detail, bool IsDemo, bool InitializationFailed = false);

internal interface IQueuePresentationSource
{
    IReadOnlyList<QueueItemSnapshot> GetQueueItems();
}

internal interface IQueueEditingPresentationSource : IQueuePresentationSource
{
    void Move(string queueItemId, int delta);
    void Remove(string queueItemId);
}

internal interface IQueueImportPresentationSource : IQueuePresentationSource
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<QueueItemSnapshot>> ImportAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default);
}

internal interface IEffectiveSettingsPresentationSource
{
    IReadOnlyList<EffectiveSettingSnapshot> GetEffectiveSettings(string queueItemId);
}

internal interface IFinalPreviewPresentationSource
{
    Task<PreviewSnapshot> GetPreviewAsync(string queueItemId, CancellationToken cancellationToken = default);
}

internal interface IPreparedArtifactPresentationSource
{
    event Action<string>? PreparedArtifactChanged;
    void RegisterPreparedRun(string queueItemId, PreparedRun run);
    void InvalidatePreparedRun(string queueItemId);
}

internal interface IPreflightPresentationSource
{
    IReadOnlyList<PreflightFindingSnapshot> GetFindings(string queueItemId);
}

internal interface IHistoryPresentationSource
{
    IReadOnlyList<HistoryEntrySnapshot> GetHistoryEntries();
    string? HistoryReadError { get; }
}

internal interface ITemplatePresentationSource
{
    IReadOnlyList<TemplateSnapshot> GetTemplates();
    string? TemplateReadError { get; }
}
