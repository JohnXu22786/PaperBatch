using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Preparation;

namespace PaperBatch.Core.Rendering;

/// <summary>Reopens generated PDFs with one explicitly pinned PDFium runtime to verify page count.</summary>
public sealed class PdfiumArtifactInspector
    : IPdfArtifactInspector, IPdfPageMetadataInspector, IPdfArtifactPreviewRenderer, IStaticPdfContentInspector
{
    private const int MaximumPdfBytes = 512 * 1024 * 1024;
    private const string RuntimeOwnerMarkerName = ".paperbatch-pdfium-runtime-v1";
    private const string RuntimeOwnerMarker = "PaperBatch.PdfiumRuntime.v1\n";
    private static readonly object RuntimeGate = new();
    private readonly string _runtimePath;
    private readonly string _runtimeSha256;

    public PdfiumArtifactInspector(string explicitRuntimePath, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explicitRuntimePath);
        if (expectedSha256 is null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("An explicit PDFium SHA-256 pin is required.", nameof(expectedSha256));
        _runtimePath = Path.GetFullPath(explicitRuntimePath);
        _runtimeSha256 = expectedSha256.ToLowerInvariant();
    }

    /// <summary>Conservatively accepts only PDFs with no AcroForm/XFA form data or page annotations.</summary>
    public async Task<bool> HasStaticPageContentAsync(Stream pdf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!pdf.CanRead) throw new ArgumentException("The PDF stream must be readable.", nameof(pdf));
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await pdf.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > MaximumPdfBytes) throw new InvalidDataException("The PDF exceeds the inspection size limit.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        var content = bytes.ToArray();
        if (content.Length < 5 || content.AsSpan(0, Math.Min(content.Length, 1024)).IndexOf("%PDF-"u8) < 0)
            throw new InvalidDataException("The source artifact is not a PDF.");

        lock (RuntimeGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtimeDirectory = CreatePrivateRuntimeDirectory();
            IntPtr library = IntPtr.Zero;
            try
            {
                library = NativeLibrary.Load(StagePinnedRuntime(runtimeDirectory));
                var init = Export<NoArguments>(library, "FPDF_InitLibrary");
                var destroy = Export<NoArguments>(library, "FPDF_DestroyLibrary");
                var load = Export<LoadDocument>(library, "FPDF_LoadMemDocument");
                var close = Export<CloseDocument>(library, "FPDF_CloseDocument");
                var pageCount = Export<GetPageCount>(library, "FPDF_GetPageCount");
                var getFormType = Export<GetFormType>(library, "FPDF_GetFormType");
                var loadPage = Export<LoadPage>(library, "FPDF_LoadPage");
                var closePage = Export<ClosePage>(library, "FPDF_ClosePage");
                var getAnnotationCount = Export<GetAnnotationCount>(library, "FPDFPage_GetAnnotCount");
                var pinned = GCHandle.Alloc(content, GCHandleType.Pinned);
                var initialized = false;
                IntPtr document = IntPtr.Zero;
                try
                {
                    init();
                    initialized = true;
                    document = load(pinned.AddrOfPinnedObject(), content.Length, IntPtr.Zero);
                    if (document == IntPtr.Zero) throw new InvalidDataException("PDFium rejected the source PDF.");
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = pageCount(document);
                    if (count <= 0) throw new InvalidDataException("PDFium returned an invalid page count.");
                    if (getFormType(document) != 0) return false;
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var page = loadPage(document, index);
                        if (page == IntPtr.Zero) throw new InvalidDataException("PDFium could not inspect a source page.");
                        try
                        {
                            var annotations = getAnnotationCount(page);
                            if (annotations < 0) throw new InvalidDataException("PDFium returned an invalid annotation count.");
                            if (annotations > 0) return false;
                        }
                        finally { closePage(page); }
                    }
                    return true;
                }
                finally
                {
                    if (document != IntPtr.Zero) close(document);
                    pinned.Free();
                    if (initialized) destroy();
                }
            }
            finally
            {
                if (library != IntPtr.Zero) NativeLibrary.Free(library);
                TryDeleteRuntimeDirectory(runtimeDirectory);
            }
        }
    }

    public async Task<int> GetPageCountAsync(Stream pdf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!pdf.CanRead) throw new ArgumentException("The PDF stream must be readable.", nameof(pdf));
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
            throw new PlatformNotSupportedException("Pinned PDFium inspection is supported only on Linux and Windows.");

        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await pdf.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > MaximumPdfBytes) throw new InvalidDataException("The PDF exceeds the inspection size limit.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        var content = bytes.ToArray();
        if (content.Length < 5 || content.AsSpan(0, Math.Min(content.Length, 1024)).IndexOf("%PDF-"u8) < 0)
            throw new InvalidDataException("The generated artifact is not a PDF.");

        lock (RuntimeGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtimeDirectory = CreatePrivateRuntimeDirectory();
            IntPtr library = IntPtr.Zero;
            try
            {
                var stagedRuntime = StagePinnedRuntime(runtimeDirectory);
                library = NativeLibrary.Load(stagedRuntime);
                var init = Export<NoArguments>(library, "FPDF_InitLibrary");
                var destroy = Export<NoArguments>(library, "FPDF_DestroyLibrary");
                var load = Export<LoadDocument>(library, "FPDF_LoadMemDocument");
                var close = Export<CloseDocument>(library, "FPDF_CloseDocument");
                var pageCount = Export<GetPageCount>(library, "FPDF_GetPageCount");
                var pinned = GCHandle.Alloc(content, GCHandleType.Pinned);
                var initialized = false;
                IntPtr document = IntPtr.Zero;
                try
                {
                    init();
                    initialized = true;
                    document = load(pinned.AddrOfPinnedObject(), content.Length, IntPtr.Zero);
                    if (document == IntPtr.Zero) throw new InvalidDataException("PDFium rejected the generated PDF document.");
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = pageCount(document);
                    if (result <= 0) throw new InvalidDataException("PDFium returned an invalid page count.");
                    return result;
                }
                finally
                {
                    if (document != IntPtr.Zero) close(document);
                    pinned.Free();
                    if (initialized) destroy();
                }
            }
            finally
            {
                if (library != IntPtr.Zero) NativeLibrary.Free(library);
                TryDeleteRuntimeDirectory(runtimeDirectory);
            }
        }
    }

    public async Task<IReadOnlyList<PageSize>> GetPageSizesAsync(Stream pdf, CancellationToken cancellationToken)
        => (await InspectPageSizesAsync(pdf, pageNumber: null, cancellationToken).ConfigureAwait(false)).PageSizes;

    private async Task<(int PageCount, IReadOnlyList<PageSize> PageSizes)> InspectPageSizesAsync(
        Stream pdf, int? pageNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!pdf.CanRead) throw new ArgumentException("The PDF stream must be readable.", nameof(pdf));
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
            throw new PlatformNotSupportedException("Pinned PDFium inspection is supported only on Linux and Windows.");

        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await pdf.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > MaximumPdfBytes) throw new InvalidDataException("The PDF exceeds the inspection size limit.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        var content = bytes.ToArray();
        if (content.Length < 5 || content.AsSpan(0, Math.Min(content.Length, 1024)).IndexOf("%PDF-"u8) < 0)
            throw new InvalidDataException("The artifact is not a PDF.");

        lock (RuntimeGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtimeDirectory = CreatePrivateRuntimeDirectory();
            IntPtr library = IntPtr.Zero;
            try
            {
                library = NativeLibrary.Load(StagePinnedRuntime(runtimeDirectory));
                var init = Export<NoArguments>(library, "FPDF_InitLibrary");
                var destroy = Export<NoArguments>(library, "FPDF_DestroyLibrary");
                var load = Export<LoadDocument>(library, "FPDF_LoadMemDocument");
                var close = Export<CloseDocument>(library, "FPDF_CloseDocument");
                var countPages = Export<GetPageCount>(library, "FPDF_GetPageCount");
                var loadPage = Export<LoadPage>(library, "FPDF_LoadPage");
                var closePage = Export<ClosePage>(library, "FPDF_ClosePage");
                var getWidth = Export<GetPageDimension>(library, "FPDF_GetPageWidthF");
                var getHeight = Export<GetPageDimension>(library, "FPDF_GetPageHeightF");
                var pinned = GCHandle.Alloc(content, GCHandleType.Pinned);
                var initialized = false;
                IntPtr document = IntPtr.Zero;
                try
                {
                    init();
                    initialized = true;
                    document = load(pinned.AddrOfPinnedObject(), content.Length, IntPtr.Zero);
                    if (document == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the PDF artifact.");
                    var count = countPages(document);
                    if (count is <= 0 or > 1_000_000) throw new InvalidDataException("PDFium returned an invalid page count.");
                    if (pageNumber is { } requestedPage && requestedPage > count)
                        throw new ArgumentOutOfRangeException(nameof(pageNumber));
                    var pages = new List<PageSize>(pageNumber is null ? count : 1);
                    var firstPage = pageNumber is { } requested ? requested - 1 : 0;
                    var endPage = pageNumber is null ? count : firstPage + 1;
                    for (var index = firstPage; index < endPage; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var page = loadPage(document, index);
                        if (page == IntPtr.Zero) throw new InvalidDataException("PDFium could not read a PDF page.");
                        try
                        {
                            var size = new PageSize(getWidth(page), getHeight(page));
                            try { size.Validate(nameof(pdf)); }
                            catch (ArgumentOutOfRangeException exception) { throw new InvalidDataException("The PDF contains invalid page dimensions.", exception); }
                            pages.Add(size);
                        }
                        finally { closePage(page); }
                    }
                    return (count, pages);
                }
                finally
                {
                    if (document != IntPtr.Zero) close(document);
                    pinned.Free();
                    if (initialized) destroy();
                }
            }
            finally
            {
                if (library != IntPtr.Zero) NativeLibrary.Free(library);
                TryDeleteRuntimeDirectory(runtimeDirectory);
            }
        }
    }

    public async Task<PdfRasterPage> RenderPageAsync(Stream pdf, int pageNumber, int maximumDimension, CancellationToken cancellationToken)
        => await RenderPageAtDpiAsync(pdf, pageNumber, 72, maximumDimension, cancellationToken).ConfigureAwait(false);

    /// <summary>Renders one page at a bounded print DPI from the explicitly pinned PDFium runtime.</summary>
    public async Task<PdfRasterPage> RenderPageAtDpiAsync(Stream pdf, int pageNumber, int dpi, int maximumDimension, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!pdf.CanRead) throw new ArgumentException("The PDF stream must be readable.", nameof(pdf));
        if (pageNumber <= 0) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (dpi is < 72 or > 600) throw new ArgumentOutOfRangeException(nameof(dpi));
        if (maximumDimension is < 64 or > 16384) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        if (!pdf.CanSeek) throw new ArgumentException("The PDF stream must support seeking for bounded page preview.", nameof(pdf));
        var inspected = await InspectPageSizesAsync(pdf, pageNumber, cancellationToken).ConfigureAwait(false);
        var size = inspected.PageSizes[0];
        var scale = Math.Min(dpi / 72d, maximumDimension / Math.Max(size.WidthPoints, size.HeightPoints));
        var width = Math.Max(1, (int)Math.Round(size.WidthPoints * scale));
        var height = Math.Max(1, (int)Math.Round(size.HeightPoints * scale));
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];

        pdf.Position = 0;
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await pdf.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > MaximumPdfBytes) throw new InvalidDataException("The PDF exceeds the preview size limit.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        var content = bytes.ToArray();
        lock (RuntimeGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtimeDirectory = CreatePrivateRuntimeDirectory();
            IntPtr library = IntPtr.Zero;
            try
            {
                library = NativeLibrary.Load(StagePinnedRuntime(runtimeDirectory));
                var init = Export<NoArguments>(library, "FPDF_InitLibrary");
                var destroy = Export<NoArguments>(library, "FPDF_DestroyLibrary");
                var load = Export<LoadDocument>(library, "FPDF_LoadMemDocument");
                var close = Export<CloseDocument>(library, "FPDF_CloseDocument");
                var loadPage = Export<LoadPage>(library, "FPDF_LoadPage");
                var closePage = Export<ClosePage>(library, "FPDF_ClosePage");
                var createBitmap = Export<CreateBitmap>(library, "FPDFBitmap_CreateEx");
                var destroyBitmap = Export<DestroyBitmap>(library, "FPDFBitmap_Destroy");
                var renderPage = Export<RenderPage>(library, "FPDF_RenderPageBitmap");
                var pixelsPin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                var contentPin = GCHandle.Alloc(content, GCHandleType.Pinned);
                var initialized = false;
                IntPtr document = IntPtr.Zero;
                IntPtr page = IntPtr.Zero;
                IntPtr bitmap = IntPtr.Zero;
                try
                {
                    init();
                    initialized = true;
                    document = load(contentPin.AddrOfPinnedObject(), content.Length, IntPtr.Zero);
                    if (document == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the preview artifact.");
                    page = loadPage(document, pageNumber - 1);
                    if (page == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the requested preview page.");
                    bitmap = createBitmap(width, height, 4, pixelsPin.AddrOfPinnedObject(), stride);
                    if (bitmap == IntPtr.Zero) throw new InvalidDataException("PDFium could not allocate the bounded preview bitmap.");
                    cancellationToken.ThrowIfCancellationRequested();
                    renderPage(bitmap, page, 0, 0, width, height, 0, 0);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new PdfRasterPage(width, height, stride, pixels);
                }
                finally
                {
                    if (bitmap != IntPtr.Zero) destroyBitmap(bitmap);
                    if (page != IntPtr.Zero) closePage(page);
                    if (document != IntPtr.Zero) close(document);
                    contentPin.Free();
                    pixelsPin.Free();
                    if (initialized) destroy();
                }
            }
            finally
            {
                if (library != IntPtr.Zero) NativeLibrary.Free(library);
                TryDeleteRuntimeDirectory(runtimeDirectory);
            }
        }
    }

    private string CreatePrivateRuntimeDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "paperbatch-pdfium-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            SourceSnapshotCache.ApplyPrivateWindowsPermissions(directory);
        }
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The private PDFium runtime directory cannot be a reparse point.");
        var marker = Path.Combine(directory, RuntimeOwnerMarkerName);
        using (var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(output)) writer.Write(RuntimeOwnerMarker);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return directory;
    }

    private string StagePinnedRuntime(string directory)
    {
        if ((File.GetAttributes(_runtimePath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("The configured PDFium runtime must be a direct file.");
        var staged = Path.Combine(directory, OperatingSystem.IsWindows() ? "pdfium.dll" : "libpdfium.so");
        File.Copy(_runtimePath, staged, overwrite: false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var runtime = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(runtime)).ToLowerInvariant(), _runtimeSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The privately staged PDFium runtime does not match its configured digest.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, UnixFileMode.UserRead);
        return staged;
    }

    private static void TryDeleteRuntimeDirectory(string directory)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 &&
                File.ReadAllText(Path.Combine(directory, RuntimeOwnerMarkerName)) == RuntimeOwnerMarker)
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static T Export<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NoArguments();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LoadDocument(IntPtr data, int length, IntPtr password);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseDocument(IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPageCount(IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFormType(IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LoadPage(IntPtr document, int pageIndex);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ClosePage(IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetAnnotationCount(IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float GetPageDimension(IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateBitmap(int width, int height, int format, IntPtr firstScan, int stride);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyBitmap(IntPtr bitmap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RenderPage(IntPtr bitmap, IntPtr page, int startX, int startY,
        int sizeX, int sizeY, int rotate, int flags);
}
