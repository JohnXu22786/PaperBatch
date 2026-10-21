namespace PaperBatch.Desktop.Presentation;

internal sealed class DemoPresentationSources :
    IQueuePresentationSource,
    IEffectiveSettingsPresentationSource,
    IFinalPreviewPresentationSource,
    IPreflightPresentationSource,
    IHistoryPresentationSource,
    ITemplatePresentationSource
{
    public IReadOnlyList<QueueItemSnapshot> GetQueueItems() =>
    [
        new QueueItemSnapshot("sample-agenda", "Sample agenda.pdf", "4 pages · sample metadata", "Example row"),
        new QueueItemSnapshot("sample-roster", "Sample roster.xlsx", "2 pages · sample metadata", "Example row"),
        new QueueItemSnapshot("sample-brief", "Sample brief.docx", "3 pages · sample metadata", "Example row")
    ];

    public IReadOnlyList<EffectiveSettingSnapshot> GetEffectiveSettings(string queueItemId) =>
    [
        new EffectiveSettingSnapshot("Paper", "A4 · example", "Sample batch default; no printer was queried."),
        new EffectiveSettingSnapshot("Sides", "One-sided · example", "Illustrative setting only."),
        new EffectiveSettingSnapshot("Copies", "1 per sample row", "No job or page count was calculated."),
        new EffectiveSettingSnapshot("Scaling", "Shrink oversized · example", $"Demo selection: {queueItemId}.")
    ];

    public Task<PreviewSnapshot> GetPreviewAsync(string queueItemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (title, pageCount) = queueItemId switch
        {
            "sample-agenda" => ("Sample agenda", 4),
            "sample-roster" => ("Sample roster", 2),
            "sample-brief" => ("Sample brief", 3),
            _ => throw new ArgumentOutOfRangeException(nameof(queueItemId), queueItemId, "No sample preview is available for this row.")
        };

        return Task.FromResult(new PreviewSnapshot(
            queueItemId,
            $"Synthetic illustration for {title} · no document content rendered",
            1,
            pageCount));
    }

    public IReadOnlyList<PreflightFindingSnapshot> GetFindings(string queueItemId) =>
    [
        new PreflightFindingSnapshot("Sample input", "Not inspected", "This row is demo metadata; no file was opened."),
        new PreflightFindingSnapshot("Printer route", "Not checked", "The demo does not enumerate or contact printers."),
        new PreflightFindingSnapshot("Prepared output", "Not created", $"No PDF was made for {queueItemId}.")
    ];

    public IReadOnlyList<HistoryEntrySnapshot> GetHistoryEntries() =>
    [
        new HistoryEntrySnapshot("Sample run A", "Example history row · no pages submitted", "Example only"),
        new HistoryEntrySnapshot("Sample run B", "Example history row · no output created", "Example only")
    ];

    public IReadOnlyList<TemplateSnapshot> GetTemplates() =>
    [
        new TemplateSnapshot("Sample classroom packet", "Example recipe · not saved", "A4 · one-sided · sample values"),
        new TemplateSnapshot("Sample office handout", "Example recipe · not saved", "Letter · two-sided · sample values")
    ];
}
