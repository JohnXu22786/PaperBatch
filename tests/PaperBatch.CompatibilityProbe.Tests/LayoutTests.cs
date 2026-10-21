using PaperBatch.Core.Layout;
using System.Collections.Immutable;

internal static class LayoutTests
{
    internal static void EmptyAndInvalidRangesAreDiagnostics()
    {
        Assert.Equal("NoDocuments", LayoutCompiler.Compile([], new()).Diagnostics[0].Code);
        Assert.Equal("InvalidRange", LayoutCompiler.Compile([Doc("a", 2)], new(Ranges: [new(0, 1)])).Diagnostics[0].Code);
        Assert.Equal("InvalidRange", LayoutCompiler.Compile([Doc("a", 2)], new(Ranges: new PageRange[] { null! }.ToImmutableArray())).Diagnostics[0].Code);
        Assert.Equal("InvalidPageSize", LayoutCompiler.Compile([new LayoutDocument("a", new PageSize[] { null! }, "Letter")], new()).Diagnostics[0].Code);
        Assert.Equal("InvalidDocument", LayoutCompiler.Compile([new LayoutDocument("empty", Array.Empty<PageSize>(), "Letter")], new()).Diagnostics[0].Code);
    }

    internal static void NUpAndFitAreDeterministicAndImmutable()
    {
        var input = new List<LayoutDocument> { Doc("a", 5) };
        var options = new LayoutOptions(NUp: NUpMode.Two, SheetSize: new(612, 792));
        var first = LayoutCompiler.Compile(input, options);
        input.Clear();
        var second = LayoutCompiler.Compile([Doc("a", 5)], options);
        Assert.True(first.Succeeded);
        Assert.True(first.Pages.SelectMany(p => p.Placements).Select(p => (p.ItemId, p.SourcePageNumber, p.CopyNumber, p.Slot, p.Scale))
            .SequenceEqual(second.Pages.SelectMany(p => p.Placements).Select(p => (p.ItemId, p.SourcePageNumber, p.CopyNumber, p.Slot, p.Scale))));
        Assert.Equal(3, first.Pages.Length);
        Assert.Equal(2, first.Pages[0].Placements.Length);
        Assert.True(first.Pages[0].Placements.All(page => page.Scale == 0.5 && page.TargetBounds?.WidthPoints == 306));
    }

    internal static void FourUpUsesFourDistinctCellRectangles()
    {
        var result = LayoutCompiler.Compile([Doc("a", 4)], new(NUp: NUpMode.Four, SheetSize: new(612, 792)));
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(1, result.Pages.Length);
        Assert.Equal(4, result.Pages[0].Placements.Length);
        Assert.True(result.Pages[0].Placements.Select(p => (p.TargetBounds!.XPoints, p.TargetBounds.YPoints)).Distinct().Count() == 4);
        Assert.True(result.Pages[0].Placements.All(p => p.TargetBounds!.WidthPoints == 306 && p.TargetBounds.HeightPoints == 396 && p.Scale == 0.5));
    }

    internal static void RangesAndReverseOrderPreserveSelectedMapping()
    {
        var result = LayoutCompiler.Compile([Doc("a", 3), Doc("b", 3)], new(Ranges: [new(2, 3)], ReverseOrder: true));
        Assert.True(result.Succeeded);
        Assert.True(result.Pages.Select(p => (p.Placements[0].ItemId, p.Placements[0].SourcePageNumber))
            .SequenceEqual([("b", 3), ("b", 2), ("a", 3), ("a", 2)]));
    }

    internal static void MixedSheetSizesCreateDistinctContiguousSegments()
    {
        var mixed = new LayoutDocument("a", [new(612, 792), new(792, 612), new(612, 792)], "Letter");
        var result = LayoutCompiler.Compile([mixed], new());
        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Pages.Length);
        Assert.True(result.Pages[0].SegmentKey != result.Pages[1].SegmentKey);
        Assert.True(result.Pages[0].SegmentKey != result.Pages[2].SegmentKey);
    }

    internal static void DuplexFileBoundariesInsertBlankBackFace()
    {
        var result = LayoutCompiler.Compile([Doc("a", 1), Doc("b", 1)], new(Duplex: true, SeparateFilesOnFront: true));
        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Pages.Length);
        Assert.Equal(PageSide.Front, result.Pages[0].Side);
        Assert.True(result.Pages[1].IsBlank);
        Assert.Equal(PageSide.Back, result.Pages[1].Side);
        Assert.Equal(PageSide.Front, result.Pages[2].Side);
        Assert.Equal(2, result.SheetEstimate);
        Assert.Equal(new PageSize(612, 792), result.Pages[1].OutputSize);

        var mixedSizes = LayoutCompiler.Compile(
            [new LayoutDocument("letter", [new PageSize(612, 792)], "Letter"), new LayoutDocument("a4", [new PageSize(595, 842)], "A4")],
            new(Duplex: true, SeparateFilesOnFront: true));
        Assert.True(mixedSizes.Succeeded);
        Assert.Equal(mixedSizes.Pages[0].OutputSize, mixedSizes.Pages[1].OutputSize);
        Assert.Equal(new PageSize(595, 842), mixedSizes.Pages[2].OutputSize);
    }

    internal static void PhysicalFacesExposeSheetDimensionsInsteadOfNUpCells()
    {
        var result = LayoutCompiler.Compile([Doc("a", 2)], new(NUp: NUpMode.Two, Duplex: true, SheetSize: new(700, 900), SeparateFilesOnFront: true));
        Assert.True(result.Succeeded);
        Assert.True(result.Pages.All(page => page.OutputSize == new PageSize(700, 900)));
        Assert.True(result.Pages[0].Placements.All(placement => placement.TargetSize == new PageSize(350, 900)));
    }

    internal static void CopyExpansionAndSegmentsPreserveOrder()
    {
        var result = LayoutCompiler.Compile([Doc("a", 2, "A4"), Doc("b", 1, "Letter")], new(Copies: 2));
        Assert.True(result.Succeeded);
        Assert.Equal(6, result.Pages.Length);
        Assert.Equal("a", result.Pages[0].Placements[0].ItemId);
        Assert.Equal(1, result.Pages[0].Placements[0].CopyNumber);
        Assert.Equal("b", result.Pages[2].Placements[0].ItemId);
        Assert.Equal(2, result.Pages[3].Placements[0].CopyNumber);
        Assert.True(result.Pages.Select(x => x.SegmentKey).SequenceEqual(["0:2:A40:0:612:792", "0:2:A40:0:612:792", "1:6:Letter0:0:612:792", "2:2:A40:0:612:792", "2:2:A40:0:612:792", "3:6:Letter0:0:612:792"]));
        Assert.True(result.Pages[0].SegmentKey != result.Pages[3].SegmentKey);
    }

    internal static void PerDocumentCopyRangeNUpAndFitSettingsAffectCompiledPages()
    {
        var docs = new[]
        {
            new LayoutDocument("a", [new PageSize(612, 792), new PageSize(612, 792)], "Letter",
                settings: new LayoutDocumentSettings(Copies: 2, NUp: NUpMode.Two, Fit: FitMode.ActualSize,
                    SheetSize: new PageSize(700, 900), Ranges: [new PageRange(1, 2)])),
            new LayoutDocument("b", [new PageSize(612, 792), new PageSize(612, 792)], "Letter",
                settings: new LayoutDocumentSettings(Copies: 1, Ranges: [new PageRange(2, 2)]))
        };
        var result = LayoutCompiler.Compile(docs, new(Copies: 1, NUp: NUpMode.One));
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(3, result.Pages.Length);
        var pagesA = result.Pages.Where(page => page.Placements[0].ItemId == "a").ToArray();
        Assert.True(pagesA.All(page => page.OutputSize == new PageSize(700, 900)));
        Assert.True(pagesA.All(page => page.Placements.Length == 2));
        Assert.True(pagesA.SelectMany(page => page.Placements).Select(page => page.SourcePageNumber).SequenceEqual([1, 2, 1, 2]));
        Assert.True(pagesA.Select(page => page.Placements[0].CopyNumber).SequenceEqual([1, 2]));
        Assert.True(pagesA.SelectMany(page => page.Placements).All(page => page.Scale == 1d));
        Assert.Equal(1, result.Pages.Count(page => page.Placements[0].ItemId == "b"));
        Assert.Equal(2, result.Pages.Single(page => page.Placements[0].ItemId == "b").Placements[0].SourcePageNumber);
    }

    internal static void AutoOrientationPreservesSourcePageDimensions()
    {
        var landscape = new PageSize(792, 612);
        var portrait = new PageSize(612, 792);
        var result = LayoutCompiler.Compile([new LayoutDocument("mixed", [landscape, portrait], "Letter")], new());
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.True(result.Pages.Select(page => page.OutputSize).SequenceEqual<PageSize?>([landscape, portrait]));
        Assert.True(result.Pages.SelectMany(page => page.Placements).All(placement => placement.Scale == 1d));
    }

    internal static void OutputLimitAndCopiesAreExplicit()
    {
        Assert.Equal("InvalidCopies", LayoutCompiler.Compile([Doc("a", 1)], new(Copies: 1000)).Diagnostics[0].Code);
        Assert.Equal("OutputLimitExceeded", LayoutCompiler.Compile([Doc("a", 4)], new(NUp: NUpMode.One, MaximumOutputPages: 3)).Diagnostics[0].Code);
        Assert.Equal("OutputLimitExceeded", LayoutCompiler.Compile([Doc("a", 2000)], new(Copies: 999)).Diagnostics[0].Code);
        Assert.Equal("OutputLimitExceeded", LayoutCompiler.Compile(
            [Doc("a", 1), Doc("b", 1)],
            new(Copies: 2, CopyOrder: CopyOrder.PerDocument, Duplex: true, SeparatePacketsOnFront: true, MaximumOutputPages: 5)).Diagnostics[0].Code);
        Assert.Equal("OutputLimitExceeded", LayoutCompiler.Compile(
            [Doc("a", 1), Doc("b", 1)],
            new(Duplex: true, SeparateFilesOnFront: true, MaximumOutputPages: 2)).Diagnostics[0].Code);
        var exactFit = LayoutCompiler.Compile([Doc("a", 2), Doc("b", 2)], new(Duplex: true, SeparateFilesOnFront: true, MaximumOutputPages: 4));
        Assert.True(exactFit.Succeeded);
        Assert.Equal(4, exactFit.Pages.Length);
    }

    internal static void MixedOrientationUsesRotationWhenItFitsBetter()
    {
        var doc = new LayoutDocument("a", [new PageSize(792, 612)], "Letter");
        var result = LayoutCompiler.Compile([doc], new(SheetSize: new PageSize(612, 792)));
        Assert.True(result.Succeeded);
        Assert.True(result.Pages[0].Placements[0].RotatedClockwise);
        Assert.Equal(1d, result.Pages[0].Placements[0].Scale);
    }

    internal static void CoversAndSeparatorsAreExplicitLayoutPages()
    {
        var result = LayoutCompiler.Compile([Doc("a", 1), Doc("b", 1)], new(AddFrontCover: true, AddSeparatorsBetweenFiles: true));
        Assert.True(result.Succeeded);
        Assert.Equal(LayoutPageKind.Cover, result.Pages[0].Placements[0].Kind);
        Assert.Equal(LayoutPageKind.Separator, result.Pages[2].Placements[0].Kind);
        Assert.Equal("a", result.Pages[1].Placements[0].ItemId);
        Assert.Equal("b", result.Pages[3].Placements[0].ItemId);
    }

    internal static void DuplexSeparatorDoesNotMoveNextFileOffFront()
    {
        var result = LayoutCompiler.Compile(
            [Doc("a", 1), Doc("b", 1)],
            new(Duplex: true, SeparateFilesOnFront: true, AddSeparatorsBetweenFiles: true));
        Assert.True(result.Succeeded);
        var nextFile = result.Pages.Single(page => page.Placements.Any(placement => placement.ItemId == "b"));
        Assert.Equal(PageSide.Front, nextFile.Side);
    }

    internal static void PerDocumentCopiesKeepCoversAndFileSeparatorsDistinct()
    {
        var result = LayoutCompiler.Compile(
            [Doc("a", 1), Doc("b", 1)],
            new(Copies: 2, CopyOrder: CopyOrder.PerDocument, AddFrontCover: true, AddSeparatorsBetweenFiles: true));
        Assert.True(result.Succeeded);
        Assert.Equal(4, result.Pages.Count(page => page.Placements.Any(placement => placement.Kind == LayoutPageKind.Cover)));
        Assert.Equal(1, result.Pages.Count(page => page.Placements.Any(placement => placement.Kind == LayoutPageKind.Separator)));
        Assert.Equal(9, result.Pages.Length);
    }

    private static LayoutDocument Doc(string id, int count, string media = "Letter") => new(id, Enumerable.Repeat(new PageSize(612, 792), count), media);
}
