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

        // Covers, separators, and duplex blanks have no source item. Attach them to the next
        // source-owned page so each physical face is submitted once and remains in layout order.
        int? nextOwner = null;
        for (var index = owners.Length - 1; index >= 0; index--)
        {
            if (owners[index] is { } owner) nextOwner = owner;
            else if (nextOwner is { } following) owners[index] = following;
        }
        int? previousOwner = null;
        for (var index = 0; index < owners.Length; index++)
        {
            if (owners[index] is { } owner) previousOwner = owner;
            else if (previousOwner is { } preceding) owners[index] = preceding;
        }

        if (owners.Any(owner => owner is null))
            throw new InvalidDataException("A layout page has no prepared segment owner.");

        var seenOwners = new HashSet<int>();
        var previousSegmentOwner = -1;
        foreach (var owner in owners.Select(value => value!.Value))
        {
            if (owner == previousSegmentOwner) continue;
            if (!seenOwners.Add(owner))
                throw new InvalidDataException("The composed layout interleaves print segments; reprepare with compatible segment settings to preserve page order.");
            previousSegmentOwner = owner;
        }

        var pageNumbers = Enumerable.Range(0, segments.Count)
            .Select(segmentIndex => owners.Select((owner, pageIndex) => (owner, pageIndex))
                .Where(entry => entry.owner == segmentIndex)
                .Select(entry => entry.pageIndex + 1)
                .ToArray())
            .ToArray();
        if (pageNumbers.Any(pages => pages.Length == 0))
            throw new InvalidDataException("A prepared segment owns no pages in the composed final PDF.");

        return segments.Select((segment, index) => segment with { FinalPageNumbers = pageNumbers[index].ToImmutableArray() }).ToArray();
    }
}
