using PaperBatch.Core.Domain;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Printing;
using PaperBatch.Core.Rendering;
using System.Text;

internal static class GdiBackendTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal static void VerifiesArtifactAndReturnsReceipt()
    {
        var pdf = RendererTests.OnePagePdf();
        var artifact = new FinalPdfArtifact("artifact-1", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf)), pdf.Length, 1);
        var native = new FakeNative(Capabilities());
        var rasterizer = new FakeRasterizer();
        var backend = new WindowsGdiPrintBackend(native, rasterizer);
        var receipt = backend.SubmitAsync(Submission(pdf, artifact)).GetAwaiter().GetResult();

        Assert.Equal("queue-identity", receipt.QueueIdentity);
        Assert.Equal("job-from-startdoc", receipt.OsJobId);
        Assert.Equal("run-nonce", receipt.RunNonce);
        Assert.Equal("driver-v1", receipt.DriverName);
        Assert.True(pdf.SequenceEqual(rasterizer.ReceivedBytes));
        Assert.Equal(1, native.StartDocCount);
        Assert.True(native.LastDocument!.Disposed);
        Assert.Equal(1, native.LastDocument.CompletedPages);
        Assert.Equal(PrintBackendJobStatus.Processing, backend.GetStatusAsync(receipt).GetAwaiter().GetResult());
        Assert.True(backend.CancelAsync(receipt).GetAwaiter().GetResult());
        Assert.Equal("queue-identity", native.LastReceipt!.QueueIdentity);
        Assert.Equal("job-from-startdoc", native.LastReceipt.OsJobId);
        Assert.Equal("run-nonce", native.LastReceipt.RunNonce);
    }

    internal static void RasterizesAndSubmitsOnlySelectedFinalPages()
    {
        var pdf = RendererTests.OnePagePdf();
        var artifact = new FinalPdfArtifact("artifact-1",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf)), pdf.Length, 3);
        var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.LongEdge, "Letter", Orientation.Portrait,
            ScalingMode.ShrinkToFit, "queue-identity", "Tray1");
        var segment = new CompatibleSegment("segment", 0, ["item"], "compat", settings, [1, 3]);
        var options = WindowsPrintJobOptions.Map("queue-identity", Capabilities(), settings);
        var submission = new WindowsPrintSubmission(options, segment, "run-nonce", At, artifact,
            new MemoryStream(pdf), [1, 3]);
        var native = new FakeNative(Capabilities());
        var rasterizer = new FakeRasterizer();
        var backend = new WindowsGdiPrintBackend(native, rasterizer);

        _ = backend.SubmitAsync(submission).GetAwaiter().GetResult();

        Assert.True(rasterizer.RequestedPageNumbers!.SequenceEqual([1, 3]));
        Assert.Equal(2, native.LastDocument!.CompletedPages);
        Assert.Equal(1, native.StartDocCount);
    }

    internal static void RejectsMixedOrientationAutoSubmissionBeforeStartDoc()
    {
        var pdf = TwoPageMixedOrientationPdf();
        var pdfText = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/MediaBox [0 0 612 792]", pdfText);
        Assert.Contains("/MediaBox [0 0 792 612]", pdfText);
        var artifact = new FinalPdfArtifact("artifact-mixed", Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(pdf)), pdf.Length, 2);
        var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.LongEdge, "Letter", Orientation.Auto,
            ScalingMode.ActualSize, "queue-identity", "Tray1");
        var segment = new CompatibleSegment("segment", 0, ["item"], "compat", settings, [1, 2]);
        var options = WindowsPrintJobOptions.Map("queue-identity", Capabilities(), settings);
        var submission = new WindowsPrintSubmission(options, segment, "run-nonce", At, artifact,
            new MemoryStream(pdf), [1, 2]);
        var native = new FakeNative(Capabilities());
        var rasterizer = new FakeRasterizer
        {
            PageSizes = [new PageSize(612, 792), new PageSize(792, 612)]
        };
        var backend = new WindowsGdiPrintBackend(native, rasterizer);

        PrintSubmissionException? failure = null;
        try { _ = backend.SubmitAsync(submission).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { failure = error; }

        Assert.True(failure is not null, "Auto orientation must not submit mixed portrait and landscape pages in one GDI document.");
        Assert.False(failure!.MayHaveReachedOs);
        Assert.Contains("mixed page orientations", failure.Message);
        Assert.Equal(0, native.StartDocCount);
        Assert.True(rasterizer.ReceivedBytes.SequenceEqual(pdf));
        Assert.True(rasterizer.RequestedPageNumbers!.SequenceEqual([1, 2]));
        Assert.True(rasterizer.PageSizes!.Select(size => size.WidthPoints > size.HeightPoints).SequenceEqual([false, true]));
    }

    internal static void NativeAdapterValidatesSelectedPageIdentityForNonPrefixPages()
    {
        var selectedPages = new[]
        {
            new WindowsPrintRasterPage(3, new PageSize(612, 792), new PdfRasterPage(1, 1, 4, new byte[4]))
        };

        Assert.True(WindowsGdiNativeAdapter.IsSelectedPage(selectedPages, selectedPages[0]));
        Assert.False(WindowsGdiNativeAdapter.IsSelectedPage(selectedPages,
            new WindowsPrintRasterPage(2, selectedPages[0].PdfSize, selectedPages[0].Raster)));

        var laterPages = new[]
        {
            new WindowsPrintRasterPage(2, new PageSize(612, 792), new PdfRasterPage(1, 1, 4, new byte[4])),
            new WindowsPrintRasterPage(3, new PageSize(612, 792), new PdfRasterPage(1, 1, 4, new byte[4]))
        };
        Assert.True(WindowsGdiNativeAdapter.IsSelectedPage(laterPages, laterPages[0]));
        Assert.True(WindowsGdiNativeAdapter.IsSelectedPage(laterPages, laterPages[1]));
    }

    internal static void NativeAdapterMapsDeletedAndCancelledWinspoolStatusesConservatively()
    {
        Assert.Equal(PrintBackendJobStatus.Unknown, WindowsGdiNativeAdapter.MapWinspoolJobStatus(0x00000100u));
        Assert.Equal(PrintBackendJobStatus.Unknown, WindowsGdiNativeAdapter.MapWinspoolJobStatus(0x00008100u));
        Assert.Equal(PrintBackendJobStatus.Cancelled, WindowsGdiNativeAdapter.MapWinspoolJobStatus(0x00008000u));
    }

    internal static void RejectsPageSelectionOutsideSegmentOwnership()
    {
        var pdf = RendererTests.OnePagePdf();
        var artifact = new FinalPdfArtifact("artifact-1",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf)), pdf.Length, 2);
        var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.LongEdge, "Letter", Orientation.Portrait,
            ScalingMode.ShrinkToFit, "queue-identity", "Tray1");
        var segment = new CompatibleSegment("segment", 0, ["item"], "compat", settings, [1]);
        var options = WindowsPrintJobOptions.Map("queue-identity", Capabilities(), settings);
        var submission = new WindowsPrintSubmission(options, segment, "run-nonce", At, artifact,
            new MemoryStream(pdf), [2]);
        var native = new FakeNative(Capabilities());
        var rasterizer = new FakeRasterizer();
        var backend = new WindowsGdiPrintBackend(native, rasterizer);

        var mayHaveReachedOs = true;
        try { _ = backend.SubmitAsync(submission).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { mayHaveReachedOs = error.MayHaveReachedOs; }

        Assert.False(mayHaveReachedOs);
        Assert.Equal(0, native.StartDocCount);
        Assert.True(rasterizer.RequestedPageNumbers is null);
    }

    internal static void RejectsCapabilityMutationBeforeStartDoc()
    {
        var pdf = RendererTests.OnePagePdf();
        var artifact = Artifact(pdf);
        var native = new FakeNative(Capabilities()) { SecondCapabilities = Capabilities() with { Fingerprint = "changed" } };
        var backend = new WindowsGdiPrintBackend(native, new FakeRasterizer());

        var mayHaveReachedOs = true;
        try { _ = backend.SubmitAsync(Submission(pdf, artifact)).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { mayHaveReachedOs = error.MayHaveReachedOs; }
        Assert.False(mayHaveReachedOs);
        Assert.Equal(0, native.StartDocCount);
    }

    internal static void CapabilityLookupFailureInsideAdapterIsKnownNoEffect()
    {
        var pdf = RendererTests.OnePagePdf();
        var native = new FakeNative(Capabilities()) { FailCapabilityLookupInStartDoc = true };
        var backend = new WindowsGdiPrintBackend(native, new FakeRasterizer());

        PrintSubmissionException? failure = null;
        try { _ = backend.SubmitAsync(Submission(pdf, Artifact(pdf))).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { failure = error; }

        Assert.True(failure is not null);
        Assert.False(failure!.MayHaveReachedOs);
        Assert.True(failure.InnerException is IOException);
        Assert.Equal(1, native.StartDocCount);
        Assert.Equal(0, native.GdiStartDocCount);
    }

    internal static void RasterizationCancellationIsKnownNoEffect()
    {
        var pdf = RendererTests.OnePagePdf();
        var native = new FakeNative(Capabilities());
        var backend = new WindowsGdiPrintBackend(native, new FakeRasterizer { CancelDuringRasterization = true });

        var mayHaveReachedOs = true;
        try { _ = backend.SubmitAsync(Submission(pdf, Artifact(pdf))).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { mayHaveReachedOs = error.MayHaveReachedOs; }

        Assert.False(mayHaveReachedOs);
        Assert.Equal(0, native.StartDocCount);
    }

    internal static void InitialCancellationIsKnownNoEffect()
    {
        var pdf = RendererTests.OnePagePdf();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var native = new FakeNative(Capabilities());
        var backend = new WindowsGdiPrintBackend(native, new FakeRasterizer());

        var mayHaveReachedOs = true;
        try { _ = backend.SubmitAsync(Submission(pdf, Artifact(pdf)), cancellation.Token).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { mayHaveReachedOs = error.MayHaveReachedOs; }

        Assert.False(mayHaveReachedOs);
        Assert.Equal(0, native.StartDocCount);
    }

    internal static void CancellationAfterStartDocRemainsAmbiguous()
    {
        var pdf = RendererTests.OnePagePdf();
        var native = new FakeNative(Capabilities()) { CancelWritingPage = true };
        var backend = new WindowsGdiPrintBackend(native, new FakeRasterizer());

        var mayHaveReachedOs = false;
        try { _ = backend.SubmitAsync(Submission(pdf, Artifact(pdf))).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { mayHaveReachedOs = error.MayHaveReachedOs; }

        Assert.True(mayHaveReachedOs);
        Assert.Equal(1, native.StartDocCount);
        Assert.True(native.LastDocument!.Disposed);
    }

    internal static void NativeAdapterCancellationBeforeStartDocIsKnownNoEffect()
    {
        var pdf = RendererTests.OnePagePdf();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var adapter = new WindowsGdiNativeAdapter();

        var mayHaveReachedOs = true;
        try
        {
            _ = adapter.StartDocAsync(Submission(pdf, Artifact(pdf)), [], cancellation.Token).GetAwaiter().GetResult();
        }
        catch (PrintSubmissionException error) { mayHaveReachedOs = error.MayHaveReachedOs; }

        Assert.False(mayHaveReachedOs);
    }

    internal static void PreservesAmbiguityAndCleansUp()
    {
        var pdf = RendererTests.OnePagePdf();
        var artifact = Artifact(pdf);
        var native = new FakeNative(Capabilities()) { FailWritingPage = true };
        var backend = new WindowsGdiPrintBackend(native, new FakeRasterizer());

        var mayHaveReachedOs = false;
        try { _ = backend.SubmitAsync(Submission(pdf, artifact)).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { mayHaveReachedOs = error.MayHaveReachedOs; }
        Assert.True(mayHaveReachedOs);
        Assert.Equal(1, native.StartDocCount);
        Assert.True(native.LastDocument!.Disposed);
        Assert.Equal(0, native.EndDocCount);
    }

    internal static void RejectsUnsupportedSettingsBeforeStartDoc()
    {
        var pdf = RendererTests.OnePagePdf();
        var artifact = Artifact(pdf);
        var caps = Capabilities() with
        {
            Options = Capabilities().Options.SetItem(PrintOption.Tray,
                new PrintOptionCapability(CapabilityState.Unavailable, [], "driver tray inventory unavailable"))
        };
        var native = new FakeNative(caps);
        var backend = new WindowsGdiPrintBackend(native, new FakeRasterizer());
        var mayHaveReachedOs = true;
        try { _ = backend.SubmitAsync(Submission(pdf, artifact)).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { mayHaveReachedOs = error.MayHaveReachedOs; }
        Assert.False(mayHaveReachedOs);
        Assert.Equal(0, native.StartDocCount);
    }

    internal static void RejectsArtifactBytesThatDoNotMatchReceipt()
    {
        var pdf = RendererTests.OnePagePdf();
        var wrongArtifact = Artifact(pdf) with { Sha256 = new string('b', 64) };
        var native = new FakeNative(Capabilities());
        var rasterizer = new FakeRasterizer();
        var backend = new WindowsGdiPrintBackend(native, rasterizer);
        var mayHaveReachedOs = true;
        try { _ = backend.SubmitAsync(Submission(pdf, wrongArtifact)).GetAwaiter().GetResult(); }
        catch (PrintSubmissionException error) { mayHaveReachedOs = error.MayHaveReachedOs; }
        Assert.False(mayHaveReachedOs);
        Assert.Equal(0, rasterizer.ReceivedBytes.Length);
        Assert.Equal(0, native.StartDocCount);
    }

    internal static void RejectsDriverClearingRequiredDevModeFlags()
    {
        const uint required = 0x00000001 | 0x00000002 | 0x00000100 | 0x00000800 | 0x00001000 | 0x00000200;
        Assert.True(WindowsGdiNativeAdapter.HasRequiredDevModeFields(required, required));
        foreach (var flag in new uint[] { 0x00000001, 0x00000002, 0x00000100, 0x00000800, 0x00001000, 0x00000200 })
            Assert.False(WindowsGdiNativeAdapter.HasRequiredDevModeFields(required & ~flag, required));
    }

    internal static void RejectsIncompleteNativeCapabilityMappings()
    {
        var values = new[] { "Letter", "A4" };
        var complete = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
            .Add("Letter", "1").Add("A4", "9");
        var missing = complete.Remove("A4");
        Assert.True(WindowsGdiNativeAdapter.HasExactNativeMapping(values, complete));
        Assert.False(WindowsGdiNativeAdapter.HasExactNativeMapping(values, missing));
        Assert.False(WindowsGdiNativeAdapter.HasExactNativeMapping(values, complete.SetItem("Other", "12")));
    }

    internal static void NativeDuplexCapabilityMapsSafeModesForPrintOptions()
    {
        var baseSettings = new PrintSettings(1, ColorMode.Color, DuplexMode.LongEdge, "Letter", Orientation.Portrait,
            ScalingMode.ShrinkToFit, "queue-identity", "Tray1");
        var unsupported = WindowsGdiNativeAdapter.MapDuplexCapability(-1);
        Assert.Equal(CapabilityState.Unavailable, unsupported.State);
        Assert.Equal(0, unsupported.Values.Length);
        Assert.Throws<PrintCapabilityException>(() => WindowsPrintJobOptions.Map("queue-identity",
            Capabilities() with { Options = Capabilities().Options.SetItem(PrintOption.Duplex, unsupported) },
            baseSettings with { DuplexMode = DuplexMode.Simplex }));

        var simplex = WindowsGdiNativeAdapter.MapDuplexCapability(0);
        Assert.Equal(CapabilityState.Supported, simplex.State);
        Assert.True(simplex.Values.SequenceEqual(["Simplex"]));
        var simplexSettings = baseSettings with { DuplexMode = DuplexMode.Simplex };
        var simplexOptions = WindowsPrintJobOptions.Map("queue-identity",
            Capabilities() with { Options = Capabilities().Options.SetItem(PrintOption.Duplex, simplex) }, simplexSettings);
        Assert.Equal("Simplex", simplexOptions.Duplex);
        Assert.Throws<PrintCapabilityException>(() => WindowsPrintJobOptions.Map("queue-identity",
            Capabilities() with { Options = Capabilities().Options.SetItem(PrintOption.Duplex, simplex) }, baseSettings));

        var duplex = WindowsGdiNativeAdapter.MapDuplexCapability(1);
        Assert.Equal(CapabilityState.Supported, duplex.State);
        Assert.True(duplex.Values.SequenceEqual(["Simplex", "LongEdge", "ShortEdge"]));
        foreach (var mode in new[] { DuplexMode.LongEdge, DuplexMode.ShortEdge })
        {
            var settings = baseSettings with { DuplexMode = mode };
            var options = WindowsPrintJobOptions.Map("queue-identity",
                Capabilities() with { Options = Capabilities().Options.SetItem(PrintOption.Duplex, duplex) }, settings);
            Assert.Equal(mode.ToString(), options.Duplex);
        }
    }

    private static WindowsPrintSubmission Submission(byte[] pdf, FinalPdfArtifact artifact)
    {
        var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.LongEdge, "Letter", Orientation.Portrait,
            ScalingMode.ShrinkToFit, "queue-identity", "Tray1");
        var segment = new CompatibleSegment("segment", 0, ["item"], "compat", settings);
        var options = WindowsPrintJobOptions.Map("queue-identity", Capabilities(), settings);
        return new WindowsPrintSubmission(options, segment, "run-nonce", At, artifact, new MemoryStream(pdf),
            Enumerable.Range(1, artifact.PageCount).ToArray());
    }

    private static FinalPdfArtifact Artifact(byte[] pdf) => new("artifact-1",
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf)), pdf.Length, 1);

    private static byte[] TwoPageMixedOrientationPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /ProcSet [/PDF] >> /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 792 612] /Resources << /ProcSet [/PDF] >> /Contents 6 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.7\n%PaperBatch synthetic mixed-orientation fixture\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static PrinterCapabilitySnapshot Capabilities() => new("queue-identity", "Queue", "driver-v1", "port-v1", "fingerprint-v1",
        new Dictionary<PrintOption, PrintOptionCapability>
        {
            [PrintOption.Media] = new(CapabilityState.Supported, ["Letter"], null),
            [PrintOption.Tray] = new(CapabilityState.Supported, ["Tray1"], null),
            [PrintOption.Color] = new(CapabilityState.Supported, ["Color"], null),
            [PrintOption.Duplex] = new(CapabilityState.Supported, ["LongEdge"], null),
            [PrintOption.Copies] = new(CapabilityState.Supported, ["1..999"], null)
        });

    private sealed class FakeRasterizer : IWindowsPrintPdfRasterizer
    {
        public bool CancelDuringRasterization { get; init; }
        public IReadOnlyList<PageSize>? PageSizes { get; init; }
        public byte[] ReceivedBytes { get; private set; } = [];
        public IReadOnlyList<int>? RequestedPageNumbers { get; private set; }
        public async Task<IReadOnlyList<WindowsPrintRasterPage>> RasterizeAsync(Stream pdf, int expectedPageCount, CancellationToken cancellationToken = default)
            => await RasterizePagesAsync(pdf, expectedPageCount, Enumerable.Range(1, expectedPageCount).ToArray(), cancellationToken);

        public async Task<IReadOnlyList<WindowsPrintRasterPage>> RasterizePagesAsync(
            Stream pdf, int expectedPageCount, IReadOnlyList<int> pageNumbers, CancellationToken cancellationToken = default)
        {
            using var content = new MemoryStream();
            await pdf.CopyToAsync(content, cancellationToken);
            ReceivedBytes = content.ToArray();
            RequestedPageNumbers = pageNumbers.ToArray();
            if (CancelDuringRasterization) throw new OperationCanceledException(cancellationToken);
            return pageNumbers.Select(page =>
            {
                var pageSize = PageSizes?[page - 1] ?? new PageSize(612, 792);
                var landscape = pageSize.WidthPoints > pageSize.HeightPoints;
                var width = landscape ? 22 : 17;
                var height = landscape ? 17 : 22;
                return new WindowsPrintRasterPage(page, pageSize,
                    new PdfRasterPage(width, height, width * 4, new byte[width * height * 4]));
            }).ToArray();
        }
    }

    private sealed class FakeNative(PrinterCapabilitySnapshot capabilities) : IWindowsGdiNativeAdapter
    {
        private int _reads;
        public PrinterCapabilitySnapshot? SecondCapabilities { get; init; }
        public bool FailWritingPage { get; init; }
        public bool CancelWritingPage { get; init; }
        public bool FailCapabilityLookupInStartDoc { get; init; }
        public int StartDocCount { get; private set; }
        public int GdiStartDocCount { get; private set; }
        public int EndDocCount { get; private set; }
        public FakeDocument? LastDocument { get; private set; }
        public OsJobReceipt? LastReceipt { get; private set; }
        public Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default) =>
            Task.FromResult(++_reads >= 2 ? SecondCapabilities ?? capabilities : capabilities);
        public Task<IWindowsGdiDocument> StartDocAsync(WindowsPrintSubmission submission, IReadOnlyList<WindowsPrintRasterPage> pages, CancellationToken cancellationToken = default)
        {
            StartDocCount++;
            if (FailCapabilityLookupInStartDoc)
                _ = WindowsGdiNativeAdapter.GetCapabilitySnapshotBeforeStartDoc<PrinterCapabilitySnapshot>(
                    () => throw new IOException("queue disappeared during capability enumeration"));
            GdiStartDocCount++;
            LastDocument = new FakeDocument(this, FailWritingPage, CancelWritingPage);
            return Task.FromResult<IWindowsGdiDocument>(LastDocument);
        }
        public Task<PrintBackendJobStatus> GetStatusAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default)
        { LastReceipt = receipt; return Task.FromResult(PrintBackendJobStatus.Processing); }
        public Task<bool> CancelAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default)
        { LastReceipt = receipt; return Task.FromResult(true); }

        public sealed class FakeDocument(FakeNative owner, bool failWrite, bool cancelWrite) : IWindowsGdiDocument
        {
            public string OsJobId => "job-from-startdoc";
            public int CompletedPages { get; private set; }
            public bool Disposed { get; private set; }
            public Task StartPageAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task WritePageAsync(WindowsPrintRasterPage page, CancellationToken cancellationToken = default) =>
                cancelWrite ? Task.FromException(new OperationCanceledException(cancellationToken)) :
                failWrite ? Task.FromException(new IOException("write failed")) : Task.CompletedTask;
            public Task EndPageAsync(CancellationToken cancellationToken = default) { CompletedPages++; return Task.CompletedTask; }
            public Task EndDocAsync(CancellationToken cancellationToken = default) { owner.EndDocCount++; return Task.CompletedTask; }
            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }
    }
}
