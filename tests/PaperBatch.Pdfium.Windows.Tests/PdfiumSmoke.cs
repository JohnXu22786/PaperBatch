using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperBatch.CompatibilityProbe;

namespace PaperBatch.Pdfium.Windows.Tests;

internal sealed record PdfiumSmokeRun(string Json, string Summary, bool CompositionPassed, string FormStatus);

internal static class PdfiumSmoke
{
    private const string ReleaseTag = "chromium/8111";
    private const string ReleaseDate = "2026-10-19";
    private const string AssetName = "pdfium-win-x64.tgz";
    private const string AssetUrl = "https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8111/pdfium-win-x64.tgz";
    private const string SourceRepository = "https://github.com/bblanchon/pdfium-binaries";
    private const string LicenseReference = "https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8111/LICENSE";

    private static readonly string[] CompositionExports =
    [
        "FPDF_InitLibrary", "FPDF_DestroyLibrary", "FPDF_LoadMemDocument", "FPDF_CloseDocument",
        "FPDF_GetPageCount", "FPDF_ImportPages", "FPDF_SaveAsCopy", "FPDF_LoadPage", "FPDF_ClosePage",
        "FPDFBitmap_Create", "FPDFBitmap_FillRect", "FPDF_RenderPageBitmap", "FPDFBitmap_GetBuffer",
        "FPDFBitmap_GetStride", "FPDFBitmap_Destroy"
    ];

    private static readonly string[] FormExports =
    [
        "FPDF_GetFormType", "FPDFDOC_InitFormFillEnvironment", "FPDFDOC_ExitFormFillEnvironment",
        "FORM_OnAfterLoadPage", "FORM_OnBeforeClosePage", "FPDFPage_HasFormFieldAtPoint",
        "FPDFPage_GetAnnotCount", "FPDFPage_GetAnnot", "FPDFAnnot_GetSubtype", "FPDFPage_CloseAnnot"
    ];

    public static string CreateUnavailableReport(string reason, string expectedSha256) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0",
        status = "blocked",
        reason,
        assetSha256Expected = expectedSha256,
        g1 = "open",
        productDistributionValidated = false,
        notValidated = NotValidated
    }, JsonOptions);

    public static PdfiumSmokeRun Run(string dllPath, string archivePath)
    {
        var compositionPassed = false;
        var formStatus = "unavailable";
        IntPtr library = IntPtr.Zero;
        var exports = new List<object>();
        object? binary = null;
        object? composition = new { status = "blocked", reason = "Composition smoke did not complete." };
        object? form = new { status = "unavailable", reason = "Form smoke did not complete." };
        string status = "blocked";
        string? failure = null;

        try
        {
            var archiveSha256 = HashFile(archivePath);
            if (!string.Equals(archiveSha256, SyntheticPdf.ExpectedReleaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded release archive SHA-256 does not match the pinned digest; the DLL was not loaded.");
            }

            var fullDllPath = Path.GetFullPath(dllPath);
            using (var stream = File.OpenRead(fullDllPath))
            {
                if (stream.Length <= 0 || stream.Length > 512L * 1024 * 1024)
                {
                    throw new InvalidDataException("The extracted PDFium DLL has an invalid size.");
                }

                var image = new byte[checked((int)stream.Length)];
                stream.ReadExactly(image);
                if (stream.ReadByte() >= 0) throw new IOException("The PDFium DLL changed while its identity was measured.");
                var parsed = PeImageParser.Parse(image);
                if (parsed.Architecture != "x64") throw new InvalidDataException($"Expected an x64 PDFium DLL, got {parsed.Architecture}.");
                binary = new
                {
                    fileName = Path.GetFileName(fullDllPath),
                    sizeBytes = image.LongLength,
                    sha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
                    architecture = parsed.Architecture,
                    fileVersion = FileVersionInfo.GetVersionInfo(fullDllPath).FileVersion
                };
                exports.AddRange(ExportResults(parsed.ExportNames));
                var missingComposition = CompositionExports.Where(name => !parsed.ExportNames.Contains(name)).ToArray();
                if (missingComposition.Length > 0)
                {
                    throw new MissingMethodException($"Required PDFium composition exports are absent: {string.Join(", ", missingComposition)}.");
                }
                library = NativeLibrary.Load(fullDllPath);
            }

            var native = new NativeApi(library);
            composition = RunComposition(native);
            compositionPassed = true;
            var missingForm = FormExports.Where(name => !NativeLibrary.TryGetExport(library, name, out _)).ToArray();
            if (missingForm.Length > 0)
            {
                formStatus = "unavailable";
                form = new { status = "unavailable", reason = "This binary does not export every bounded AcroForm/annotation API required by the fixture.", missingExports = missingForm };
            }
            else
            {
                form = RunFormFixture(library);
                formStatus = "verified";
            }

            status = formStatus == "verified" ? "verified" : "blocked";
        }
        catch (Exception exception)
        {
            status = "blocked";
            failure = exception.GetType().Name + ": " + exception.Message;
            if (!compositionPassed) composition = new { status = "failed", reason = failure };
            else if (formStatus != "verified")
            {
                formStatus = "failed";
                form = new { status = "failed", reason = failure };
            }
        }
        finally
        {
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
        }

        var report = new
        {
            schemaVersion = "1.0",
            generatedAtUtc = DateTimeOffset.UtcNow,
            status,
            source = new
            {
                repository = SourceRepository,
                releaseTag = ReleaseTag,
                releaseDate = ReleaseDate,
                asset = AssetName,
                assetUrl = AssetUrl,
                assetSha256Expected = SyntheticPdf.ExpectedReleaseSha256,
                buildProvenance = "Community-provided weekly build. The publisher states the project is unaffiliated with Google and Foxit.",
                licensingReferences = new[]
                {
                    "https://github.com/bblanchon/pdfium-binaries/blob/master/LICENSE",
                    LicenseReference,
                    "https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8111/third_party/"
                }
            },
            binary,
            apiExports = exports,
            composition,
            formFixture = form,
            failure,
            g1 = "open",
            productDistributionValidated = false,
            notValidated = NotValidated
        };
        return new PdfiumSmokeRun(JsonSerializer.Serialize(report, JsonOptions), $"PDFium smoke status: {status}; composition: {(compositionPassed ? "verified" : "failed")}; form fixture: {formStatus}; G1: open.", compositionPassed, formStatus);
    }

    private static object RunComposition(NativeApi api)
    {
        api.InitLibrary();
        try
        {
            using var sourceBytes = PinnedBytes.From(SyntheticPdf.CreateCompositionSource());
            using var insertBytes = PinnedBytes.From(SyntheticPdf.CreateCompositionInsert());
            var destination = api.LoadMemDocument(sourceBytes.Pointer, sourceBytes.Length, IntPtr.Zero);
            if (destination == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the generated destination fixture.");
            try
            {
                var source = api.LoadMemDocument(insertBytes.Pointer, insertBytes.Length, IntPtr.Zero);
                if (source == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the generated insert fixture.");
                try
                {
                var before = api.GetPageCount(destination);
                var insertedCount = api.GetPageCount(source);
                if (before != 1 || insertedCount != 1) throw new InvalidDataException("PDFium read an unexpected source fixture page count.");
                if (api.ImportPages(destination, source, "1", 1) == 0) throw new InvalidOperationException("FPDF_ImportPages rejected the deterministic one-page insert.");
                var after = api.GetPageCount(destination);
                if (after != 2) throw new InvalidDataException($"Expected two pages after import, got {after}.");
                var saved = SaveDocument(api, destination);
                if (saved.Length == 0) throw new IOException("FPDF_SaveAsCopy returned an empty document.");
                using var reopenedBytes = PinnedBytes.From(saved);
                var reopened = api.LoadMemDocument(reopenedBytes.Pointer, reopenedBytes.Length, IntPtr.Zero);
                if (reopened == IntPtr.Zero) throw new InvalidDataException("PDFium could not reopen the composed in-memory document.");
                try
                {
                    var reopenedCount = api.GetPageCount(reopened);
                    if (reopenedCount != 2) throw new InvalidDataException($"Expected two pages after saving/reopening, got {reopenedCount}.");
                    var pages = new[]
                    {
                        RenderAndCountColor(api, reopened, 0, "red"),
                        RenderAndCountColor(api, reopened, 1, "blue")
                    };
                    return new
                    {
                        status = "verified",
                        api = new[] { "FPDF_LoadMemDocument", "FPDF_ImportPages", "FPDF_SaveAsCopy", "FPDF_GetPageCount", "FPDF_RenderPageBitmap" },
                        sourcePageCount = before,
                        importedPageCount = insertedCount,
                        savedSizeBytes = saved.Length,
                        savedSha256 = Convert.ToHexString(SHA256.HashData(saved)).ToLowerInvariant(),
                        reopenedPageCount = reopenedCount,
                        renderedPages = pages
                    };
                }
                finally
                {
                    api.CloseDocument(reopened);
                }
            }
            finally
            {
                api.CloseDocument(source);
            }
            }
            finally
            {
                api.CloseDocument(destination);
            }
        }
        finally
        {
            api.DestroyLibrary();
        }
    }

    private static object RenderAndCountColor(NativeApi api, IntPtr document, int pageIndex, string expected)
    {
        var page = api.LoadPage(document, pageIndex);
        if (page == IntPtr.Zero) throw new InvalidDataException($"PDFium could not reopen composed page {pageIndex}.");
        var bitmap = api.BitmapCreate(96, 96, 0);
        if (bitmap == IntPtr.Zero)
        {
            api.ClosePage(page);
            throw new InvalidOperationException("FPDFBitmap_Create failed.");
        }

        try
        {
            api.BitmapFillRect(bitmap, 0, 0, 96, 96, 0xffffffff);
            api.RenderPageBitmap(bitmap, page, 0, 0, 96, 96, 0, 0);
            var buffer = api.BitmapGetBuffer(bitmap);
            var stride = api.BitmapGetStride(bitmap);
            if (buffer == IntPtr.Zero || stride < 96 * 4) throw new InvalidDataException("PDFium returned an invalid rendered bitmap.");
            var pixels = new byte[stride * 96];
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
            var matchingPixels = 0;
            for (var y = 0; y < 96; y++)
            {
                for (var x = 0; x < 96; x++)
                {
                    var offset = y * stride + x * 4;
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    if (expected == "red" && red > 180 && green < 70 && blue < 70) matchingPixels++;
                    if (expected == "blue" && blue > 180 && green < 70 && red < 70) matchingPixels++;
                }
            }

            if (matchingPixels < 100) throw new InvalidDataException($"Page {pageIndex} did not render enough {expected} fixture pixels.");
            return new
            {
                pageIndex,
                expectedColor = expected,
                matchingPixelCount = matchingPixels,
                bitmapSha256 = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant(),
                bitmapWidth = 96,
                bitmapHeight = 96
            };
        }
        finally
        {
            api.BitmapDestroy(bitmap);
            api.ClosePage(page);
        }
    }

    private static object RunFormFixture(IntPtr library)
    {
        var api = new FormApi(library);
        api.InitLibrary();
        try
        {
            using var bytes = PinnedBytes.From(SyntheticPdf.CreateTextFieldFixture());
            var document = api.LoadMemDocument(bytes.Pointer, bytes.Length, IntPtr.Zero);
            if (document == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the generated AcroForm fixture.");
            try
            {
                var formType = api.GetFormType(document);
                if (formType != 1) throw new InvalidDataException($"Expected AcroForm type 1, got {formType}.");
                var page = api.LoadPage(document, 0);
                if (page == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the generated form page.");
                try
                {
                    var annotationCount = api.PageGetAnnotCount(page);
                    if (annotationCount != 1) throw new InvalidDataException($"Expected one widget annotation, got {annotationCount}.");
                    var annotation = api.PageGetAnnot(page, 0);
                    if (annotation == IntPtr.Zero) throw new InvalidDataException("PDFium could not read the generated widget annotation.");
                    int subtype;
                    try { subtype = api.AnnotGetSubtype(annotation); }
                    finally { api.PageCloseAnnot(page, annotation); }
                    if (subtype != 20) throw new InvalidDataException($"Expected widget annotation subtype 20, got {subtype}.");

                    var invalidator = new InvalidateCallback((_, _, _, _, _, _) => { });
                    var info = new FormFillInfoV1
                    {
                        Version = 1,
                        Release = IntPtr.Zero,
                        Invalidate = Marshal.GetFunctionPointerForDelegate(invalidator),
                        Reserved = new IntPtr[96]
                    };
                    var infoSize = Marshal.SizeOf<FormFillInfoV1>();
                    var infoPointer = Marshal.AllocHGlobal(infoSize);
                    Marshal.StructureToPtr(info, infoPointer, false);
                    try
                    {
                        var form = api.InitFormFillEnvironment(document, infoPointer);
                        if (form == IntPtr.Zero) throw new InvalidOperationException("FPDFDOC_InitFormFillEnvironment failed for the generated AcroForm fixture.");
                        try
                        {
                            api.FormOnAfterLoadPage(page, form);
                            var fieldType = api.PageHasFormFieldAtPoint(form, page, 50, 40);
                            if (fieldType != 6) throw new InvalidDataException($"Expected a text field (6) at the fixture coordinate, got {fieldType}.");
                            return new
                            {
                                status = "verified",
                                api = new[] { "FPDF_GetFormType", "FPDFDOC_InitFormFillEnvironment", "FORM_OnAfterLoadPage", "FPDFPage_HasFormFieldAtPoint", "FORM_OnBeforeClosePage", "FPDFDOC_ExitFormFillEnvironment", "FPDFPage_GetAnnotCount", "FPDFPage_GetAnnot", "FPDFAnnot_GetSubtype" },
                                formType,
                                annotationCount,
                                annotationSubtype = subtype,
                                pointQuery = new { x = 50, y = 40, fieldType }
                            };
                        }
                        finally
                        {
                            api.FormOnBeforeClosePage(page, form);
                            api.ExitFormFillEnvironment(form);
                            GC.KeepAlive(invalidator);
                        }
                    }
                    finally
                    {
                        Marshal.DestroyStructure<FormFillInfoV1>(infoPointer);
                        Marshal.FreeHGlobal(infoPointer);
                    }
                }
                finally
                {
                    api.ClosePage(page);
                }
            }
            finally
            {
                api.CloseDocument(document);
            }
        }
        finally
        {
            api.DestroyLibrary();
        }
    }

    private static byte[] SaveDocument(NativeApi api, IntPtr document)
    {
        using var output = new MemoryStream();
        Exception? callbackError = null;
        WriteBlockCallback writer = (_, data, size) =>
        {
            try
            {
                if (size > int.MaxValue) throw new IOException("PDFium save callback block exceeded the supported size.");
                var buffer = new byte[(int)size];
                Marshal.Copy(data, buffer, 0, buffer.Length);
                output.Write(buffer);
                return 1;
            }
            catch (Exception exception)
            {
                callbackError = exception;
                return 0;
            }
        };
        var fileWrite = new FileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(writer) };
        var fileWritePointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileWrite>());
        Marshal.StructureToPtr(fileWrite, fileWritePointer, false);
        try
        {
            if (api.SaveAsCopy(document, fileWritePointer, 0) == 0) throw new IOException("FPDF_SaveAsCopy failed.", callbackError);
            if (callbackError is not null) throw new IOException("PDFium's save callback failed.", callbackError);
            GC.KeepAlive(writer);
            return output.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(fileWritePointer);
        }
    }

    private static IEnumerable<object> ExportResults(IReadOnlySet<string> exports)
    {
        foreach (var name in CompositionExports.Concat(FormExports).Distinct(StringComparer.Ordinal))
        {
            var compositionRequired = CompositionExports.Contains(name, StringComparer.Ordinal);
            var formFixtureRequired = FormExports.Contains(name, StringComparer.Ordinal);
            yield return new
            {
                name,
                exportStatus = exports.Contains(name) ? "available" : "unavailable",
                requiredForComposition = compositionRequired,
                requiredForFormFixture = formFixtureRequired
            };
        }
    }

    private static T GetExport<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static readonly string[] NotValidated =
    [
        "The product's bundled PDFium distribution or production dependency pin.",
        "An official Google or Foxit build or a complete source-to-binary provenance policy.",
        "Dynamic XFA, JavaScript, all form field types, or general AcroForm compatibility.",
        "Microsoft Office or WPS coexistence.",
        "A safe Windows GDI queue/driver route, job-ID behavior, or physical printer output."
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private sealed class NativeApi
    {
        public readonly InitLibraryDelegate InitLibrary;
        public readonly VoidDelegate DestroyLibrary;
        public readonly LoadMemDocumentDelegate LoadMemDocument;
        public readonly CloseDocumentDelegate CloseDocument;
        public readonly GetPageCountDelegate GetPageCount;
        public readonly ImportPagesDelegate ImportPages;
        public readonly SaveAsCopyDelegate SaveAsCopy;
        public readonly LoadPageDelegate LoadPage;
        public readonly ClosePageDelegate ClosePage;
        public readonly BitmapCreateDelegate BitmapCreate;
        public readonly BitmapFillRectDelegate BitmapFillRect;
        public readonly RenderPageBitmapDelegate RenderPageBitmap;
        public readonly BitmapGetBufferDelegate BitmapGetBuffer;
        public readonly BitmapGetStrideDelegate BitmapGetStride;
        public readonly BitmapDestroyDelegate BitmapDestroy;

        public NativeApi(IntPtr library)
        {
            InitLibrary = Get<InitLibraryDelegate>(library, "FPDF_InitLibrary");
            DestroyLibrary = Get<VoidDelegate>(library, "FPDF_DestroyLibrary");
            LoadMemDocument = Get<LoadMemDocumentDelegate>(library, "FPDF_LoadMemDocument");
            CloseDocument = Get<CloseDocumentDelegate>(library, "FPDF_CloseDocument");
            GetPageCount = Get<GetPageCountDelegate>(library, "FPDF_GetPageCount");
            ImportPages = Get<ImportPagesDelegate>(library, "FPDF_ImportPages");
            SaveAsCopy = Get<SaveAsCopyDelegate>(library, "FPDF_SaveAsCopy");
            LoadPage = Get<LoadPageDelegate>(library, "FPDF_LoadPage");
            ClosePage = Get<ClosePageDelegate>(library, "FPDF_ClosePage");
            BitmapCreate = Get<BitmapCreateDelegate>(library, "FPDFBitmap_Create");
            BitmapFillRect = Get<BitmapFillRectDelegate>(library, "FPDFBitmap_FillRect");
            RenderPageBitmap = Get<RenderPageBitmapDelegate>(library, "FPDF_RenderPageBitmap");
            BitmapGetBuffer = Get<BitmapGetBufferDelegate>(library, "FPDFBitmap_GetBuffer");
            BitmapGetStride = Get<BitmapGetStrideDelegate>(library, "FPDFBitmap_GetStride");
            BitmapDestroy = Get<BitmapDestroyDelegate>(library, "FPDFBitmap_Destroy");
        }

        private static T Get<T>(IntPtr library, string name) where T : Delegate => GetExport<T>(library, name);
    }

    private sealed class FormApi
    {
        public readonly InitLibraryDelegate InitLibrary;
        public readonly VoidDelegate DestroyLibrary;
        public readonly LoadMemDocumentDelegate LoadMemDocument;
        public readonly CloseDocumentDelegate CloseDocument;
        public readonly LoadPageDelegate LoadPage;
        public readonly ClosePageDelegate ClosePage;
        public readonly GetFormTypeDelegate GetFormType;
        public readonly InitFormFillEnvironmentDelegate InitFormFillEnvironment;
        public readonly ExitFormFillEnvironmentDelegate ExitFormFillEnvironment;
        public readonly FormPageDelegate FormOnAfterLoadPage;
        public readonly FormPageDelegate FormOnBeforeClosePage;
        public readonly HasFormFieldAtPointDelegate PageHasFormFieldAtPoint;
        public readonly PageGetAnnotCountDelegate PageGetAnnotCount;
        public readonly PageGetAnnotDelegate PageGetAnnot;
        public readonly AnnotGetSubtypeDelegate AnnotGetSubtype;
        public readonly CloseAnnotDelegate PageCloseAnnot;

        public FormApi(IntPtr library)
        {
            InitLibrary = GetExport<InitLibraryDelegate>(library, "FPDF_InitLibrary");
            DestroyLibrary = GetExport<VoidDelegate>(library, "FPDF_DestroyLibrary");
            LoadMemDocument = GetExport<LoadMemDocumentDelegate>(library, "FPDF_LoadMemDocument");
            CloseDocument = GetExport<CloseDocumentDelegate>(library, "FPDF_CloseDocument");
            LoadPage = GetExport<LoadPageDelegate>(library, "FPDF_LoadPage");
            ClosePage = GetExport<ClosePageDelegate>(library, "FPDF_ClosePage");
            GetFormType = GetExport<GetFormTypeDelegate>(library, "FPDF_GetFormType");
            InitFormFillEnvironment = GetExport<InitFormFillEnvironmentDelegate>(library, "FPDFDOC_InitFormFillEnvironment");
            ExitFormFillEnvironment = GetExport<ExitFormFillEnvironmentDelegate>(library, "FPDFDOC_ExitFormFillEnvironment");
            FormOnAfterLoadPage = GetExport<FormPageDelegate>(library, "FORM_OnAfterLoadPage");
            FormOnBeforeClosePage = GetExport<FormPageDelegate>(library, "FORM_OnBeforeClosePage");
            PageHasFormFieldAtPoint = GetExport<HasFormFieldAtPointDelegate>(library, "FPDFPage_HasFormFieldAtPoint");
            PageGetAnnotCount = GetExport<PageGetAnnotCountDelegate>(library, "FPDFPage_GetAnnotCount");
            PageGetAnnot = GetExport<PageGetAnnotDelegate>(library, "FPDFPage_GetAnnot");
            AnnotGetSubtype = GetExport<AnnotGetSubtypeDelegate>(library, "FPDFAnnot_GetSubtype");
            PageCloseAnnot = GetExport<CloseAnnotDelegate>(library, "FPDFPage_CloseAnnot");
        }
    }

    private sealed class PinnedBytes : IDisposable
    {
        private readonly IntPtr _pointer;
        public IntPtr Pointer => _pointer;
        public int Length { get; }

        private PinnedBytes(byte[] bytes)
        {
            Length = bytes.Length;
            _pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, _pointer, bytes.Length);
        }

        public static PinnedBytes From(byte[] bytes) => new(bytes);
        public void Dispose() => Marshal.FreeHGlobal(_pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileWrite { public int Version; public IntPtr WriteBlock; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormFillInfoV1
    {
        public int Version;
        public IntPtr Release;
        public IntPtr Invalidate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 96)] public IntPtr[] Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InitLibraryDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LoadMemDocumentDelegate(IntPtr data, int size, IntPtr password);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseDocumentDelegate(IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPageCountDelegate(IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int ImportPagesDelegate(IntPtr destination, IntPtr source, [MarshalAs(UnmanagedType.LPStr)] string pageRange, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SaveAsCopyDelegate(IntPtr document, IntPtr fileWrite, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LoadPageDelegate(IntPtr document, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ClosePageDelegate(IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr BitmapCreateDelegate(int width, int height, int alpha);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BitmapFillRectDelegate(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RenderPageBitmapDelegate(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr BitmapGetBufferDelegate(IntPtr bitmap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BitmapGetStrideDelegate(IntPtr bitmap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BitmapDestroyDelegate(IntPtr bitmap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFormTypeDelegate(IntPtr document);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr InitFormFillEnvironmentDelegate(IntPtr document, IntPtr formInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ExitFormFillEnvironmentDelegate(IntPtr form);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FormPageDelegate(IntPtr page, IntPtr form);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HasFormFieldAtPointDelegate(IntPtr form, IntPtr page, double x, double y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PageGetAnnotCountDelegate(IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PageGetAnnotDelegate(IntPtr page, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AnnotGetSubtypeDelegate(IntPtr annotation);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseAnnotDelegate(IntPtr page, IntPtr annotation);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InvalidateCallback(IntPtr info, IntPtr page, double left, double top, double right, double bottom);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteBlockCallback(IntPtr fileWrite, IntPtr data, uint size);
}
