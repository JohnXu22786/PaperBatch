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
        var firstPdf = output.ToArray();
        var secondPdf = repeated.ToArray();
        if (!PdfOutputsMatchExceptTrailerIdPayloads(firstPdf, secondPdf))
            throw new InvalidOperationException($"Identical compiled inputs produced differences outside the generated trailer /ID string payloads. {DescribePdfDifference(firstPdf, secondPdf)}");
        var repeatedImage = RenderFirstPage(runtimePath, expectedHash, secondPdf, 200, 100);
        AssertColor(repeatedImage, width: 200, x: 20, y: 70, red: true);
        AssertColor(repeatedImage, width: 200, x: 120, y: 70, red: false);
        if (!SHA256.HashData(input).SequenceEqual(sourceHash)) throw new InvalidOperationException("Composition mutated source PDF bytes.");
    }

    internal static void PdfDifferenceDiagnosticsReportOffsetsWithoutPdfContent()
    {
        var differentLength = DescribePdfDifference(Encoding.ASCII.GetBytes("/ID [<1>]"), Encoding.ASCII.GetBytes("/ID [<12>]"));
        Assert.Contains("differing byte count: 3", differentLength);
        Assert.Contains("first differing offsets: 7,8,9", differentLength);
        Assert.Contains("confined to aligned /ID array: False", differentLength);

        var trailingByte = DescribePdfDifference(Encoding.ASCII.GetBytes("/ID [<1>]"), Encoding.ASCII.GetBytes("/ID [<1>]X"));
        Assert.Contains("differing byte count: 1", trailingByte);
        Assert.Contains("first differing offsets: 9", trailingByte);
        Assert.Contains("confined to aligned /ID array: False", trailingByte);

        var shiftedId = DescribePdfDifference(Encoding.ASCII.GetBytes("/ID [<1>]Z"), Encoding.ASCII.GetBytes("/ID  [<2>]"));
        Assert.Contains("confined to aligned /ID array: False", shiftedId);
        Assert.True(!shiftedId.Contains("<1>", StringComparison.Ordinal));
        Assert.True(!shiftedId.Contains("<2>", StringComparison.Ordinal));

        var longerKey = DescribePdfDifference(Encoding.ASCII.GetBytes("trailer\n<< /IDENTIFIER [<1>] >>"), Encoding.ASCII.GetBytes("trailer\n<< /IDENTIFIER [<2>] >>"));
        Assert.Contains("confined to aligned /ID array: False", longerKey);

        var streamKey = DescribePdfDifference(Encoding.ASCII.GetBytes("stream\n/ID [<1>]\nendstream\ntrailer\n<< >>"), Encoding.ASCII.GetBytes("stream\n/ID [<2>]\nendstream\ntrailer\n<< >>"));
        Assert.Contains("confined to aligned /ID array: False", streamKey);

        var commentedKey = DescribePdfDifference(Encoding.ASCII.GetBytes("trailer\n<< % /ID [<1>]\n/Size 1 >>"), Encoding.ASCII.GetBytes("trailer\n<< % /ID [<2>]\n/Size 1 >>"));
        Assert.Contains("confined to aligned /ID array: False", commentedKey);

        var trailerId = DescribePdfDifference(Encoding.ASCII.GetBytes("trailer\n<< /ID [<1><2>] >>\nstartxref\n0\n%%EOF"), Encoding.ASCII.GetBytes("trailer\n<< /ID [<3><4>] >>\nstartxref\n0\n%%EOF"));
        Assert.Contains("confined to aligned /ID array: True", trailerId);
        Assert.True(PdfOutputsMatchExceptTrailerIdPayloads(
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<11><22>] >>\nstartxref\n0\n%%EOF"),
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<33><44>] >>\nstartxref\n0\n%%EOF")));
        Assert.True(!PdfOutputsMatchExceptTrailerIdPayloads(
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<11><22>] /Size 1 >>\nstartxref\n0\n%%EOF"),
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<33><44>] /Size 2 >>\nstartxref\n0\n%%EOF")));

        var commentedTrailer = DescribePdfDifference(
            Encoding.ASCII.GetBytes("trailer\n<< /Size 1 >>\n% trailer\n<< /ID [<1>] >>\nstartxref\n0\n%%EOF"),
            Encoding.ASCII.GetBytes("trailer\n<< /Size 1 >>\n% trailer\n<< /ID [<2>] >>\nstartxref\n0\n%%EOF"));
        Assert.Contains("confined to aligned /ID array: False", commentedTrailer);

        var literalId = DescribePdfDifference(
            Encoding.ASCII.GetBytes("trailer\n<< /ID [(abc]def)(second)] >>\nstartxref\n0\n%%EOF"),
            Encoding.ASCII.GetBytes("trailer\n<< /ID [(abc]xyz)(second)] >>\nstartxref\n0\n%%EOF"));
        Assert.Contains("confined to aligned /ID array: True", literalId);

        var commentedArray = DescribePdfDifference(
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<1> % comment ]\n<2>] >>\nstartxref\n0\n%%EOF"),
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<1> % comment ]\n<3>] >>\nstartxref\n0\n%%EOF"));
        Assert.Contains("confined to aligned /ID array: True", commentedArray);
    }

    internal static void PdfOutputComparisonIgnoresOnlyTrailerIdPayloads()
    {
        Assert.True(PdfOutputsMatchExceptTrailerIdPayloads(
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<11><22>] /Size 1 >>\nstartxref\n0\n%%EOF"),
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<33><44>] /Size 1 >>\nstartxref\n0\n%%EOF")));
        Assert.True(!PdfOutputsMatchExceptTrailerIdPayloads(
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<11><22>] /Size 1 >>\nstartxref\n0\n%%EOF"),
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<33><44>] /Size 2 >>\nstartxref\n0\n%%EOF")));
        Assert.True(!PdfOutputsMatchExceptTrailerIdPayloads(
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<11><22>] >>\nstartxref\n0\n%%EOF"),
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<11><2>] >>\nstartxref\n0\n%%EOF")));
        Assert.True(!PdfOutputsMatchExceptTrailerIdPayloads(
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<GG><HH>] >>\nstartxref\n0\n%%EOF"),
            Encoding.ASCII.GetBytes("trailer\n<< /ID [<II><JJ>] >>\nstartxref\n0\n%%EOF")));
        Assert.True(PdfOutputsMatchExceptTrailerIdPayloads("same"u8.ToArray(), "same"u8.ToArray()));
    }

    private static bool PdfOutputsMatchExceptTrailerIdPayloads(byte[] first, byte[] second)
    {
        if (first.AsSpan().SequenceEqual(second)) return true;
        if (first.Length != second.Length) return false;
        var firstPayloads = PdfIdPayloadRanges(first);
        var secondPayloads = PdfIdPayloadRanges(second);
        if (firstPayloads is not { } firstRanges || secondPayloads is not { } secondRanges || firstRanges != secondRanges) return false;
        var differenceFound = false;
        for (var index = 0; index < first.Length; index++)
        {
            if (first[index] == second[index]) continue;
            if (IsInRange(index, firstRanges.First) || IsInRange(index, firstRanges.Second))
            {
                differenceFound = true;
                continue;
            }
            return false;
        }
        return differenceFound;
    }

    private static ((int Start, int End) First, (int Start, int End) Second)? PdfIdPayloadRanges(byte[] pdf)
    {
        var array = PdfIdValueRange(pdf);
        if (array is not { } idArray) return null;
        var index = SkipPdfTrivia(pdf, idArray.Start + 1);
        if (!TryReadHexStringPayload(pdf, idArray.End, ref index, out var first)) return null;
        index = SkipPdfTrivia(pdf, index);
        if (!TryReadHexStringPayload(pdf, idArray.End, ref index, out var second)) return null;
        index = SkipPdfTrivia(pdf, index);
        return index == idArray.End ? (first, second) : null;
    }

    private static bool TryReadHexStringPayload(byte[] pdf, int arrayEnd, ref int index, out (int Start, int End) payload)
    {
        payload = default;
        if (index >= arrayEnd || pdf[index] != '<' || (index + 1 < arrayEnd && pdf[index + 1] == '<')) return false;
        var close = pdf.AsSpan(index + 1, arrayEnd - index - 1).IndexOf((byte)'>');
        if (close < 0) return false;
        foreach (var value in pdf.AsSpan(index + 1, close))
            if (!IsPdfHexDigit(value) && value is not 0 and not 9 and not 10 and not 12 and not 13 and not 32) return false;
        payload = (index + 1, index + 1 + close);
        index += close + 2;
        return true;
    }

    private static bool IsInRange(int value, (int Start, int End) range) => value >= range.Start && value < range.End;

    private static bool IsPdfHexDigit(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

    private static string DescribePdfDifference(byte[] first, byte[] second)
    {
        var firstIdRange = PdfIdValueRange(first);
        var secondIdRange = PdfIdValueRange(second);
        var rangesAlign = firstIdRange is { } a && secondIdRange is { } b && a == b;
        var differenceCount = Math.Abs(first.Length - second.Length);
        var firstOffsets = new List<int>(12);
        var confinedToAlignedId = rangesAlign && first.Length == second.Length;
        var commonLength = Math.Min(first.Length, second.Length);
        for (var index = 0; index < commonLength; index++)
        {
            if (first[index] == second[index]) continue;
            differenceCount++;
            if (firstOffsets.Count < 12) firstOffsets.Add(index);
            if (!rangesAlign || firstIdRange is not { } range || index < range.Start || index > range.End)
                confinedToAlignedId = false;
        }
        if (first.Length != second.Length && firstOffsets.Count < 12) firstOffsets.Add(commonLength);
        if (differenceCount == 0) confinedToAlignedId = false;
        var offsets = string.Join(",", firstOffsets);
        return $"Output lengths are {first.Length} and {second.Length} bytes; differing byte count: {differenceCount}; first differing offsets: {offsets}; differences confined to aligned /ID array: {confinedToAlignedId}; SHA-256 values are {Convert.ToHexString(SHA256.HashData(first))} and {Convert.ToHexString(SHA256.HashData(second))}.";
    }

    private static (int Start, int End)? PdfIdValueRange(byte[] pdf)
    {
        var trailerMarker = Encoding.ASCII.GetBytes("trailer");
        var trailerStart = pdf.AsSpan().LastIndexOf(trailerMarker);
        if (trailerStart < 0) return null;

        if ((trailerStart > 0 && !IsPdfDelimiter(pdf[trailerStart - 1])) ||
            (trailerStart + trailerMarker.Length < pdf.Length && !IsPdfDelimiter(pdf[trailerStart + trailerMarker.Length]))) return null;
        var lineStart = trailerStart - 1;
        while (lineStart >= 0 && pdf[lineStart] is not 10 and not 13) lineStart--;
        for (var prefixIndex = lineStart + 1; prefixIndex < trailerStart; prefixIndex++)
            if (pdf[prefixIndex] is not 9 and not 32) return null;

        var index = SkipPdfTrivia(pdf, trailerStart + trailerMarker.Length);
        if (index + 1 >= pdf.Length || pdf[index] != '<' || pdf[index + 1] != '<') return null;
        index += 2;
        var dictionaryDepth = 1;
        var arrayDepth = 0;
        (int Start, int End)? idValueRange = null;
        while (index < pdf.Length)
        {
            index = SkipPdfTrivia(pdf, index);
            if (index >= pdf.Length) return null;
            var current = pdf[index];
            if (current == '(')
            {
                index = SkipPdfLiteralString(pdf, index);
                continue;
            }
            if (current == '<')
            {
                if (index + 1 < pdf.Length && pdf[index + 1] == '<')
                {
                    dictionaryDepth++;
                    index += 2;
                }
                else
                {
                    var close = pdf.AsSpan(index + 1).IndexOf((byte)'>');
                    if (close < 0) return null;
                    index += close + 2;
                }
                continue;
            }
            if (current == '>' && index + 1 < pdf.Length && pdf[index + 1] == '>')
            {
                dictionaryDepth--;
                if (dictionaryDepth == 0)
                {
                    var afterDictionary = SkipPdfTrivia(pdf, index + 2);
                    var startXref = Encoding.ASCII.GetBytes("startxref");
                    if (afterDictionary + startXref.Length > pdf.Length ||
                        !pdf.AsSpan(afterDictionary, startXref.Length).SequenceEqual(startXref) ||
                        (afterDictionary + startXref.Length < pdf.Length && !IsPdfDelimiter(pdf[afterDictionary + startXref.Length]))) return null;
                    return idValueRange;
                }
                index += 2;
                continue;
            }
            if (current == '[')
            {
                arrayDepth++;
                index++;
                continue;
            }
            if (current == ']')
            {
                arrayDepth = Math.Max(0, arrayDepth - 1);
                index++;
                continue;
            }
            if (current == '/')
            {
                var nameStart = index + 1;
                index = nameStart;
                while (index < pdf.Length && !IsPdfDelimiter(pdf[index])) index++;
                if (dictionaryDepth == 1 && arrayDepth == 0 && pdf.AsSpan(nameStart, index - nameStart).SequenceEqual("ID"u8))
                {
                    var valueStart = SkipPdfTrivia(pdf, index);
                    if (valueStart >= pdf.Length || pdf[valueStart] != '[') return null;
                    var valueEnd = FindPdfArrayEnd(pdf, valueStart);
                    if (valueEnd < 0) return null;
                    idValueRange = (valueStart, valueEnd);
                    index = valueEnd + 1;
                    continue;
                }
                continue;
            }
            index++;
        }
        return null;
    }

    private static int SkipPdfTrivia(byte[] pdf, int index)
    {
        while (index < pdf.Length)
        {
            if (pdf[index] is 0 or 9 or 10 or 12 or 13 or 32)
            {
                index++;
                continue;
            }
            if (pdf[index] == '%')
            {
                while (index < pdf.Length && pdf[index] is not 10 and not 13) index++;
                continue;
            }
            break;
        }
        return index;
    }

    private static int SkipPdfLiteralString(byte[] pdf, int index)
    {
        var depth = 0;
        while (index < pdf.Length)
        {
            var current = pdf[index++];
            if (current == '\\')
            {
                if (index < pdf.Length) index++;
            }
            else if (current == '(') depth++;
            else if (current == ')' && --depth == 0) return index;
        }
        return index;
    }

    private static int FindPdfArrayEnd(byte[] pdf, int start)
    {
        var depth = 0;
        for (var index = start; index < pdf.Length;)
        {
            var afterTrivia = SkipPdfTrivia(pdf, index);
            if (afterTrivia != index)
            {
                index = afterTrivia;
                continue;
            }
            if (pdf[index] == '(')
            {
                index = SkipPdfLiteralString(pdf, index);
                continue;
            }
            if (pdf[index] == '<' && (index + 1 >= pdf.Length || pdf[index + 1] != '<'))
            {
                var close = pdf.AsSpan(index + 1).IndexOf((byte)'>');
                if (close < 0) return -1;
                index += close + 2;
                continue;
            }
            if (pdf[index] == '[') depth++;
            else if (pdf[index] == ']' && --depth == 0) return index;
            index++;
        }
        return -1;
    }


    private static bool IsPdfDelimiter(byte value) =>
        value is 0 or 9 or 10 or 12 or 13 or 32 or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    internal static byte[] TwoPagePdf()
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
