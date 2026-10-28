using System.Security.Cryptography;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Rendering;

namespace PaperBatch.Core.Printing;

/// <summary>A rendered page whose geometry is derived from the submitted PDF bytes.</summary>
public sealed record WindowsPrintRasterPage(int Number, PageSize PdfSize, PdfRasterPage Raster);

/// <summary>Must use the explicitly configured, digest-pinned PDFium runtime.</summary>
public interface IWindowsPrintPdfRasterizer
{
    Task<IReadOnlyList<WindowsPrintRasterPage>> RasterizeAsync(
        Stream pdf, int expectedPageCount, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WindowsPrintRasterPage>> RasterizePagesAsync(
        Stream pdf, int expectedPageCount, IReadOnlyList<int> pageNumbers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pageNumbers);
        if (!pageNumbers.SequenceEqual(Enumerable.Range(1, expectedPageCount)))
            throw new NotSupportedException("This PDF rasterizer does not support selecting pages from a composed artifact.");
        return RasterizeAsync(pdf, expectedPageCount, cancellationToken);
    }
}

/// <summary>PDFium rasterization through a caller supplied runtime path and SHA-256 pin.</summary>
public sealed class PinnedPdfiumWindowsPrintRasterizer : IWindowsPrintPdfRasterizer
{
    private const int PrintDpi = 300;
    private const int MaximumDimension = 16384;
    private const int MaximumPageCount = 500;
    private const long MaximumRasterBytes = 512L * 1024 * 1024;
    private readonly PdfiumArtifactInspector _inspector;

    public PinnedPdfiumWindowsPrintRasterizer(string explicitRuntimePath, string expectedSha256) =>
        _inspector = new PdfiumArtifactInspector(explicitRuntimePath, expectedSha256);

    public async Task<IReadOnlyList<WindowsPrintRasterPage>> RasterizeAsync(
        Stream pdf, int expectedPageCount, CancellationToken cancellationToken = default) =>
        await RasterizePagesAsync(pdf, expectedPageCount, Enumerable.Range(1, expectedPageCount).ToArray(), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<WindowsPrintRasterPage>> RasterizePagesAsync(
        Stream pdf, int expectedPageCount, IReadOnlyList<int> pageNumbers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(pageNumbers);
        if (!pdf.CanRead || !pdf.CanSeek) throw new ArgumentException("The PDF stream must be readable and seekable.", nameof(pdf));
        pdf.Position = 0;
        if (expectedPageCount is < 1 or > MaximumPageCount)
            throw new InvalidDataException("The final PDF page count is outside the bounded print raster limit.");
        if (pageNumbers.Count is < 1 or > MaximumPageCount || pageNumbers.Any(page => page < 1 || page > expectedPageCount) ||
            !pageNumbers.SequenceEqual(pageNumbers.Distinct().Order()))
            throw new InvalidDataException("The selected final PDF pages are invalid.");
        if (await _inspector.GetPageCountAsync(pdf, cancellationToken).ConfigureAwait(false) != expectedPageCount)
            throw new InvalidDataException("PDFium page count differs from the immutable artifact before rasterization.");
        pdf.Position = 0;
        var sizes = await _inspector.GetPageSizesAsync(pdf, cancellationToken).ConfigureAwait(false);
        if (sizes.Count != expectedPageCount) throw new InvalidDataException("PDFium page geometry differs from the immutable artifact page count.");
        var pages = new List<WindowsPrintRasterPage>(pageNumbers.Count);
        long totalRasterBytes = 0;
        foreach (var pageNumber in pageNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = pageNumber - 1;
            var size = sizes[index];
            size.Validate($"PDF page {index + 1}");
            var scale = PrintDpi / 72d;
            var width = checked((int)Math.Round(size.WidthPoints * scale));
            var height = checked((int)Math.Round(size.HeightPoints * scale));
            if (width > MaximumDimension || height > MaximumDimension)
                throw new InvalidDataException($"PDF page {index + 1} exceeds the 300 DPI print raster dimension limit.");
            var projectedBytes = checked((long)width * height * 4);
            if (projectedBytes > MaximumRasterBytes || totalRasterBytes + projectedBytes > MaximumRasterBytes)
                throw new InvalidDataException("The final PDF exceeds the bounded aggregate print raster memory limit.");
            pdf.Position = 0;
            var raster = await _inspector.RenderPageAtDpiAsync(pdf, index + 1, PrintDpi, MaximumDimension, cancellationToken).ConfigureAwait(false);
            totalRasterBytes = checked(totalRasterBytes + raster.BgraPixels.LongLength);
            if (totalRasterBytes > MaximumRasterBytes)
                throw new InvalidDataException("The final PDF exceeds the bounded aggregate print raster memory limit.");
            var expectedRatio = sizes[index].WidthPoints / sizes[index].HeightPoints;
            var rasterRatio = (double)raster.Width / raster.Height;
            if (Math.Abs(expectedRatio - rasterRatio) > Math.Max(0.01, expectedRatio * 0.001))
                throw new InvalidDataException("PDFium raster geometry differs from the immutable artifact page geometry.");
            pages.Add(new WindowsPrintRasterPage(pageNumber, sizes[index], raster));
        }
        return pages;
    }
}

/// <summary>GDI and Winspool calls are kept behind this narrow, injectable boundary.</summary>
public interface IWindowsGdiNativeAdapter
{
    Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default);
    Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(PrinterCapabilitySnapshot enumeratedQueue, CancellationToken cancellationToken = default) =>
        GetCapabilitiesAsync(enumeratedQueue.QueueIdentity, cancellationToken);
    Task<IWindowsGdiDocument> StartDocAsync(
        WindowsPrintSubmission submission, IReadOnlyList<WindowsPrintRasterPage> pages,
        CancellationToken cancellationToken = default);
    Task<PrintBackendJobStatus> GetStatusAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default);
}

public interface IWindowsGdiDocument : IAsyncDisposable
{
    string OsJobId { get; }
    Task StartPageAsync(CancellationToken cancellationToken = default);
    Task WritePageAsync(WindowsPrintRasterPage page, CancellationToken cancellationToken = default);
    Task EndPageAsync(CancellationToken cancellationToken = default);
    Task EndDocAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Safe orchestration for a Windows GDI backend. PDF parsing and all capability validation happen
/// before StartDoc; after StartDoc, every failure is conservatively reported as possibly submitted.
/// </summary>
public sealed class WindowsGdiPrintBackend(
    IWindowsGdiNativeAdapter native,
    IWindowsPrintPdfRasterizer rasterizer) : IWindowsPrintBackend
{
    private const long MaximumPdfBytes = 512L * 1024 * 1024;
    private const int MaximumPageCount = 500;

    public Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default) =>
        native.GetCapabilitiesAsync(queueIdentity, cancellationToken);

    public async Task<OsJobReceipt> SubmitAsync(WindowsPrintSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var queue = submission.Options.QueueIdentity;
        PrinterCapabilitySnapshot initial;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            initial = await native.GetCapabilitiesAsync(queue, cancellationToken).ConfigureAwait(false);
            EnsureOptions(submission, initial);
        }
        catch (PrintSubmissionException) { throw; }
        catch (Exception error)
        {
            throw new PrintSubmissionException("Queue capability preflight failed before StartDoc.", false, error);
        }

        byte[] bytes;
        try { bytes = await ReadExactArtifactAsync(submission, cancellationToken).ConfigureAwait(false); }
        catch (PrintSubmissionException) { throw; }
        catch (Exception error)
        {
            throw new PrintSubmissionException("Final artifact verification failed before StartDoc.", false, error);
        }
        IReadOnlyList<WindowsPrintRasterPage> pages;
        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            var pageNumbers = submission.FinalPageNumbers;
            ArgumentNullException.ThrowIfNull(pageNumbers);
            if (pageNumbers.Count is < 1 or > MaximumPageCount || pageNumbers.Any(page => page < 1 || page > submission.Artifact.PageCount) ||
                !pageNumbers.SequenceEqual(pageNumbers.Distinct().Order()))
                throw new InvalidDataException("Selected final PDF pages must be unique, ordered, and within the immutable artifact.");
            var segmentPageNumbers = submission.Segment.FinalPageNumbers;
            if (!segmentPageNumbers.IsDefaultOrEmpty)
            {
                if (!pageNumbers.SequenceEqual(segmentPageNumbers))
                    throw new InvalidDataException("Selected final PDF pages must match the segment's persisted page ownership.");
            }
            else if (!pageNumbers.SequenceEqual(Enumerable.Range(1, submission.Artifact.PageCount)))
            {
                throw new InvalidDataException("A segment without persisted page ownership may submit only the complete final PDF.");
            }
            pages = await rasterizer.RasterizePagesAsync(content, submission.Artifact.PageCount, pageNumbers, cancellationToken).ConfigureAwait(false);
            ValidatePages(pages, pageNumbers);
            EnsureSingleAutoOrientation(submission.Segment.PrintSettings, pages);
        }
        catch (PrintSubmissionException) { throw; }
        catch (Exception error)
        {
            throw new PrintSubmissionException("PDF preflight/rasterization failed before StartDoc.", mayHaveReachedOs: false, error);
        }

        PrinterCapabilitySnapshot current;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = await native.GetCapabilitiesAsync(queue, cancellationToken).ConfigureAwait(false);
            EnsureSameCapabilities(initial, current);
            EnsureOptions(submission, current);
        }
        catch (PrintSubmissionException) { throw; }
        catch (Exception error)
        {
            throw new PrintSubmissionException("Final queue capability preflight failed before StartDoc.", false, error);
        }

        IWindowsGdiDocument? document = null;
        var startDocReturned = false;
        var endDocSucceeded = false;
        try
        {
            document = await native.StartDocAsync(submission, pages, cancellationToken).ConfigureAwait(false);
            startDocReturned = true;
            if (string.IsNullOrWhiteSpace(document.OsJobId))
                throw new InvalidDataException("GDI StartDoc returned an empty job identifier.");
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await document.StartPageAsync(cancellationToken).ConfigureAwait(false);
                await document.WritePageAsync(page, cancellationToken).ConfigureAwait(false);
                await document.EndPageAsync(cancellationToken).ConfigureAwait(false);
            }
            await document.EndDocAsync(cancellationToken).ConfigureAwait(false);
            endDocSucceeded = true;
            return new OsJobReceipt(queue, document.OsJobId, submission.RunNonce,
                submission.SubmittedAtUtc, current.DriverName, current.PortName, current.Fingerprint);
        }
        catch (PrintSubmissionException error) when (!startDocReturned && !error.MayHaveReachedOs)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new PrintSubmissionException("GDI submission failed after StartDoc may have begun.", mayHaveReachedOs: true, error);
        }
        finally
        {
            if (document is not null)
            {
                try { await document.DisposeAsync().ConfigureAwait(false); }
                catch when (endDocSucceeded) { }
            }
        }
    }

    public Task<PrintBackendJobStatus> GetStatusAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default)
    {
        ValidateReceipt(receipt);
        return native.GetStatusAsync(receipt, cancellationToken);
    }

    public Task<bool> CancelAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default)
    {
        ValidateReceipt(receipt);
        return native.CancelAsync(receipt, cancellationToken);
    }

    private static async Task<byte[]> ReadExactArtifactAsync(WindowsPrintSubmission submission, CancellationToken cancellationToken)
    {
        if (!submission.PdfContent.CanRead) throw new PrintSubmissionException("The final artifact stream is not readable.", false);
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await submission.PdfContent.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > MaximumPdfBytes || bytes.Length + count > submission.Artifact.LengthBytes)
                throw new PrintSubmissionException("The final PDF bytes exceed the persisted artifact length.", false);
            await bytes.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        var content = bytes.ToArray();
        var digest = Convert.ToHexStringLower(SHA256.HashData(content));
        if (content.LongLength != submission.Artifact.LengthBytes ||
            !string.Equals(digest, submission.Artifact.Sha256, StringComparison.Ordinal))
            throw new PrintSubmissionException("The PDF bytes do not match the immutable final artifact identity.", false);
        return content;
    }

    private static void EnsureOptions(WindowsPrintSubmission submission, PrinterCapabilitySnapshot snapshot)
    {
        try { _ = WindowsPrintJobOptions.Map(submission.Options.QueueIdentity, snapshot, submission.Segment.PrintSettings); }
        catch (PrintCapabilityException error) { throw new PrintSubmissionException(error.Message, false, error); }
        if (!string.Equals(submission.Options.CapabilityFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
            throw new PrintSubmissionException("The queue capability fingerprint changed before StartDoc.", false);
    }

    private static void EnsureSameCapabilities(PrinterCapabilitySnapshot initial, PrinterCapabilitySnapshot current)
    {
        if (!string.Equals(initial.QueueIdentity, current.QueueIdentity, StringComparison.Ordinal) ||
            !string.Equals(initial.DriverName, current.DriverName, StringComparison.Ordinal) ||
            !string.Equals(initial.PortName, current.PortName, StringComparison.Ordinal) ||
            !string.Equals(initial.Fingerprint, current.Fingerprint, StringComparison.Ordinal))
            throw new PrintSubmissionException("Queue identity or capabilities changed before StartDoc.", false);
    }

    private static void ValidatePages(IReadOnlyList<WindowsPrintRasterPage> pages, IReadOnlyList<int> expectedPageNumbers)
    {
        if (pages.Count != expectedPageNumbers.Count) throw new InvalidDataException("PDFium raster page count differs from the selected final artifact pages.");
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            if (page.Number != expectedPageNumbers[index] || !double.IsFinite(page.PdfSize.WidthPoints) || !double.IsFinite(page.PdfSize.HeightPoints) ||
                page.PdfSize.WidthPoints <= 0 || page.PdfSize.HeightPoints <= 0 || page.PdfSize.WidthPoints > 14400 || page.PdfSize.HeightPoints > 14400 ||
                page.Raster.Width <= 0 || page.Raster.Height <= 0 || page.Raster.Width > 16384 || page.Raster.Height > 16384 ||
                page.Raster.Stride < checked(page.Raster.Width * 4) ||
                checked((long)page.Raster.Stride * page.Raster.Height) > 512L * 1024 * 1024 ||
                page.Raster.BgraPixels.LongLength < checked((long)page.Raster.Stride * page.Raster.Height))
                throw new InvalidDataException("PDFium returned invalid page geometry or raster data.");
            var pdfRatio = page.PdfSize.WidthPoints / page.PdfSize.HeightPoints;
            var rasterRatio = (double)page.Raster.Width / page.Raster.Height;
            if (Math.Abs(pdfRatio - rasterRatio) > Math.Max(0.01, pdfRatio * 0.001))
                throw new InvalidDataException("PDFium raster geometry does not match the immutable PDF page geometry.");
        }
    }

    private static void EnsureSingleAutoOrientation(
        PrintSettings settings, IReadOnlyList<WindowsPrintRasterPage> pages)
    {
        if (settings.Orientation != Orientation.Auto || pages.Count < 2) return;
        var firstIsLandscape = pages[0].PdfSize.WidthPoints > pages[0].PdfSize.HeightPoints;
        if (pages.Skip(1).Any(page =>
                (page.PdfSize.WidthPoints > page.PdfSize.HeightPoints) != firstIsLandscape))
            throw new PrintSubmissionException(
                "Auto orientation cannot submit a GDI document with mixed page orientations.",
                mayHaveReachedOs: false);
    }

    private static void ValidateReceipt(OsJobReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(receipt.QueueIdentity) || string.IsNullOrWhiteSpace(receipt.OsJobId) ||
            string.IsNullOrWhiteSpace(receipt.RunNonce))
            throw new ArgumentException("A queue-scoped OS job receipt is required.", nameof(receipt));
    }
}
