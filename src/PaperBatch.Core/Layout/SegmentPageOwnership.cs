using System.Collections.Immutable;
using PaperBatch.Core.Domain;

namespace PaperBatch.Core.Layout;

/// <summary>Assigns each composed physical page to exactly one prepared segment in layout order.</summary>
public static class SegmentPageOwnership
{
    public static IReadOnlyList<CompatibleSegment> Assign(LayoutCompilation layout, IReadOnlyList<CompatibleSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(segments);
        if (!layout.Succeeded || layout.Pages.IsDefaultOrEmpty)
            throw new ArgumentException("A successful nonempty layout is required.", nameof(layout));

        var segmentByItem = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            foreach (var itemId in segments[segmentIndex].ItemIds)
                if (!segmentByItem.TryAdd(itemId, segmentIndex))
                    throw new InvalidDataException($"Prepared item '{itemId}' belongs to more than one segment.");

        var owners = new int?[layout.Pages.Length];
        for (var pageIndex = 0; pageIndex < layout.Pages.Length; pageIndex++)
        {
            var page = layout.Pages[pageIndex];
            if (page.Number != pageIndex + 1)
                throw new InvalidDataException("Layout page numbers must be contiguous before assigning segment ownership.");

            var pageOwners = page.Placements
                .Where(placement => placement.Kind == LayoutPageKind.Source)
                .Select(placement => segmentByItem.TryGetValue(placement.ItemId, out var owner)
                    ? owner
                    : throw new InvalidDataException($"Layout page {page.Number} references unknown item '{placement.ItemId}'."))
                .Distinct()
                .ToArray();
            if (pageOwners.Length > 1)
                throw new InvalidDataException($"Layout page {page.Number} combines items from different print segments and cannot be submitted safely.");
            if (pageOwners.Length == 1) owners[pageIndex] = pageOwners[0];
        }

        // A duplex back-side blank is the reverse of the preceding document's sheet, so it
        // must stay with the segment that owns that front. Other source-less pages attach forward.
        int? previousOwner = null;
        for (var index = 0; index < owners.Length; index++)
        {
            if (owners[index] is { } owner) previousOwner = owner;
            else if (layout.Pages[index].IsBlank && layout.Pages[index].Side == PageSide.Back && previousOwner is { } preceding)
                owners[index] = preceding;
        }

        int? nextOwner = null;
        for (var index = owners.Length - 1; index >= 0; index--)
        {
            if (owners[index] is { } owner) nextOwner = owner;
            else if (nextOwner is { } following) owners[index] = following;
        }
        previousOwner = null;
        for (var index = 0; index < owners.Length; index++)
        {
            if (owners[index] is { } owner) previousOwner = owner;
            else if (previousOwner is { } preceding) owners[index] = preceding;
        }

        if (owners.Any(owner => owner is null))
            throw new InvalidDataException("A layout page has no prepared segment owner.");
        if (Enumerable.Range(0, segments.Count).Any(segmentIndex => !owners.Contains(segmentIndex)))
            throw new InvalidDataException("A prepared segment owns no pages in the composed final PDF.");

        var orderedOwners = owners.Select(owner => owner!.Value).ToArray();
        var result = new List<CompatibleSegment>();
        var splitCounts = new int[segments.Count];
        var usedSegmentIds = new HashSet<string>(StringComparer.Ordinal);
        var rangeStart = 0;
        while (rangeStart < orderedOwners.Length)
        {
            var owner = orderedOwners[rangeStart];
            var rangeEnd = rangeStart + 1;
            while (rangeEnd < orderedOwners.Length && orderedOwners[rangeEnd] == owner) rangeEnd++;
            var sourceSegment = segments[owner];
            var splitIndex = ++splitCounts[owner];
            var segmentId = splitIndex == 1 ? sourceSegment.SegmentId :
                sourceSegment.SegmentId + "_part" + splitIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (segmentId.Length > 128 || !usedSegmentIds.Add(segmentId))
            {
                segmentId = "segment_" + result.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                while (!usedSegmentIds.Add(segmentId)) segmentId += "_";
            }
            result.Add(sourceSegment with
            {
                SegmentId = segmentId,
                Index = result.Count,
                FinalPageNumbers = Enumerable.Range(rangeStart + 1, rangeEnd - rangeStart).ToImmutableArray()
            });
            rangeStart = rangeEnd;
        }

        return result;
    }
}
