using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PaperBatch.Core.Rendering;

/// <summary>Reopens generated PDFs with one explicitly pinned PDFium runtime to verify page count.</summary>
public sealed class PdfiumArtifactInspector
    : IPdfArtifactInspector
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
}
