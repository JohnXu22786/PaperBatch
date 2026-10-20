namespace PaperBatch.Desktop.Presentation;

internal enum ShellSection
{
    Queue,
    History,
    Templates
}

internal sealed record QueueItemSnapshot(string Id, string Title, string Details, string Status);

internal sealed record EffectiveSettingSnapshot(string Name, string Value, string Explanation);

internal sealed record PreviewSnapshot(string ItemId, string Description, int PageNumber, int PageCount);

internal sealed record PreflightFindingSnapshot(string Title, string Status, string Detail);

internal sealed record HistoryEntrySnapshot(string Title, string Summary, string Status);

internal sealed record TemplateSnapshot(string Name, string Summary, string SettingsSummary);

internal interface IQueuePresentationSource
{
    IReadOnlyList<QueueItemSnapshot> GetQueueItems();
}

internal interface IEffectiveSettingsPresentationSource
{
    IReadOnlyList<EffectiveSettingSnapshot> GetEffectiveSettings(string queueItemId);
}

internal interface IFinalPreviewPresentationSource
{
    PreviewSnapshot GetPreview(string queueItemId);
}

internal interface IPreflightPresentationSource
{
    IReadOnlyList<PreflightFindingSnapshot> GetFindings(string queueItemId);
}

internal interface IHistoryPresentationSource
{
    IReadOnlyList<HistoryEntrySnapshot> GetHistoryEntries();
}

internal interface ITemplatePresentationSource
{
    IReadOnlyList<TemplateSnapshot> GetTemplates();
}
