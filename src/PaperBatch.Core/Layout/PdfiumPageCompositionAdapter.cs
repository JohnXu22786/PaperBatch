using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PaperBatch.Core.Layout;

/// <summary>PDFium composition for immutable PDFs containing static page content.</summary>
/// <remarks>
/// The runtime is an explicit, digest-pinned input. This adapter never probes arbitrary DLL paths
/// and edits only documents loaded from private byte copies; it never writes to source files.
/// Interactive annotations and forms must be rejected by the caller before composition.
/// </remarks>
public sealed class PdfiumPageCompositionAdapter : IPageCompositionAdapter
{
    private const int MaximumSourceBytes = 256 * 1024 * 1024;
    private const long MaximumTotalSourceBytes = 512L * 1024 * 1024;
    private const long MaximumOutputBytes = 512L * 1024 * 1024;
    private readonly string _runtimePath;
    private readonly string _expectedSha256;
    public string RuntimeSha256 => _expectedSha256;
    private static readonly object RuntimeGate = new();
    private static readonly string[] RequiredExports =
    [
        "FPDF_InitLibrary", "FPDF_DestroyLibrary", "FPDF_LoadMemDocument", "FPDF_CloseDocument",
        "FPDF_GetPageCount", "FPDF_ImportPages", "FPDF_SaveAsCopy", "FPDFPage_New",
        "FPDFPage_Delete", "FPDF_LoadPage", "FPDF_ClosePage", "FPDFPage_CountObjects",
        "FPDFPage_GetObject", "FPDFPage_RemoveObject", "FPDFPage_InsertObject",
        "FPDFPageObj_Transform", "FPDFPage_GenerateContent", "FPDF_GetPageWidthF", "FPDF_GetPageHeightF"
    ];

    public PdfiumPageCompositionAdapter(string explicitRuntimePath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(explicitRuntimePath)) throw new ArgumentException("An explicit PDFium runtime path is required.", nameof(explicitRuntimePath));
        if (expectedSha256 is null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)) throw new ArgumentException("A SHA-256 digest is required.", nameof(expectedSha256));
        _runtimePath = Path.GetFullPath(explicitRuntimePath);
        _expectedSha256 = expectedSha256.ToLowerInvariant();
    }

    public Task ComposePdfAsync(LayoutCompilation compilation, IEnumerable<PdfCompositionSource> sources, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        if (!compilation.Succeeded || compilation.Pages.IsDefaultOrEmpty) throw new InvalidOperationException("A successful nonempty layout compilation is required.");

        var sourceMap = sources.ToDictionary(source => source.ItemId, StringComparer.Ordinal);
        if (sourceMap.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value.PdfBytes.IsEmpty || pair.Value.PdfBytes.Length > MaximumSourceBytes))
            throw new ArgumentException("Source PDFs must have unique item IDs and contain between 1 byte and 256 MiB.", nameof(sources));
        if (sourceMap.Values.Sum(source => (long)source.PdfBytes.Length) > MaximumTotalSourceBytes)
            throw new ArgumentException("Combined source PDFs cannot exceed 512 MiB.", nameof(sources));
        var requiredItems = compilation.Pages.SelectMany(page => page.Placements)
            .Where(placement => placement.Kind == LayoutPageKind.Source).Select(placement => placement.ItemId).Distinct(StringComparer.Ordinal);
        if (requiredItems.Any(item => !sourceMap.ContainsKey(item))) throw new ArgumentException("A compiled source item has no matching PDF bytes.", nameof(sources));
        for (var index = 0; index < compilation.Pages.Length; index++)
        {
            var page = compilation.Pages[index];
            if (page.Number != index + 1 || page.OutputSize is null) throw new ArgumentException("Every physical face must have a contiguous page number and explicit output page size.", nameof(compilation));
            try { page.OutputSize.Validate(nameof(compilation)); }
            catch (ArgumentOutOfRangeException error) { throw new ArgumentException("A physical face has invalid output dimensions.", nameof(compilation), error); }
        }
        if (sourceMap.Values.Any(source => !source.StaticPageContentVerified))
            throw new NotSupportedException("PDF composition requires source inspection that confirms static page content with no interactive annotations or forms.");

        lock (RuntimeGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var runtime = new PdfiumRuntime(_runtimePath, _expectedSha256);
            var output = Compose(runtime, compilation, sourceMap, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(output);
        }
        return Task.CompletedTask;
    }

    private static byte[] Compose(PdfiumRuntime api, LayoutCompilation compilation, Dictionary<string, PdfCompositionSource> sources, CancellationToken cancellationToken)
    {
        api.InitLibrary();
        try
        {
            // An existing source document acts as the destination container; all its pages are
            // removed in memory before imports. A second handle owns the immutable import source.
            var firstItem = compilation.Pages.SelectMany(page => page.Placements).First(placement => placement.Kind == LayoutPageKind.Source).ItemId;
            var destinationBytes = sources[firstItem].PdfBytes.ToArray();
            using var destinationPin = PinnedBytes.Create(destinationBytes);
            var destinationDocument = api.LoadMemDocument(destinationPin.Pointer, destinationBytes.Length, IntPtr.Zero);
            if (destinationDocument == IntPtr.Zero) throw new InvalidDataException("PDFium could not open a source PDF.");

            var sourceHandles = new Dictionary<string, (IntPtr Document, PinnedBytes Pin)>(StringComparer.Ordinal);
            var importedPageIndex = new Dictionary<(int FaceNumber, int PlacementIndex), int>();
            var importedCount = 0;
            var outputPages = new List<IntPtr>(compilation.Pages.Length);
            try
            {
                foreach (var item in sources.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = sources[item].PdfBytes.ToArray();
                    var pin = PinnedBytes.Create(bytes);
                    var handle = api.LoadMemDocument(pin.Pointer, bytes.Length, IntPtr.Zero);
                    if (handle == IntPtr.Zero) { pin.Dispose(); throw new InvalidDataException($"PDFium could not open source item '{item}'."); }
                    sourceHandles.Add(item, (handle, pin));
                }

                var originalPages = api.GetPageCount(destinationDocument);
                if (originalPages < 0) throw new InvalidDataException("PDFium returned an invalid source page count.");

                foreach (var face in compilation.Pages)
                for (var placementIndex = 0; placementIndex < face.Placements.Length; placementIndex++)
                {
                    var placement = face.Placements[placementIndex];
                    if (placement.Kind != LayoutPageKind.Source) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (face.Number, placementIndex);
                    if (api.ImportPages(destinationDocument, sourceHandles[placement.ItemId].Document, placement.SourcePageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), originalPages + importedCount) == 0)
                        throw new InvalidDataException($"PDFium failed to import page {placement.SourcePageNumber} from item '{placement.ItemId}'.");
                    importedPageIndex.Add(key, originalPages + importedCount++);
                }

                var outputStart = originalPages + importedCount;
                for (var index = 0; index < compilation.Pages.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var size = compilation.Pages[index].OutputSize!;
                    var page = api.NewPage(destinationDocument, outputStart + index, size.WidthPoints, size.HeightPoints);
                    if (page == IntPtr.Zero) throw new InvalidDataException("PDFium could not create an output page.");
                    outputPages.Add(page);
                }

                foreach (var face in compilation.Pages)
                for (var placementIndex = 0; placementIndex < face.Placements.Length; placementIndex++)
                {
                    var placement = face.Placements[placementIndex];
                    if (placement.Kind != LayoutPageKind.Source) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePageIndex = importedPageIndex[(face.Number, placementIndex)];
                    var sourcePage = api.LoadPage(destinationDocument, sourcePageIndex);
                    if (sourcePage == IntPtr.Zero) throw new InvalidDataException("PDFium could not reopen an imported source page.");
                    try
                    {
                        if (Math.Abs(api.GetPageWidth(sourcePage) - placement.SourceSize.WidthPoints) > 0.1
                            || Math.Abs(api.GetPageHeight(sourcePage) - placement.SourceSize.HeightPoints) > 0.1)
                            throw new InvalidDataException($"Source item '{placement.ItemId}' page {placement.SourcePageNumber} dimensions differ from the compiled layout.");
                        var targetPage = outputPages[face.Number - 1];
                        var bounds = placement.TargetBounds ?? new PageRectangle(0, 0, placement.TargetSize.WidthPoints, placement.TargetSize.HeightPoints);
                        var (a, b, c, d, e, f) = Affine(placement, bounds, face.OutputSize!);
                        while (true)
                        {
                            var remaining = api.CountObjects(sourcePage);
                            if (remaining < 0) throw new InvalidDataException("PDFium returned an invalid page object count.");
                            if (remaining == 0) break;
                            var item = api.GetObject(sourcePage, 0);
                            if (item == IntPtr.Zero || api.RemoveObject(sourcePage, item) == 0) throw new InvalidDataException("PDFium could not detach an imported vector object.");
                            api.TransformObject(item, a, b, c, d, e, f);
                            if (api.InsertObject(targetPage, item) == 0) throw new InvalidDataException("PDFium could not place an imported vector object.");
                        }
                    }
                    finally { api.ClosePage(sourcePage); }
                }

                foreach (var page in outputPages)
                    if (api.GenerateContent(page) == 0) throw new InvalidDataException("PDFium could not generate output page content.");
                foreach (var page in outputPages) { api.ClosePage(page); }
                outputPages.Clear();
                for (var index = outputStart - 1; index >= 0; index--) api.DeletePage(destinationDocument, index);

                var saved = api.Save(destinationDocument);
                VerifySaved(api, saved, compilation);
                return saved;
            }
            finally
            {
                foreach (var page in outputPages) api.ClosePage(page);
                foreach (var (handle, pin) in sourceHandles.Values) { api.CloseDocument(handle); pin.Dispose(); }
                api.CloseDocument(destinationDocument);
            }
        }
        finally { api.DestroyLibrary(); }
    }

    private static (double A, double B, double C, double D, double E, double F) Affine(PagePlacement placement, PageRectangle bounds, PageSize outputSize)
    {
        var scale = placement.Scale;
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(bounds.XPoints) || !double.IsFinite(bounds.YPoints)
            || !double.IsFinite(bounds.WidthPoints) || !double.IsFinite(bounds.HeightPoints))
            throw new InvalidDataException("The compiled page placement contains invalid transform values.");
        var contentWidth = scale * (placement.RotatedClockwise ? placement.SourceSize.HeightPoints : placement.SourceSize.WidthPoints);
        var contentHeight = scale * (placement.RotatedClockwise ? placement.SourceSize.WidthPoints : placement.SourceSize.HeightPoints);
        var x = bounds.XPoints + (bounds.WidthPoints - contentWidth) / 2d;
        var y = outputSize.HeightPoints - bounds.YPoints - (bounds.HeightPoints + contentHeight) / 2d;
        return placement.RotatedClockwise
            ? (0, -scale, scale, 0, x, y + scale * placement.SourceSize.WidthPoints)
            : (scale, 0, 0, scale, x, y);
    }

    private static void VerifySaved(PdfiumRuntime api, byte[] saved, LayoutCompilation compilation)
    {
        using var pin = PinnedBytes.Create(saved);
        var document = api.LoadMemDocument(pin.Pointer, saved.Length, IntPtr.Zero);
        if (document == IntPtr.Zero) throw new InvalidDataException("PDFium could not reopen the composed PDF.");
        try
        {
            if (api.GetPageCount(document) != compilation.Pages.Length) throw new InvalidDataException("The saved PDF page count does not match the compiled page map.");
            for (var index = 0; index < compilation.Pages.Length; index++)
            {
                var page = api.LoadPage(document, index);
                if (page == IntPtr.Zero) throw new InvalidDataException("PDFium could not inspect a saved output page.");
                try
                {
                    var expected = compilation.Pages[index].OutputSize!;
                    if (Math.Abs(api.GetPageWidth(page) - expected.WidthPoints) > 0.1 || Math.Abs(api.GetPageHeight(page) - expected.HeightPoints) > 0.1)
                        throw new InvalidDataException("A saved PDF page has dimensions that differ from the compiled output face.");
                }
                finally { api.ClosePage(page); }
            }
        }
        finally { api.CloseDocument(document); }
    }

    private sealed class PinnedBytes : IDisposable
    {
        private GCHandle _handle;
        private PinnedBytes(byte[] bytes) => _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        public IntPtr Pointer => _handle.AddrOfPinnedObject();
        public static PinnedBytes Create(byte[] bytes) => new(bytes);
        public void Dispose() { if (_handle.IsAllocated) _handle.Free(); }
    }

    private sealed class PdfiumRuntime : IDisposable
    {
        private readonly FileStream _runtimeFile = null!;
        private readonly IntPtr _library;
        public PdfiumRuntime(string path, string expectedHash)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This PDFium adapter is currently qualified only for the pinned Windows x64 runtime.");
            if (!File.Exists(path)) throw new FileNotFoundException("The explicitly configured PDFium runtime was not found.", path);
            _runtimeFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var actual = Convert.ToHexString(SHA256.HashData(_runtimeFile)).ToLowerInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedHash)))
                    throw new InvalidDataException("The configured PDFium runtime digest does not match the pinned digest.");
                _runtimeFile.Position = 0;
                _library = NativeLibrary.Load(GetOpenedPath(_runtimeFile.SafeFileHandle));
                foreach (var export in RequiredExports)
                    if (!NativeLibrary.TryGetExport(_library, export, out _)) throw new EntryPointNotFoundException($"The configured PDFium runtime does not export required API {export}.");
                InitLibrary = Get<NoArgs>("FPDF_InitLibrary"); DestroyLibrary = Get<NoArgs>("FPDF_DestroyLibrary");
                LoadMemDocument = Get<LoadMem>("FPDF_LoadMemDocument"); CloseDocument = Get<CloseHandle>("FPDF_CloseDocument");
                GetPageCount = Get<CountPages>("FPDF_GetPageCount"); ImportPages = Get<Import>("FPDF_ImportPages");
                SaveAsCopy = Get<SaveCopy>("FPDF_SaveAsCopy"); NewPage = Get<NewPageFn>("FPDFPage_New");
                DeletePage = Get<DeletePageFn>("FPDFPage_Delete"); LoadPage = Get<LoadPageFn>("FPDF_LoadPage");
                ClosePage = Get<CloseHandle>("FPDF_ClosePage"); CountObjects = Get<CountObjectsFn>("FPDFPage_CountObjects");
                GetObject = Get<GetObjectFn>("FPDFPage_GetObject"); RemoveObject = Get<RemoveObjectFn>("FPDFPage_RemoveObject");
                InsertObject = Get<InsertObjectFn>("FPDFPage_InsertObject"); TransformObject = Get<TransformFn>("FPDFPageObj_Transform");
                GenerateContent = Get<GenerateFn>("FPDFPage_GenerateContent"); GetPageWidth = Get<PageDimension>("FPDF_GetPageWidthF");
                GetPageHeight = Get<PageDimension>("FPDF_GetPageHeightF");
            }
            catch
            {
                if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
                _runtimeFile.Dispose();
                throw;
            }
        }
        public NoArgs InitLibrary { get; private set; } = null!;
        public NoArgs DestroyLibrary { get; private set; } = null!;
        public LoadMem LoadMemDocument { get; private set; } = null!;
        public CloseHandle CloseDocument { get; private set; } = null!;
        public CountPages GetPageCount { get; private set; } = null!;
        public Import ImportPages { get; private set; } = null!;
        private SaveCopy SaveAsCopy { get; set; } = null!;
        public NewPageFn NewPage { get; private set; } = null!;
        public DeletePageFn DeletePage { get; private set; } = null!;
        public LoadPageFn LoadPage { get; private set; } = null!;
        public CloseHandle ClosePage { get; private set; } = null!;
        public CountObjectsFn CountObjects { get; private set; } = null!;
        public GetObjectFn GetObject { get; private set; } = null!;
        public RemoveObjectFn RemoveObject { get; private set; } = null!;
        public InsertObjectFn InsertObject { get; private set; } = null!;
        public TransformFn TransformObject { get; private set; } = null!;
        public GenerateFn GenerateContent { get; private set; } = null!;
        public PageDimension GetPageWidth { get; private set; } = null!;
        public PageDimension GetPageHeight { get; private set; } = null!;
        public byte[] Save(IntPtr document)
        {
            using var output = new MemoryStream();
            var state = new WriteState(output, MaximumOutputBytes);
            var writerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeFileWrite>());
            try
            {
                var writer = new NativeFileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(Write) };
                Marshal.StructureToPtr(writer, writerPtr, false);
                WriteTargets[writerPtr] = state;
                if (SaveAsCopy(document, writerPtr, 0) == 0) throw new InvalidDataException("PDFium failed to serialize the composed document.");
                if (state.Error is not null) throw new IOException("The output stream callback failed.", state.Error);
                return output.ToArray();
            }
            finally
            {
                WriteTargets.TryRemove(writerPtr, out _);
                Marshal.FreeHGlobal(writerPtr);
                GC.KeepAlive(Write);
            }
        }
        private static readonly ConcurrentDictionary<IntPtr, WriteState> WriteTargets = new();
        private static readonly WriteBlock Write = WriteOutput;
        private static int WriteOutput(IntPtr writer, IntPtr data, uint size)
        {
            try
            {
                if (size > int.MaxValue || !WriteTargets.TryGetValue(writer, out var state)) return 0;
                if (state.Stream.Length + size > state.MaximumBytes) throw new InvalidDataException("The generated PDF exceeds the 512 MiB output limit.");
                var buffer = new byte[(int)size];
                Marshal.Copy(data, buffer, 0, buffer.Length);
                state.Stream.Write(buffer);
                return 1;
            }
            catch (Exception error)
            {
                if (WriteTargets.TryGetValue(writer, out var state)) state.Error = error;
                return 0;
            }
        }
        private sealed class WriteState(Stream stream, long maximumBytes) { public Stream Stream { get; } = stream; public long MaximumBytes { get; } = maximumBytes; public Exception? Error { get; set; } }
        [StructLayout(LayoutKind.Sequential)] private struct NativeFileWrite { public int Version; public IntPtr WriteBlock; }
        private T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
        private static string GetOpenedPath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw new IOException("Could not resolve the opened PDFium runtime path.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            return buffer.ToString();
        }
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
        public void Dispose()
        {
            NativeLibrary.Free(_library);
            _runtimeFile.Dispose();
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void NoArgs();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr LoadMem(IntPtr bytes, int length, IntPtr password);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void CloseHandle(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int CountPages(IntPtr document);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] public delegate int Import(IntPtr destination, IntPtr source, string pageRanges, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SaveCopy(IntPtr document, IntPtr writer, int flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr NewPageFn(IntPtr document, int index, double width, double height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DeletePageFn(IntPtr document, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr LoadPageFn(IntPtr document, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int CountObjectsFn(IntPtr page);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr GetObjectFn(IntPtr page, int index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int RemoveObjectFn(IntPtr page, IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int InsertObjectFn(IntPtr page, IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void TransformFn(IntPtr item, double a, double b, double c, double d, double e, double f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GenerateFn(IntPtr page);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate float PageDimension(IntPtr page);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteBlock(IntPtr writer, IntPtr data, uint size);

    }
}
