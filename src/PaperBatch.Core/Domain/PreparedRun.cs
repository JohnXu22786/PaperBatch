using System.Collections.Immutable;

namespace PaperBatch.Core.Domain;

public enum ColorMode
{
    Monochrome,
    Color
}

public enum DuplexMode
{
    Simplex,
    LongEdge,
    ShortEdge
}

public enum Orientation
{
    Auto,
    Portrait,
    Landscape
}

public enum ScalingMode
{
    ShrinkToFit,
    ActualSize
}

public enum RunState
{
    Preparing,
    Ready,
    InProgress,
    Submitting,
    AcceptedByOs,
    OsProcessing,
    OsReportsComplete,
    FailedBeforeSubmit,
    CancelRequested,
    CancelObserved,
    NeedsReviewUnknown,
    UserConfirmedPaperOutput
}

public enum JobState
{
    Ready,
    Submitting,
    AcceptedByOs,
    OsProcessing,
    OsReportsComplete,
    FailedBeforeSubmit,
    CancelRequested,
    CancelObserved,
    NeedsReviewUnknown,
    UserConfirmedPaperOutput
}

public sealed record SourceSnapshot(
    string SnapshotId,
    string Sha256,
    long LengthBytes,
    DateTimeOffset CapturedAtUtc);

public sealed record RendererCapabilities
{
    public RendererCapabilities(int contractVersion, IEnumerable<string> supportedMediaTypes)
    {
        ContractVersion = contractVersion;
        SupportedMediaTypes = supportedMediaTypes?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(supportedMediaTypes));
    }

    public int ContractVersion { get; init; }
    public ImmutableArray<string> SupportedMediaTypes { get; init; }
}

public sealed record RendererResult(
    string RendererId,
    string RendererVersion,
    string ArtifactId,
    string ArtifactSha256,
    long ArtifactLengthBytes,
    int PageCount,
    RendererCapabilities Capabilities);

/// <summary>Identity of the immutable, fully composed PDF for this prepared run.</summary>
public sealed record FinalPdfArtifact(string ArtifactId, string Sha256, long LengthBytes, int PageCount);

public readonly record struct PageMapping(int SourcePageNumber, int RenderedPageNumber);

public sealed record PrintSettings(
    int Copies,
    ColorMode ColorMode,
    DuplexMode DuplexMode,
    string Media,
    Orientation Orientation,
    ScalingMode ScalingMode,
    string? PrinterId = null,
    string? TrayId = null);

public sealed record PreparedItem
{
    public PreparedItem(
        string itemId,
        SourceSnapshot source,
        RendererResult render,
        IEnumerable<PageMapping> pageMap,
        PrintSettings printSettings)
    {
        ItemId = itemId;
        Source = source;
        Render = render;
        PageMap = pageMap?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(pageMap));
        PrintSettings = printSettings;
    }

    public string ItemId { get; init; }
    public SourceSnapshot Source { get; init; }
    public RendererResult Render { get; init; }
    public ImmutableArray<PageMapping> PageMap { get; init; }
    public PrintSettings PrintSettings { get; init; }
}

public sealed record CompatibleSegment
{
    public CompatibleSegment(
        string segmentId,
        int index,
        IEnumerable<string> itemIds,
        string compatibilityKey,
        PrintSettings printSettings,
        IEnumerable<int>? finalPageNumbers = null)
    {
        SegmentId = segmentId;
        Index = index;
        ItemIds = itemIds?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(itemIds));
        CompatibilityKey = compatibilityKey;
        PrintSettings = printSettings;
        FinalPageNumbers = finalPageNumbers?.ToImmutableArray() ?? [];
    }

    public string SegmentId { get; init; }
    public int Index { get; init; }
    public ImmutableArray<string> ItemIds { get; init; }
    public string CompatibilityKey { get; init; }
    public PrintSettings PrintSettings { get; init; }
    /// <summary>Ordered 1-based pages in the composed final PDF owned by this segment; empty only for legacy metadata.</summary>
    public ImmutableArray<int> FinalPageNumbers { get; init; }
}

/// <summary>Immutable, engine-neutral input to a later coordinator submission.</summary>
public sealed record PreparedRun
{
    private PreparedRun(
        string runId,
        DateTimeOffset createdAtUtc,
        string snapshotFingerprint,
        ImmutableArray<PreparedItem> items,
        ImmutableArray<CompatibleSegment> segments,
        FinalPdfArtifact? finalArtifact)
    {
        RunId = runId;
        CreatedAtUtc = createdAtUtc;
        SnapshotFingerprint = snapshotFingerprint.ToLowerInvariant();
        Items = items;
        Segments = segments;
        FinalArtifact = finalArtifact;
    }

    public string RunId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string SnapshotFingerprint { get; }
    public ImmutableArray<PreparedItem> Items { get; }
    public ImmutableArray<CompatibleSegment> Segments { get; }
    public FinalPdfArtifact? FinalArtifact { get; }

    public static PreparedRun Create(
        string runId,
        DateTimeOffset createdAtUtc,
        string snapshotFingerprint,
        IEnumerable<PreparedItem> items,
        IEnumerable<CompatibleSegment> segments,
        FinalPdfArtifact? finalArtifact = null)
    {
        ValidateIdentifier(runId, nameof(runId));
        ValidateSha256(snapshotFingerprint, nameof(snapshotFingerprint));
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(segments);

        var immutableItems = items.ToImmutableArray();
        var immutableSegments = segments.ToImmutableArray();
        if (immutableItems.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A prepared run must contain at least one item.", nameof(items));
        }

        if (immutableSegments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A prepared run must contain at least one compatible segment.", nameof(segments));
        }

        var itemsById = new Dictionary<string, PreparedItem>(StringComparer.Ordinal);
        var normalizedItemsBuilder = ImmutableArray.CreateBuilder<PreparedItem>(immutableItems.Length);
        foreach (var item in immutableItems)
        {
            ValidateItem(item);
            var normalizedItem = item with
            {
                Source = item.Source with
                {
                    Sha256 = item.Source.Sha256.ToLowerInvariant(),
                    CapturedAtUtc = item.Source.CapturedAtUtc.ToUniversalTime()
                },
                Render = item.Render with { ArtifactSha256 = item.Render.ArtifactSha256.ToLowerInvariant() }
            };
            if (!itemsById.TryAdd(normalizedItem.ItemId, normalizedItem))
            {
                throw new ArgumentException($"Duplicate prepared item ID '{normalizedItem.ItemId}'.", nameof(items));
            }

            normalizedItemsBuilder.Add(normalizedItem);
        }

        var normalizedItems = normalizedItemsBuilder.MoveToImmutable();

        var segmentIds = new HashSet<string>(StringComparer.Ordinal);
        var flattenedItemIds = new List<string>();
        for (var index = 0; index < immutableSegments.Length; index++)
        {
            var segment = immutableSegments[index];
            if (segment is null)
            {
                throw new ArgumentException("A compatible segment cannot be null.", nameof(segments));
            }

            ValidateIdentifier(segment.SegmentId, nameof(segments));
            if (!segmentIds.Add(segment.SegmentId))
            {
                throw new ArgumentException($"Duplicate segment ID '{segment.SegmentId}'.", nameof(segments));
            }

            if (segment.Index != index)
            {
                throw new ArgumentException("Segment indices must be contiguous and preserve submission order.", nameof(segments));
            }

            if (segment.ItemIds.IsDefaultOrEmpty)
            {
                throw new ArgumentException("A compatible segment must contain at least one item.", nameof(segments));
            }
            if (segment.ItemIds.Distinct(StringComparer.Ordinal).Count() != segment.ItemIds.Length)
            {
                throw new ArgumentException("A compatible segment cannot contain the same item more than once.", nameof(segments));
            }

            if (string.IsNullOrWhiteSpace(segment.CompatibilityKey) || segment.CompatibilityKey.Length > 256)
            {
                throw new ArgumentException("A compatible segment requires a bounded compatibility key.", nameof(segments));
            }

            ValidatePrintSettings(segment.PrintSettings, nameof(segments));
            if (!segment.FinalPageNumbers.IsDefault)
            {
                var previousPage = 0;
                foreach (var pageNumber in segment.FinalPageNumbers)
                {
                    if (pageNumber <= previousPage || (previousPage != 0 && pageNumber != previousPage + 1) ||
                        finalArtifact is null || pageNumber > finalArtifact.PageCount)
                        throw new ArgumentException("Each segment must own one contiguous range of ordered, in-range final artifact pages.", nameof(segments));
                    previousPage = pageNumber;
                }
            }
            foreach (var itemId in segment.ItemIds)
            {
                if (!itemsById.TryGetValue(itemId, out var item))
                {
                    throw new ArgumentException($"Segment references unknown item '{itemId}'.", nameof(segments));
                }

                if (item.PrintSettings != segment.PrintSettings)
                {
                    throw new ArgumentException("Items in a compatible segment must have identical effective print settings.", nameof(segments));
                }

                flattenedItemIds.Add(itemId);
            }
        }

        if (!flattenedItemIds.ToHashSet(StringComparer.Ordinal).SetEquals(itemsById.Keys))
        {
            throw new ArgumentException("Segments must cover every prepared item at least once.", nameof(segments));
        }

        if (finalArtifact is not null)
        {
            ValidateIdentifier(finalArtifact.ArtifactId, nameof(finalArtifact));
            ValidateSha256(finalArtifact.Sha256, nameof(finalArtifact));
            if (finalArtifact.LengthBytes <= 0 || finalArtifact.PageCount <= 0 ||
                !string.Equals(finalArtifact.ArtifactId, "artifact_" + finalArtifact.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
                throw new ArgumentException("The final PDF artifact identity, length, and page count are invalid.", nameof(finalArtifact));
        }

        var mappedSegments = immutableSegments.Where(segment => !segment.FinalPageNumbers.IsDefaultOrEmpty).ToArray();
        if (mappedSegments.Length > 0)
        {
            if (mappedSegments.Length != immutableSegments.Length || finalArtifact is null)
                throw new ArgumentException("Final page ownership must be present for every segment of a run with a final artifact.", nameof(segments));
            var ownedPages = mappedSegments.SelectMany(segment => segment.FinalPageNumbers).Order().ToArray();
            if (ownedPages.Length != finalArtifact.PageCount ||
                ownedPages.Where((pageNumber, index) => pageNumber != index + 1).Any())
                throw new ArgumentException("Final page ownership must cover every composed page exactly once.", nameof(segments));
        }

        return new PreparedRun(
            runId,
            createdAtUtc.ToUniversalTime(),
            snapshotFingerprint,
            normalizedItems,
            immutableSegments,
            finalArtifact is null ? null : finalArtifact with { Sha256 = finalArtifact.Sha256.ToLowerInvariant() });
    }

    private static void ValidateItem(PreparedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateIdentifier(item.ItemId, nameof(item));
        ArgumentNullException.ThrowIfNull(item.Source);
        ValidateIdentifier(item.Source.SnapshotId, nameof(item));
        ValidateSha256(item.Source.Sha256, nameof(item));
        if (item.Source.LengthBytes < 0)
        {
            throw new ArgumentException("Snapshot length cannot be negative.", nameof(item));
        }

        ArgumentNullException.ThrowIfNull(item.Render);
        ValidateIdentifier(item.Render.RendererId, nameof(item));
        ValidateBoundedText(item.Render.RendererVersion, 128, nameof(item));
        ValidateIdentifier(item.Render.ArtifactId, nameof(item));
        ValidateSha256(item.Render.ArtifactSha256, nameof(item));
        if (item.Render.ArtifactLengthBytes < 0 || item.Render.PageCount <= 0)
        {
            throw new ArgumentException("Rendered artifacts require a nonnegative size and at least one page.", nameof(item));
        }

        ArgumentNullException.ThrowIfNull(item.Render.Capabilities);
        if (item.Render.Capabilities.ContractVersion <= 0 || item.Render.Capabilities.SupportedMediaTypes.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Renderer capabilities require a positive contract version and at least one media type.", nameof(item));
        }

        foreach (var mediaType in item.Render.Capabilities.SupportedMediaTypes)
        {
            if (string.IsNullOrWhiteSpace(mediaType) || mediaType.Length > 128 || mediaType.Any(char.IsControl) || !IsValidMediaType(mediaType))
            {
                throw new ArgumentException("Renderer media types must be bounded MIME type identifiers.", nameof(item));
            }
        }

        if (item.PageMap.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A prepared item requires a page mapping.", nameof(item));
        }

        var sourcePages = new HashSet<int>();
        var renderedPages = new HashSet<int>();
        foreach (var mapping in item.PageMap)
        {
            if (mapping.SourcePageNumber <= 0 || mapping.RenderedPageNumber <= 0 || mapping.RenderedPageNumber > item.Render.PageCount ||
                !sourcePages.Add(mapping.SourcePageNumber) || !renderedPages.Add(mapping.RenderedPageNumber))
            {
                throw new ArgumentException("Page mappings must use unique positive page numbers within the rendered artifact.", nameof(item));
            }
        }

        ValidatePrintSettings(item.PrintSettings, nameof(item));
    }

    private static bool IsValidMediaType(string value)
    {
        var slashIndex = value.IndexOf('/');
        return slashIndex > 0 && slashIndex < value.Length - 1 && slashIndex == value.LastIndexOf('/') &&
            IsMimeToken(value.AsSpan(0, slashIndex)) && IsMimeToken(value.AsSpan(slashIndex + 1));
    }

    private static bool IsMimeToken(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && "!#$%&'*+-.^_`|~".IndexOf(character) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidatePrintSettings(PrintSettings settings, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Copies is < 1 or > 999 || string.IsNullOrWhiteSpace(settings.Media) || settings.Media.Length > 128 || settings.Media.Any(char.IsControl) ||
            !Enum.IsDefined(settings.ColorMode) || !Enum.IsDefined(settings.DuplexMode) || !Enum.IsDefined(settings.Orientation) || !Enum.IsDefined(settings.ScalingMode))
        {
            throw new ArgumentException("Print settings require valid enum values, 1–999 copies, and a bounded media identifier.", parameterName);
        }

        ValidateOptionalIdentifier(settings.PrinterId, "printer ID", parameterName);
        ValidateOptionalIdentifier(settings.TrayId, "tray ID", parameterName);
    }

    private static void ValidateOptionalIdentifier(string? value, string description, string parameterName)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)))
        {
            throw new ArgumentException($"Optional {description} must be bounded and contain no control characters.", parameterName);
        }
    }

    private static void ValidateIdentifier(string value, string parameterName) => ValidateBoundedText(value, 128, parameterName);

    private static void ValidateBoundedText(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("Value must be nonempty, bounded, and contain no control characters.", parameterName);
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 values must contain exactly 64 hexadecimal characters.", parameterName);
        }
    }
}

public static class RunStateAggregator
{
    public static RunState Aggregate(IEnumerable<JobState> jobStates)
    {
        ArgumentNullException.ThrowIfNull(jobStates);
        var states = jobStates.ToArray();
        if (states.Length == 0)
        {
            throw new ArgumentException("A run must contain at least one job state.", nameof(jobStates));
        }

        foreach (var state in states)
        {
            if (!Enum.IsDefined(state))
            {
                throw new ArgumentOutOfRangeException(nameof(jobStates), state, "Every job state must be defined.");
            }
        }

        if (states.Any(state => state != states[0])) return RunState.InProgress;

        return states[0] switch
        {
            JobState.Ready => RunState.Ready,
            JobState.Submitting => RunState.Submitting,
            JobState.AcceptedByOs => RunState.AcceptedByOs,
            JobState.OsProcessing => RunState.OsProcessing,
            JobState.OsReportsComplete => RunState.OsReportsComplete,
            JobState.FailedBeforeSubmit => RunState.FailedBeforeSubmit,
            JobState.CancelRequested => RunState.CancelRequested,
            JobState.CancelObserved => RunState.CancelObserved,
            JobState.NeedsReviewUnknown => RunState.NeedsReviewUnknown,
            JobState.UserConfirmedPaperOutput => RunState.UserConfirmedPaperOutput,
            _ => throw new ArgumentOutOfRangeException(nameof(jobStates), states[0], "Every job state must be defined.")
        };
    }
}

public static class JobStateTransitions
{
    public static bool IsAllowed(JobState current, JobState next) => (current, next) switch
    {
        (JobState.Ready, JobState.Submitting) => true,
        (JobState.FailedBeforeSubmit, JobState.Submitting) => true,
        (JobState.Submitting, JobState.AcceptedByOs or JobState.FailedBeforeSubmit or JobState.NeedsReviewUnknown) => true,
        (JobState.AcceptedByOs, JobState.OsProcessing or JobState.OsReportsComplete or JobState.CancelRequested or JobState.NeedsReviewUnknown) => true,
        (JobState.OsProcessing, JobState.OsReportsComplete or JobState.CancelRequested or JobState.NeedsReviewUnknown) => true,
        (JobState.CancelRequested, JobState.CancelObserved or JobState.OsProcessing or JobState.OsReportsComplete or JobState.NeedsReviewUnknown) => true,
        (JobState.NeedsReviewUnknown, JobState.AcceptedByOs or JobState.OsProcessing or JobState.OsReportsComplete or JobState.FailedBeforeSubmit or JobState.CancelObserved or JobState.UserConfirmedPaperOutput) => true,
        (JobState.OsReportsComplete, JobState.UserConfirmedPaperOutput) => true,
        (JobState.CancelObserved, JobState.UserConfirmedPaperOutput) => true,
        _ => false
    };
}
