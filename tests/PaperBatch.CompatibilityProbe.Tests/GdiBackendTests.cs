using PaperBatch.Core.Domain;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Printing;
using PaperBatch.Core.Rendering;

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

    private static WindowsPrintSubmission Submission(byte[] pdf, FinalPdfArtifact artifact)
    {
        var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.LongEdge, "Letter", Orientation.Portrait,
            ScalingMode.ShrinkToFit, "queue-identity", "Tray1");
        var segment = new CompatibleSegment("segment", 0, ["item"], "compat", settings);
        var options = WindowsPrintJobOptions.Map("queue-identity", Capabilities(), settings);
        return new WindowsPrintSubmission(options, segment, "run-nonce", At, artifact, new MemoryStream(pdf));
    }

    private static FinalPdfArtifact Artifact(byte[] pdf) => new("artifact-1",
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf)), pdf.Length, 1);

    private static PrinterCapabilitySnapshot Capabilities() => new("queue-identity", "Queue", "driver-v1", "port-v1", "fingerprint-v1",
        new Dictionary<PrintOption, PrintOptionCapability>
        {
            [PrintOption.Media] = new(CapabilityState.Supported, ["Letter"], null),
            [PrintOption.Tray] = new(CapabilityState.Supported, ["Tray1"], null),
            [PrintOption.Color] = new(CapabilityState.Supported, ["Color"], null),
            [PrintOption.Duplex] = new(CapabilityState.Supported, ["LongEdge"], null)
        });

    private sealed class FakeRasterizer : IWindowsPrintPdfRasterizer
    {
        public byte[] ReceivedBytes { get; private set; } = [];
        public async Task<IReadOnlyList<WindowsPrintRasterPage>> RasterizeAsync(Stream pdf, int expectedPageCount, CancellationToken cancellationToken = default)
        {
            using var content = new MemoryStream();
            await pdf.CopyToAsync(content, cancellationToken);
            ReceivedBytes = content.ToArray();
            return [
                new(1, new PageSize(612, 792), new PdfRasterPage(17, 22, 68, new byte[1496]))
            ];
        }
    }

    private sealed class FakeNative(PrinterCapabilitySnapshot capabilities) : IWindowsGdiNativeAdapter
    {
        private int _reads;
        public PrinterCapabilitySnapshot? SecondCapabilities { get; init; }
        public bool FailWritingPage { get; init; }
        public int StartDocCount { get; private set; }
        public int EndDocCount { get; private set; }
        public FakeDocument? LastDocument { get; private set; }
        public OsJobReceipt? LastReceipt { get; private set; }
        public Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default) =>
            Task.FromResult(++_reads >= 2 ? SecondCapabilities ?? capabilities : capabilities);
        public Task<IWindowsGdiDocument> StartDocAsync(WindowsPrintSubmission submission, IReadOnlyList<WindowsPrintRasterPage> pages, CancellationToken cancellationToken = default)
        {
            StartDocCount++;
            LastDocument = new FakeDocument(this, FailWritingPage);
            return Task.FromResult<IWindowsGdiDocument>(LastDocument);
        }
        public Task<PrintBackendJobStatus> GetStatusAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default)
        { LastReceipt = receipt; return Task.FromResult(PrintBackendJobStatus.Processing); }
        public Task<bool> CancelAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default)
        { LastReceipt = receipt; return Task.FromResult(true); }

        public sealed class FakeDocument(FakeNative owner, bool failWrite) : IWindowsGdiDocument
        {
            public string OsJobId => "job-from-startdoc";
            public int CompletedPages { get; private set; }
            public bool Disposed { get; private set; }
            public Task StartPageAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task WritePageAsync(WindowsPrintRasterPage page, CancellationToken cancellationToken = default) =>
                failWrite ? Task.FromException(new IOException("write failed")) : Task.CompletedTask;
            public Task EndPageAsync(CancellationToken cancellationToken = default) { CompletedPages++; return Task.CompletedTask; }
            public Task EndDocAsync(CancellationToken cancellationToken = default) { owner.EndDocCount++; return Task.CompletedTask; }
            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }
    }
}
