using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PaperBatch.Core.Layout;

internal static class PdfCompositionTests
{
    internal static void CompositionRequiresExplicitStaticSourceInspection()
    {
        var plan = LayoutCompiler.Compile([new LayoutDocument("doc", [new PageSize(612, 792)], "Letter")], new());
        var adapter = new PdfiumPageCompositionAdapter("/missing/pdfium.dll", new string('0', 64));
        using var output = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => adapter.ComposePdfAsync(
            plan,
            [new PdfCompositionSource("doc", new byte[] { 1, 2, 3 })],
            output).GetAwaiter().GetResult());
        Assert.Equal(0, output.Length);
    }

    internal static void CompositionRequiresEveryCompiledSourceAndOutputSize()
    {
        var plan = LayoutCompiler.Compile([new LayoutDocument("doc", [new PageSize(612, 792)], "Letter")], new());
        var adapter = new PdfiumPageCompositionAdapter("/missing/pdfium.dll", new string('0', 64));
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => adapter.ComposePdfAsync(
            plan,
            [new PdfCompositionSource("other", new byte[] { 1 }, StaticPageContentVerified: true)],
            output).GetAwaiter().GetResult());

        var invalid = plan with { Pages = ImmutableArray.Create(plan.Pages[0] with { OutputSize = null }) };
        Assert.Throws<ArgumentException>(() => adapter.ComposePdfAsync(
            invalid,
            [new PdfCompositionSource("doc", new byte[] { 1 }, StaticPageContentVerified: true)],
            output).GetAwaiter().GetResult());
    }

    internal static void CompositionRequiresAnExplicitDigestAndRuntimePath()
    {
        Assert.Throws<ArgumentException>(() => new PdfiumPageCompositionAdapter(" ", new string('0', 64)));
        Assert.Throws<ArgumentException>(() => new PdfiumPageCompositionAdapter("/tmp/pdfium.dll", "not-a-digest"));
    }

    internal static void NativeCompositionRendersTwoUpFixture()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_PATH")!;
        var expectedHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_SHA256")
            ?? throw new InvalidOperationException("PAPERBATCH_PDFIUM_TEST_SHA256 is required with the native test runtime.");
        var input = TwoPagePdf();
        var sourceHash = SHA256.HashData(input);
        var plan = LayoutCompiler.Compile(
            [new LayoutDocument("fixture", [new(100, 100), new(100, 100)], "Test")],
            new(NUp: NUpMode.Two, SheetSize: new(200, 100)));
        var adapter = new PdfiumPageCompositionAdapter(runtimePath, expectedHash);
        var stalePlan = LayoutCompiler.Compile(
            [new LayoutDocument("fixture", [new(101, 100), new(101, 100)], "Test")],
            new(NUp: NUpMode.Two, SheetSize: new(200, 100)));
        using var rejectedOutput = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => adapter.ComposePdfAsync(stalePlan,
            [new PdfCompositionSource("fixture", input, StaticPageContentVerified: true)], rejectedOutput).GetAwaiter().GetResult());
        Assert.Equal(0, rejectedOutput.Length);

        using var output = new MemoryStream();
        adapter.ComposePdfAsync(plan, [new PdfCompositionSource("fixture", input, StaticPageContentVerified: true)], output).GetAwaiter().GetResult();
        if (!SHA256.HashData(input).SequenceEqual(sourceHash)) throw new InvalidOperationException("Composition mutated source PDF bytes.");
        if (output.Length == 0) throw new InvalidOperationException("Composition produced no PDF bytes.");
        var image = RenderFirstPage(runtimePath, expectedHash, output.ToArray(), 200, 100);
        AssertColor(image, width: 200, x: 20, y: 70, red: true);
        AssertColor(image, width: 200, x: 120, y: 70, red: false);

        var overlappingRanges = LayoutCompiler.Compile(
            [new LayoutDocument("fixture", [new(100, 100), new(100, 100)], "Test")],
            new(NUp: NUpMode.Two, SheetSize: new(200, 100), Ranges: [new(1, 1), new(1, 1)]));
        using var duplicatedPageOutput = new MemoryStream();
        adapter.ComposePdfAsync(overlappingRanges,
            [new PdfCompositionSource("fixture", input, StaticPageContentVerified: true)], duplicatedPageOutput).GetAwaiter().GetResult();
        var duplicatedPageImage = RenderFirstPage(runtimePath, expectedHash, duplicatedPageOutput.ToArray(), 200, 100);
        AssertColor(duplicatedPageImage, width: 200, x: 20, y: 70, red: true);
        AssertColor(duplicatedPageImage, width: 200, x: 120, y: 70, red: true);

        using var repeated = new MemoryStream();
        adapter.ComposePdfAsync(plan, [new PdfCompositionSource("fixture", input, StaticPageContentVerified: true)], repeated).GetAwaiter().GetResult();
        if (!output.ToArray().SequenceEqual(repeated.ToArray())) throw new InvalidOperationException("Identical compiled inputs did not produce deterministic PDF bytes.");
    }

    private static byte[] TwoPagePdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ProcSet [/PDF] >> /Contents 4 0 R >>",
            "<< /Length 30 >>\nstream\n1 0 0 rg 10 20 20 20 re f\nq Q\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ProcSet [/PDF] >> /Contents 6 0 R >>",
            "<< /Length 30 >>\nstream\n0 0 1 rg 10 20 20 20 re f\nq Q\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] RenderFirstPage(string libraryPath, string expectedHash, byte[] pdf, int width, int height)
    {
        Assert.Equal(expectedHash.ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(libraryPath))).ToLowerInvariant());
        var library = NativeLibrary.Load(Path.GetFullPath(libraryPath));
        var init = Export<NoArgs>(library, "FPDF_InitLibrary");
        var destroy = Export<NoArgs>(library, "FPDF_DestroyLibrary");
        var load = Export<LoadMem>(library, "FPDF_LoadMemDocument");
        var closeDocument = Export<CloseHandle>(library, "FPDF_CloseDocument");
        var loadPage = Export<LoadPage>(library, "FPDF_LoadPage");
        var closePage = Export<CloseHandle>(library, "FPDF_ClosePage");
        var createBitmap = Export<CreateBitmap>(library, "FPDFBitmap_Create");
        var fill = Export<FillRect>(library, "FPDFBitmap_FillRect");
        var render = Export<RenderPage>(library, "FPDF_RenderPageBitmap");
        var getBuffer = Export<GetBuffer>(library, "FPDFBitmap_GetBuffer");
        var getStride = Export<GetStride>(library, "FPDFBitmap_GetStride");
        var destroyBitmap = Export<CloseHandle>(library, "FPDFBitmap_Destroy");
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        IntPtr document = IntPtr.Zero, page = IntPtr.Zero, bitmap = IntPtr.Zero;
        try
        {
            init();
            document = load(pin.AddrOfPinnedObject(), pdf.Length, IntPtr.Zero);
            if (document == IntPtr.Zero) throw new InvalidDataException("PDFium could not reopen the generated fixture.");
            page = loadPage(document, 0);
            if (page == IntPtr.Zero) throw new InvalidDataException("PDFium could not load the generated first page.");
            bitmap = createBitmap(width, height, 1);
            if (bitmap == IntPtr.Zero) throw new InvalidDataException("PDFium could not create the verification bitmap.");
            fill(bitmap, 0, 0, width, height, 0xFFFFFFFF);
            render(bitmap, page, 0, 0, width, height, 0, 0);
            var stride = getStride(bitmap);
            var length = checked(stride * height);
            var pixels = new byte[length];
            Marshal.Copy(getBuffer(bitmap), pixels, 0, length);
            return pixels;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) destroyBitmap(bitmap);
            if (page != IntPtr.Zero) closePage(page);
            if (document != IntPtr.Zero) closeDocument(document);
            pin.Free();
            destroy();
            NativeLibrary.Free(library);
        }
    }

    private static void AssertColor(byte[] pixels, int width, int x, int y, bool red)
    {
        var offset = checked(y * width * 4 + x * 4);
        var blue = pixels[offset];
        var green = pixels[offset + 1];
        var redValue = pixels[offset + 2];
        if (!(red ? redValue > 240 && green < 10 && blue < 10 : blue > 240 && green < 10 && redValue < 10))
            throw new InvalidOperationException($"Expected {(red ? "red" : "blue")} at ({x},{y}), got BGRA {blue},{green},{redValue},{pixels[offset + 3]}.");
    }

    private static T Export<T>(IntPtr library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LoadMem(IntPtr bytes, int length, IntPtr password);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseHandle(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LoadPage(IntPtr document, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateBitmap(int width, int height, int alpha);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RenderPage(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetBuffer(IntPtr bitmap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetStride(IntPtr bitmap);
}
