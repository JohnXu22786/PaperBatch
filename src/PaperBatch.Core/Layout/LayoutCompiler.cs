using System.Collections.Immutable;
using System.Globalization;

namespace PaperBatch.Core.Layout;

public enum CopyOrder { CollatedPackets, PerDocument }
public enum NUpMode { One = 1, Two = 2, Four = 4 }
public enum PageSide { Front, Back }
public enum LayoutPageKind { Source, Cover, Separator, Blank }
public enum FitMode { ShrinkToFit, Fit, ActualSize }

public sealed record PageSize(double WidthPoints, double HeightPoints)
{
    public PageSize Validate(string name)
    {
        if (!double.IsFinite(WidthPoints) || !double.IsFinite(HeightPoints) || WidthPoints <= 0 || HeightPoints <= 0 || WidthPoints > 14400 || HeightPoints > 14400)
            throw new ArgumentOutOfRangeException(name, "Page dimensions must be finite, positive, and at most 14,400 points.");
        return this;
    }
}

public sealed record LayoutDocumentSettings(int? Copies = null, NUpMode? NUp = null, FitMode? Fit = null,
    PageSize? SheetSize = null, ImmutableArray<PageRange> Ranges = default);

public sealed record LayoutDocument(string ItemId, ImmutableArray<PageSize> Pages, string Media, string? PrinterId = null,
    string? TrayId = null, LayoutDocumentSettings? Settings = null, string? SourceName = null)
{
    public LayoutDocument(string itemId, IEnumerable<PageSize> pages, string media, string? printerId = null,
        string? trayId = null, LayoutDocumentSettings? settings = null, string? sourceName = null)
        : this(itemId, pages?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(pages)), media, printerId, trayId, settings, sourceName) { }
}

public sealed record PageRange(int FirstPage, int LastPage);

public sealed record LayoutOptions(
    int Copies = 1,
    CopyOrder CopyOrder = CopyOrder.CollatedPackets,
    NUpMode NUp = NUpMode.One,
    bool Duplex = false,
    bool SeparateFilesOnFront = false,
    bool ReverseOrder = false,
    FitMode Fit = FitMode.ShrinkToFit,
    PageSize? SheetSize = null,
    ImmutableArray<PageRange> Ranges = default,
    bool AddFrontCover = false,
    bool AddSeparatorsBetweenFiles = false,
    bool SeparatePacketsOnFront = false,
    int MaximumOutputPages = 1_000_000);

public sealed record PagePlacement(
    string ItemId,
    int SourcePageNumber,
    int CopyNumber,
    int Slot,
    PageSize SourceSize,
    PageSize TargetSize,
    double Scale,
    bool RotatedClockwise = false,
    LayoutPageKind Kind = LayoutPageKind.Source,
    PageRectangle? TargetBounds = null);

/// <summary>One physical face in the deterministic print plan; blank faces have no placements.</summary>
public sealed record PhysicalPage(int Number, int SheetNumber, PageSide Side, string SegmentKey, ImmutableArray<PagePlacement> Placements, PageSize? OutputSize = null)
{
    public bool IsBlank => Placements.IsDefaultOrEmpty;
}

public sealed record LayoutDiagnostic(string Code, string Message);
public sealed record PageRectangle(double XPoints, double YPoints, double WidthPoints, double HeightPoints);
public sealed record LayoutCompilation(ImmutableArray<PhysicalPage> Pages, ImmutableArray<LayoutDiagnostic> Diagnostics, int SheetEstimate)
{
    public bool Succeeded => Diagnostics.IsDefaultOrEmpty;
}

/// <summary>Immutable input PDF bytes keyed by the matching layout item identifier.</summary>
public sealed record PdfCompositionSource(string ItemId, ReadOnlyMemory<byte> PdfBytes, bool StaticPageContentVerified = false);

/// <summary>Composes an already compiled page map using the selected pinned engine adapter.</summary>
public interface IPageCompositionAdapter
{
    /// <summary>Maximum byte length of one source PDF accepted by this compositor.</summary>
    long MaximumSourceBytes => long.MaxValue;

    Task ComposePdfAsync(LayoutCompilation compilation, IEnumerable<PdfCompositionSource> sources, Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>Deterministic page selection, copy expansion, N-up, duplex, and contiguous segment mapping.</summary>
public static class LayoutCompiler
{
    private const int MaxCopies = 999;

    public static LayoutCompilation Compile(IEnumerable<LayoutDocument> documents, LayoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(options);
        var docs = documents.ToImmutableArray();
        if (docs.IsDefaultOrEmpty) return Failed("NoDocuments", "At least one prepared document is required.");
        if (options.Copies is < 1 or > MaxCopies) return Failed("InvalidCopies", "Copies must be between 1 and 999.");
        if (!Enum.IsDefined(options.CopyOrder) || !Enum.IsDefined(options.NUp) || !Enum.IsDefined(options.Fit)) return Failed("InvalidSetting", "A layout setting is unsupported.");
        if (options.MaximumOutputPages is < 1 or > 10_000_000) return Failed("InvalidOutputLimit", "The output page limit is invalid.");
        if (options.SheetSize is not null)
        {
            try { options.SheetSize.Validate(nameof(options.SheetSize)); }
            catch (ArgumentOutOfRangeException) { return Failed("InvalidSheetSize", "The selected sheet dimensions are invalid."); }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in docs)
        {
            if (doc is null || string.IsNullOrWhiteSpace(doc.ItemId) || doc.ItemId.Length > 128 || !ids.Add(doc.ItemId) || string.IsNullOrWhiteSpace(doc.Media) || doc.Media.Length > 128 || doc.Pages.IsDefaultOrEmpty)
                return Failed("InvalidDocument", "Documents require unique IDs, bounded media, and at least one page.");
            foreach (var page in doc.Pages)
            {
                if (page is null) return Failed("InvalidPageSize", "A source page has invalid dimensions.");
                try { page.Validate(nameof(documents)); }
                catch (ArgumentOutOfRangeException) { return Failed("InvalidPageSize", "A source page has invalid dimensions."); }
            }
            if (doc.Settings is { } documentSettings)
            {
                if (documentSettings.SheetSize is { } sheetSize)
                {
                    try { sheetSize.Validate(nameof(documents)); }
                    catch (ArgumentOutOfRangeException) { return Failed("InvalidSheetSize", "A document sheet size is invalid."); }
                }
                if (documentSettings.Copies is < 1 or > MaxCopies ||
                    documentSettings.NUp is { } nupValue && !Enum.IsDefined(nupValue) ||
                    documentSettings.Fit is { } fitValue && !Enum.IsDefined(fitValue))
                    return Failed("InvalidSetting", "A per-document layout setting is invalid.");
            }
        }

        var selected = new List<(int Doc, int Page)>();
        for (var d = 0; d < docs.Length; d++)
        {
            var documentRanges = docs[d].Settings?.Ranges ?? default;
            var ranges = !documentRanges.IsDefaultOrEmpty ? documentRanges : options.Ranges.IsDefaultOrEmpty ? [new PageRange(1, docs[d].Pages.Length)] : options.Ranges;
            foreach (var range in ranges)
            {
                var sourceName = docs[d].SourceName ?? docs[d].ItemId;
                if (range is null)
                    return Failed("InvalidRange", $"The selected page range for {sourceName} is invalid; valid pages are 1-{docs[d].Pages.Length}.");
                if (range.FirstPage < 1 || range.LastPage < range.FirstPage || range.LastPage > docs[d].Pages.Length)
                    return Failed("InvalidRange", $"Page range {range.FirstPage}-{range.LastPage} for {sourceName} is invalid; valid pages are 1-{docs[d].Pages.Length}.");
                for (var p = range.FirstPage - 1; p < range.LastPage; p++) selected.Add((d, p));
            }
        }
        if (selected.Count == 0) return Failed("NoSelectedPages", "The page selection is empty.");
        if (options.ReverseOrder) selected.Reverse();

        var selectedGroups = selected.GroupBy(x => x.Doc).ToArray();
        var selectedByDoc = selectedGroups.ToDictionary(group => group.Key, group => group.Select(x => x.Page).ToArray());
        IEnumerable<(int Doc, int Copy)> EnumerateUnits()
        {
            if (options.CopyOrder == CopyOrder.CollatedPackets)
            {
                var maximumCopies = selectedGroups.Max(group => docs[group.Key].Settings?.Copies ?? options.Copies);
                for (var copy = 1; copy <= maximumCopies; copy++)
                    foreach (var group in selectedGroups)
                        if (copy <= (docs[group.Key].Settings?.Copies ?? options.Copies)) yield return (group.Key, copy);
            }
            else
                foreach (var group in selectedGroups)
                    for (var copy = 1; copy <= (docs[group.Key].Settings?.Copies ?? options.Copies); copy++)
                        yield return (group.Key, copy);
        }
        long estimatedFaces = 0;
        bool estimateNeedFront = true;
        int estimateLastDoc = -1;
        int estimateLastCopy = 0;
        int estimateUnitIndex = 0;
        void CountEstimatedFace()
        {
            estimatedFaces++;
            if (options.Duplex) estimateNeedFront = !estimateNeedFront;
        }
        foreach (var unit in EnumerateUnits())
        {
            var startsPacket = options.CopyOrder == CopyOrder.CollatedPackets ? unit.Copy != estimateLastCopy : estimateUnitIndex > 0;
            if (startsPacket && options.SeparatePacketsOnFront && options.Duplex && estimateLastCopy != 0 && !estimateNeedFront)
            {
                CountEstimatedFace();
                estimateNeedFront = true;
            }
            if (options.AddFrontCover && (startsPacket || estimateUnitIndex == 0)) CountEstimatedFace();
            var samePacketAsPrevious = options.CopyOrder == CopyOrder.PerDocument || unit.Copy == estimateLastCopy;
            if (options.AddSeparatorsBetweenFiles && estimateLastDoc >= 0 && unit.Doc != estimateLastDoc && samePacketAsPrevious)
                CountEstimatedFace();
            if (options.SeparateFilesOnFront && options.Duplex && !estimateNeedFront)
            {
                CountEstimatedFace();
                estimateNeedFront = true;
            }
            var capacity = (int)(docs[unit.Doc].Settings?.NUp ?? options.NUp);
            var sourceFaces = (selectedByDoc[unit.Doc].Length + capacity - 1L) / capacity;
            estimatedFaces += sourceFaces;
            if (options.Duplex && sourceFaces % 2 == 1) estimateNeedFront = !estimateNeedFront;
            estimateLastDoc = unit.Doc;
            estimateLastCopy = unit.Copy;
            estimateUnitIndex++;
            if (estimatedFaces > options.MaximumOutputPages) break;
        }
        if (estimatedFaces > options.MaximumOutputPages) return Failed("OutputLimitExceeded", "The compiled page count exceeds the configured limit.");

        var pages = ImmutableArray.CreateBuilder<PhysicalPage>();
        var sheet = 1;
        string? previousKey = null;
        string? previousCompatibilityKey = null;
        var segmentOrdinal = -1;
        bool needFront = true;
        bool outputLimitExceeded = false;
        bool TryAddPhysicalPage(PhysicalPage page)
        {
            if (pages.Count >= options.MaximumOutputPages)
            {
                outputLimitExceeded = true;
                return false;
            }
            pages.Add(page);
            return true;
        }
        void Append(string key, ImmutableArray<PagePlacement> placements, PageSize? outputSize = null, bool startsFile = false)
        {
            outputSize ??= options.SheetSize ?? placements[0].TargetSize;
            if (startsFile && options.SeparateFilesOnFront && options.Duplex && !needFront)
            {
                var previousOutputSize = pages.Count == 0 ? outputSize : pages[^1].OutputSize ?? outputSize;
                TryAddPhysicalPage(new PhysicalPage(pages.Count + 1, sheet++, PageSide.Back, previousKey ?? key, [], previousOutputSize));
                needFront = true;
            }
            var side = options.Duplex ? (needFront ? PageSide.Front : PageSide.Back) : PageSide.Front;
            if (!StringComparer.Ordinal.Equals(previousCompatibilityKey, key))
            {
                segmentOrdinal++;
                previousCompatibilityKey = key;
            }
            var segmentKey = $"{segmentOrdinal}:{key}";
            TryAddPhysicalPage(new PhysicalPage(pages.Count + 1, sheet, side, segmentKey, placements, outputSize));
            if (options.Duplex) { needFront = !needFront; if (side == PageSide.Back) sheet++; }
            else sheet++;
            previousKey = key;
        }

        var lastDoc = -1;
        var lastCopy = 0;
        var unitIndex = 0;
        foreach (var unit in EnumerateUnits())
        {
            var startsPacket = options.CopyOrder == CopyOrder.CollatedPackets ? unit.Copy != lastCopy : unitIndex > 0;
            if (startsPacket)
            {
                if (options.SeparatePacketsOnFront && options.Duplex && lastCopy != 0 && !needFront)
                {
                    var blankSize = pages.Count > 0 ? pages[pages.Count - 1].OutputSize : DocumentSheetSize(docs[unit.Doc], options) ?? docs[unit.Doc].Pages[selectedByDoc[unit.Doc][0]];
                    TryAddPhysicalPage(new PhysicalPage(pages.Count + 1, sheet++, PageSide.Back, previousKey ?? "blank", [], blankSize));
                    needFront = true;
                }
            }
            if (options.AddFrontCover && (startsPacket || unitIndex == 0))
            {
                var coverSize = DocumentSheetSize(docs[unit.Doc], options) ?? docs[unit.Doc].Pages[selectedByDoc[unit.Doc][0]];
                Append(SegmentKey(docs[unit.Doc], coverSize), [new PagePlacement("$cover", 0, unit.Copy, 0, coverSize, coverSize, 1, Kind: LayoutPageKind.Cover)], coverSize);
            }
            var samePacketAsPrevious = options.CopyOrder == CopyOrder.PerDocument || unit.Copy == lastCopy;
            if (options.AddSeparatorsBetweenFiles && lastDoc >= 0 && unit.Doc != lastDoc && samePacketAsPrevious)
            {
                var separatorSize = DocumentSheetSize(docs[unit.Doc], options) ?? docs[unit.Doc].Pages[selectedByDoc[unit.Doc][0]];
                Append(SegmentKey(docs[unit.Doc], separatorSize), [new PagePlacement("$separator", 0, unit.Copy, 0, separatorSize, separatorSize, 1, Kind: LayoutPageKind.Separator)], separatorSize);
            }
            lastDoc = unit.Doc;
            lastCopy = unit.Copy;
            unitIndex++;
            var sourcePages = selectedByDoc[unit.Doc];
            var capacity = (int)(docs[unit.Doc].Settings?.NUp ?? options.NUp);
            var fit = docs[unit.Doc].Settings?.Fit ?? options.Fit;
            for (var offset = 0; offset < sourcePages.Length; offset += capacity)
            {
                var group = sourcePages.Skip(offset).Take(capacity).ToArray();
                var sheetSize = DocumentSheetSize(docs[unit.Doc], options) ?? docs[unit.Doc].Pages[group[0]];
                var key = SegmentKey(docs[unit.Doc], sheetSize);
                var columns = capacity == 1 ? 1 : 2;
                var rows = capacity / columns;
                var cell = new PageSize(sheetSize.WidthPoints / columns, sheetSize.HeightPoints / rows);
                var placements = group.Select((x, slot) =>
                {
                    var source = docs[unit.Doc].Pages[x];
                    var directScale = Math.Min(cell.WidthPoints / source.WidthPoints, cell.HeightPoints / source.HeightPoints);
                    var rotatedScale = Math.Min(cell.WidthPoints / source.HeightPoints, cell.HeightPoints / source.WidthPoints);
                    var rotated = rotatedScale > directScale;
                    var scale = fit == FitMode.ActualSize ? 1d : Math.Max(directScale, rotatedScale);
                    if (fit == FitMode.ShrinkToFit) scale = Math.Min(1d, scale);
                    var bounds = new PageRectangle(slot % columns * cell.WidthPoints, slot / columns * cell.HeightPoints, cell.WidthPoints, cell.HeightPoints);
                    return new PagePlacement(docs[unit.Doc].ItemId, x + 1, unit.Copy, slot, source, cell, scale, rotated, TargetBounds: bounds);
                }).ToImmutableArray();
                Append(key, placements, sheetSize, startsFile: offset == 0);
            }
        }
        if (outputLimitExceeded) return Failed("OutputLimitExceeded", "The compiled page count exceeds the configured limit.");
        return new LayoutCompilation(pages.ToImmutable(), [], options.Duplex ? (pages.Count + 1) / 2 : pages.Count);

    }

    private static LayoutCompilation Failed(string code, string message) => new([], [new LayoutDiagnostic(code, message)], 0);

    private static PageSize? DocumentSheetSize(LayoutDocument document, LayoutOptions options) =>
        document.Settings?.SheetSize ?? options.SheetSize;

    private static string SegmentKey(LayoutDocument document, PageSize sheetSize)
    {
        var printer = document.PrinterId ?? string.Empty;
        var tray = document.TrayId ?? string.Empty;
        return $"{document.Media.Length}:{document.Media}{printer.Length}:{printer}{tray.Length}:{tray}{sheetSize.WidthPoints.ToString("R", CultureInfo.InvariantCulture)}:{sheetSize.HeightPoints.ToString("R", CultureInfo.InvariantCulture)}";
    }
}
