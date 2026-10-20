using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperBatch.CompatibilityProbe;

namespace PaperBatch.Pdfium.Windows.Tests;

internal sealed record PdfiumSmokeRun(string Json, string Summary, bool CompositionPassed, string FormStatus);
internal delegate T ApiTracker<T>(string name, Func<T> call);

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

    public static IReadOnlyList<string> BoundedFormApiNames => FormExports;

    public static object[] CreateFormExportResults(IReadOnlySet<string> exports, IReadOnlyDictionary<string, string>? executionStatuses = null, bool exportsChecked = true) => FormExports
        .Select(name => (object)new
        {
            name,
            exportStatus = !exportsChecked ? "unchecked" : exports.Contains(name) ? "available" : "unavailable",
            executionStatus = !exportsChecked ? "not-run" : exports.Contains(name)
                ? executionStatuses?.GetValueOrDefault(name) ?? "not-run"
                : "unavailable"
        })
        .ToArray();

    public static object CreateBlockedFormFixture(string reason, IReadOnlySet<string> exports, IReadOnlyDictionary<string, string>? executionStatuses = null) => new
    {
        status = "blocked",
        reason,
        apiResults = CreateFormExportResults(exports, executionStatuses)
    };

    private static object[] BuildApiExportResults(IReadOnlySet<string> exports, IReadOnlyDictionary<string, string>? executionStatuses = null, bool exportsChecked = true) => CompositionExports.Concat(FormExports).Distinct(StringComparer.Ordinal)
        .Select(name => (object)new
        {
            name,
            exportStatus = !exportsChecked ? "unchecked" : exports.Contains(name) ? "available" : "unavailable",
            executionStatus = !exportsChecked ? "not-run" : exports.Contains(name) ? executionStatuses?.GetValueOrDefault(name) ?? "not-run" : "unavailable",
            requiredForComposition = CompositionExports.Contains(name, StringComparer.Ordinal),
            requiredForFormFixture = FormExports.Contains(name, StringComparer.Ordinal)
        })
        .ToArray();

    public static string CreateUnavailableReport(string reason, string expectedSha256) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0",
        status = "blocked",
        reason,
        assetSha256Expected = expectedSha256,
        apiExports = BuildApiExportResults(new HashSet<string>(StringComparer.Ordinal), exportsChecked: false),
        composition = new { status = "blocked", reason },
        formFixture = new
        {
            status = "not-run",
            reason,
            apiResults = CreateFormExportResults(new HashSet<string>(StringComparer.Ordinal), exportsChecked: false)
        },
        g1 = "open",
        productDistributionValidated = false,
        notValidated = NotValidated
    }, JsonOptions);

    public static PdfiumSmokeRun Run(string dllPath, string archivePath, string checkpointPath)
    {
        var compositionPassed = false;
        var compositionStarted = false;
        var formFixtureStarted = false;
        var formStatus = "not-run";
        IntPtr library = IntPtr.Zero;
        var exports = new List<object>();
        IReadOnlySet<string> exportNames = new HashSet<string>(StringComparer.Ordinal);
        var formExecutionStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var apiExecutionStatuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var exportsChecked = false;
        object? binary = null;
        object? composition = new { status = "blocked", reason = "Composition smoke did not complete." };
        object? form = new
        {
            status = "not-run",
            reason = "Form smoke did not complete.",
            apiResults = CreateFormExportResults(exportNames, formExecutionStatuses, exportsChecked: false)
        };
        string status = "blocked";
        string compositionStatus = "blocked";
        string? failure = null;

        T TrackApiResult<T>(string name, Func<T> call)
        {
            apiExecutionStatuses[name] = "started";
            SaveCheckpoint();
            try
            {
                var result = call();
                apiExecutionStatuses[name] = "verified";
                SaveCheckpoint();
                return result;
            }
            catch
            {
                apiExecutionStatuses[name] = "failed";
                SaveCheckpoint();
                throw;
            }
        }

        void TrackApi(string name, Action call) => TrackApiResult(name, () => { call(); return true; });
        IntPtr TrackPointer(string name, Func<IntPtr> call) => TrackApiResult(name, call);
        int TrackInt(string name, Func<int> call) => TrackApiResult(name, call);

        void SaveCheckpoint()
        {
            var checkpoint = new
            {
                apiExports = BuildApiExportResults(exportNames, apiExecutionStatuses),
                binary,
                composition,
                formFixtureStarted,
                formFixture = form,
                formApiResults = CreateFormExportResults(exportNames, formExecutionStatuses)
            };
            File.WriteAllText(checkpointPath, JsonSerializer.Serialize(checkpoint, JsonOptions));
        }

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
                exportNames = parsed.ExportNames;
                exportsChecked = true;
                foreach (var name in FormExports)
                {
                    formExecutionStatuses[name] = exportNames.Contains(name) ? "not-run" : "unavailable";
                }
                foreach (var name in CompositionExports)
                {
                    apiExecutionStatuses[name] = exportNames.Contains(name) ? "not-run" : "unavailable";
                }
                binary = new
                {
                    fileName = Path.GetFileName(fullDllPath),
                    sizeBytes = image.LongLength,
                    sha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
                    architecture = parsed.Architecture,
                    fileVersion = FileVersionInfo.GetVersionInfo(fullDllPath).FileVersion
                };
                SaveCheckpoint();
                exports.AddRange(BuildApiExportResults(parsed.ExportNames, apiExecutionStatuses));
                var missingComposition = CompositionExports.Where(name => !parsed.ExportNames.Contains(name)).ToArray();
                if (missingComposition.Length > 0)
                {
                    throw new MissingMethodException($"Required PDFium composition exports are absent: {string.Join(", ", missingComposition)}.");
                }
                library = NativeLibrary.Load(fullDllPath);
            }

            var native = new NativeApi(library);
            compositionStarted = true;
            compositionStatus = "started";
            composition = new { status = "started" };
            SaveCheckpoint();
            composition = RunComposition(native, TrackPointer, TrackInt, (name, call) => TrackApi(name, call));
            compositionPassed = true;
            compositionStatus = "verified";
            SaveCheckpoint();
            var missingForm = FormExports.Where(name => !NativeLibrary.TryGetExport(library, name, out _)).ToArray();
            if (missingForm.Length > 0)
            {
                formStatus = "unavailable";
                form = new
                {
                    status = "unavailable",
                    reason = "The bounded AcroForm/annotation fixture was not run because required API exports are missing.",
                    missingExports = missingForm,
                    apiResults = CreateFormExportResults(exportNames, formExecutionStatuses)
                };
            }
            else
            {
                formStatus = "blocked";
                form = CreateBlockedFormFixture(
                    "The bounded form fixture is not run because prior hosted Windows attempts against this exact binary raised a repeatable native access violation in the form API path. Individual exports are recorded, and all form API calls remain not-run.",
                    exportNames,
                    formExecutionStatuses);
                SaveCheckpoint();
            }

            status = formStatus == "verified" ? "verified" : "blocked";
        }
        catch (Exception exception)
        {
            status = "blocked";
            failure = exception.GetType().Name + ": " + exception.Message;
            if (!compositionPassed && compositionStarted)
            {
                compositionStatus = "failed";
                composition = new { status = "failed", reason = failure };
            }
            else if (formFixtureStarted && formStatus != "verified")
            {
                formStatus = "failed";
                foreach (var name in formExecutionStatuses.Keys.ToArray())
                {
                    if (formExecutionStatuses[name] == "started") formExecutionStatuses[name] = "failed";
                }
                form = new { status = "failed", reason = failure, apiResults = CreateFormExportResults(exportNames, formExecutionStatuses) };
            }
        }
        finally
        {
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
        }

        exports.Clear();
        exports.AddRange(BuildApiExportResults(exportNames, apiExecutionStatuses, exportsChecked));

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
        return new PdfiumSmokeRun(JsonSerializer.Serialize(report, JsonOptions), $"PDFium smoke status: {status}; composition: {compositionStatus}; form fixture: {formStatus}; G1: open.", compositionPassed, formStatus);
    }

    private static object RunComposition(NativeApi api, ApiTracker<IntPtr> trackPointer, ApiTracker<int> trackInt, Action<string, Action> trackVoid)
    {
        trackVoid("FPDF_InitLibrary", () => api.InitLibrary());
        try
        {
            using var sourceBytes = PinnedBytes.From(SyntheticPdf.CreateCompositionSource());
            using var insertBytes = PinnedBytes.From(SyntheticPdf.CreateCompositionInsert());
            var destination = trackPointer("FPDF_LoadMemDocument", () => api.LoadMemDocument(sourceBytes.Pointer, sourceBytes.Length, IntPtr.Zero));
            if (destination == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the generated destination fixture.");
            try
            {
                var source = trackPointer("FPDF_LoadMemDocument", () => api.LoadMemDocument(insertBytes.Pointer, insertBytes.Length, IntPtr.Zero));
                if (source == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the generated insert fixture.");
                try
                {
                var before = trackInt("FPDF_GetPageCount", () => api.GetPageCount(destination));
                var insertedCount = trackInt("FPDF_GetPageCount", () => api.GetPageCount(source));
                if (before != 1 || insertedCount != 1) throw new InvalidDataException("PDFium read an unexpected source fixture page count.");
                if (trackInt("FPDF_ImportPages", () => api.ImportPages(destination, source, "1", 1)) == 0) throw new InvalidOperationException("FPDF_ImportPages rejected the deterministic one-page insert.");
                var after = trackInt("FPDF_GetPageCount", () => api.GetPageCount(destination));
                if (after != 2) throw new InvalidDataException($"Expected two pages after import, got {after}.");
                var saved = SaveDocument(api, destination, trackInt);
                if (saved.Length == 0) throw new IOException("FPDF_SaveAsCopy returned an empty document.");
                using var reopenedBytes = PinnedBytes.From(saved);
                var reopened = trackPointer("FPDF_LoadMemDocument", () => api.LoadMemDocument(reopenedBytes.Pointer, reopenedBytes.Length, IntPtr.Zero));
                if (reopened == IntPtr.Zero) throw new InvalidDataException("PDFium could not reopen the composed in-memory document.");
                try
                {
                    var reopenedCount = trackInt("FPDF_GetPageCount", () => api.GetPageCount(reopened));
                    if (reopenedCount != 2) throw new InvalidDataException($"Expected two pages after saving/reopening, got {reopenedCount}.");
                    var pages = new[]
                    {
                        RenderAndCountColor(api, reopened, 0, "red", trackPointer, trackInt, trackVoid),
                        RenderAndCountColor(api, reopened, 1, "blue", trackPointer, trackInt, trackVoid)
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
                    trackVoid("FPDF_CloseDocument", () => api.CloseDocument(reopened));
                }
            }
            finally
            {
                trackVoid("FPDF_CloseDocument", () => api.CloseDocument(source));
            }
            }
            finally
            {
                trackVoid("FPDF_CloseDocument", () => api.CloseDocument(destination));
            }
        }
        finally
        {
            trackVoid("FPDF_DestroyLibrary", () => api.DestroyLibrary());
        }
    }

    private static object RenderAndCountColor(NativeApi api, IntPtr document, int pageIndex, string expected, ApiTracker<IntPtr> trackPointer, ApiTracker<int> trackInt, Action<string, Action> trackVoid)
    {
        var page = trackPointer("FPDF_LoadPage", () => api.LoadPage(document, pageIndex));
        if (page == IntPtr.Zero) throw new InvalidDataException($"PDFium could not reopen composed page {pageIndex}.");
        var bitmap = trackPointer("FPDFBitmap_Create", () => api.BitmapCreate(96, 96, 0));
        if (bitmap == IntPtr.Zero)
        {
            trackVoid("FPDF_ClosePage", () => api.ClosePage(page));
            throw new InvalidOperationException("FPDFBitmap_Create failed.");
        }

        try
        {
            trackVoid("FPDFBitmap_FillRect", () => api.BitmapFillRect(bitmap, 0, 0, 96, 96, 0xffffffff));
            trackVoid("FPDF_RenderPageBitmap", () => api.RenderPageBitmap(bitmap, page, 0, 0, 96, 96, 0, 0));
            var buffer = trackPointer("FPDFBitmap_GetBuffer", () => api.BitmapGetBuffer(bitmap));
            var stride = trackInt("FPDFBitmap_GetStride", () => api.BitmapGetStride(bitmap));
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
            trackVoid("FPDFBitmap_Destroy", () => api.BitmapDestroy(bitmap));
            trackVoid("FPDF_ClosePage", () => api.ClosePage(page));
        }
    }

    private static object RunFormFixture(IntPtr library, IReadOnlySet<string> exportNames, IDictionary<string, string> executionStatuses, Action checkpoint)
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
                executionStatuses["FPDF_GetFormType"] = "started";
                checkpoint();
                var formType = api.GetFormType(document);
                if (formType != 1) throw new InvalidDataException($"Expected AcroForm type 1, got {formType}.");
                executionStatuses["FPDF_GetFormType"] = "verified";
                checkpoint();
                var page = api.LoadPage(document, 0);
                if (page == IntPtr.Zero) throw new InvalidDataException("PDFium could not open the generated form page.");
                try
                {
                    executionStatuses["FPDFPage_GetAnnotCount"] = "started";
                    checkpoint();
                    var annotationCount = api.PageGetAnnotCount(page);
                    if (annotationCount != 1) throw new InvalidDataException($"Expected one widget annotation, got {annotationCount}.");
                    executionStatuses["FPDFPage_GetAnnotCount"] = "verified";
                    checkpoint();
                    executionStatuses["FPDFPage_GetAnnot"] = "started";
                    checkpoint();
                    var annotation = api.PageGetAnnot(page, 0);
                    if (annotation == IntPtr.Zero) throw new InvalidDataException("PDFium could not read the generated widget annotation.");
                    executionStatuses["FPDFPage_GetAnnot"] = "verified";
                    checkpoint();
                    int subtype;
                    try
                    {
                        executionStatuses["FPDFAnnot_GetSubtype"] = "started";
                        checkpoint();
                        subtype = api.AnnotGetSubtype(annotation);
                        if (subtype != 20) throw new InvalidDataException($"Expected widget annotation subtype 20, got {subtype}.");
                        executionStatuses["FPDFAnnot_GetSubtype"] = "verified";
                        checkpoint();
                    }
                    finally
                    {
                        executionStatuses["FPDFPage_CloseAnnot"] = "started";
                        checkpoint();
                        api.PageCloseAnnot(page, annotation);
                        executionStatuses["FPDFPage_CloseAnnot"] = "verified";
                        checkpoint();
                    }

                    executionStatuses["FPDFDOC_InitFormFillEnvironment"] = "blocked";
                    checkpoint();
                    return new
                    {
                        status = "blocked",
                        reason = "AcroForm and widget annotation parsing passed. The form-fill interaction path caused a repeatable access violation in hosted Windows CI, so interaction APIs remain unavailable and were not rerun.",
                        formType,
                        annotationCount,
                        annotationSubtype = subtype,
                        apiResults = CreateFormExportResults(exportNames, new Dictionary<string, string>(executionStatuses))
                    };
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

    private static byte[] SaveDocument(NativeApi api, IntPtr document, ApiTracker<int> trackInt)
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
            if (trackInt("FPDF_SaveAsCopy", () => api.SaveAsCopy(document, fileWritePointer, 0)) == 0) throw new IOException("FPDF_SaveAsCopy failed.", callbackError);
            if (callbackError is not null) throw new IOException("PDFium's save callback failed.", callbackError);
            GC.KeepAlive(writer);
            return output.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(fileWritePointer);
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PageGetAnnotCountDelegate(IntPtr page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PageGetAnnotDelegate(IntPtr page, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AnnotGetSubtypeDelegate(IntPtr annotation);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseAnnotDelegate(IntPtr page, IntPtr annotation);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InvalidateCallback(IntPtr info, IntPtr page, double left, double top, double right, double bottom);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteBlockCallback(IntPtr fileWrite, IntPtr data, uint size);
}
