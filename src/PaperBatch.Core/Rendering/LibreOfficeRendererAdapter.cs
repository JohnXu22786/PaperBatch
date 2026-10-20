using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.ComponentModel;
using PaperBatch.Core.Intake;

namespace PaperBatch.Core.Rendering;

/// <summary>Reopens a completed PDF, validates its document structure, and returns its page count.</summary>
public interface IPdfArtifactInspector
{
    Task<int> GetPageCountAsync(Stream pdf, CancellationToken cancellationToken);
}

/// <summary>
/// Converts immutable snapshots with one directly configured LibreOffice engine in an isolated
/// temporary profile. This adapter does not claim native Microsoft Office or WPS support.
/// </summary>
public sealed class LibreOfficeRendererAdapter : IRendererAdapter
{
    private const long MaximumSourceBytes = 512L * 1024 * 1024;
    private const long MaximumOutputBytes = 512L * 1024 * 1024;
    private const int MaximumDiagnosticCharacters = 4096;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);
    private static readonly HashSet<SourceFormat> SupportedFormats =
    [
        SourceFormat.Doc, SourceFormat.Docx, SourceFormat.Rtf,
        SourceFormat.Xls, SourceFormat.Xlsx,
        SourceFormat.Ppt, SourceFormat.Pptx,
        SourceFormat.Odt, SourceFormat.Ods, SourceFormat.Odp,
        SourceFormat.Jpeg, SourceFormat.Png, SourceFormat.Tiff
    ];

    private readonly SourceSnapshotCache _snapshots;
    private readonly RenderArtifactStore _artifacts;
    private readonly IPdfArtifactInspector _pdfInspector;
    private readonly string _enginePath;
    private readonly string _engineSha256;
    private readonly string _programDirectory;
    private readonly string _installationDirectory;

    public LibreOfficeRendererAdapter(
        SourceSnapshotCache snapshots,
        RenderArtifactStore artifacts,
        IPdfArtifactInspector pdfInspector,
        string directEnginePath,
        string trustedEngineSha256)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _pdfInspector = pdfInspector ?? throw new ArgumentNullException(nameof(pdfInspector));
        ArgumentException.ThrowIfNullOrWhiteSpace(directEnginePath);
        if (trustedEngineSha256 is null || trustedEngineSha256.Length != 64 || !trustedEngineSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A trusted direct-engine SHA-256 pin is required.", nameof(trustedEngineSha256));
        _enginePath = Path.GetFullPath(directEnginePath);
        _engineSha256 = trustedEngineSha256.ToLowerInvariant();
        _programDirectory = Path.GetDirectoryName(_enginePath) ?? throw new ArgumentException("The direct engine must have a parent directory.", nameof(directEnginePath));
        _installationDirectory = Directory.GetParent(_programDirectory)?.FullName ??
            throw new ArgumentException("The direct engine must be inside a LibreOffice installation directory.", nameof(directEnginePath));
    }

    public ValueTask<bool> CanRenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExecutionContext != RendererExecutionContext.BackgroundWorker || !SupportedFormats.Contains(request.Format) ||
            !OperatingSystem.IsLinux() || !File.Exists(_enginePath))
            return ValueTask.FromResult(false);

        try
        {
            VerifyRuntimeInstallationReadOnly();
            var info = new FileInfo(_enginePath);
            if ((File.GetAttributes(_enginePath) & FileAttributes.ReparsePoint) != 0) return ValueTask.FromResult(false);
            if ((File.GetUnixFileMode(_enginePath) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                return ValueTask.FromResult(false);
            if (info.Length <= 0 || info.Length > 64 * 1024 * 1024) return ValueTask.FromResult(false);
            using var input = new FileStream(_enginePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(string.Equals(digest, _engineSha256, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or Win32Exception or NotSupportedException or System.Security.SecurityException)
        {
            return ValueTask.FromResult(false);
        }
    }

    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        if (!SupportedFormats.Contains(request.Format)) return RenderResult.Unsupported("LibreOffice does not support this source format.");
        if (!await CanRenderAsync(request, cancellationToken).ConfigureAwait(false))
            return RenderResult.Unsupported("The pinned LibreOffice engine is unavailable for this execution context.");

        var operation = Path.Combine(Path.GetTempPath(), "paperbatch-lo-" + Guid.NewGuid().ToString("N"));
        CreatePrivateOperationDirectory(operation);
        var ownerMarker = Path.Combine(operation, ".paperbatch-lo-operation-v1");
        var markerCreated = false;
        try
        {
            WriteOwnerMarker(ownerMarker);
            markerCreated = true;
            var profileUri = LibreOfficeProfilePolicy.CreatePrivateProfile(operation);
            var enginePath = await StagePinnedEngineAsync(operation, cancellationToken).ConfigureAwait(false);
            var extension = ExtensionFor(request.Format);
            var sourcePath = Path.Combine(operation, "source" + extension);
            var pdfPath = Path.Combine(operation, "source.pdf");
            await CopySnapshotAsync(request.SnapshotId, sourcePath, cancellationToken).ConfigureAwait(false);
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var sourcePreflight = SourceFormatPreflight.Inspect(source, extension);
                if (sourcePreflight.Status != SourcePreflightStatus.Ready)
                    return RenderResult.Failed("The source snapshot failed structural or encryption safety checks.");
            }
            var version = await ReadVersionAsync(profileUri, enginePath, cancellationToken).ConfigureAwait(false);
            if (version is null) return RenderResult.Failed("LibreOffice did not provide a usable version receipt.");

            var startInfo = CreateBaseStartInfo(profileUri, enginePath);
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add("pdf");
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(operation);
            startInfo.ArgumentList.Add(sourcePath);
            var result = await RunOwnedProcessAsync(startInfo, ProcessTimeout, cancellationToken).ConfigureAwait(false);
            if (result.Status == OwnedProcessStatus.TimedOut)
                return new RenderResult(RenderStatus.TimedOut, null, "LibreOffice exceeded its rendering time limit and was stopped.");
            if (result.Status == OwnedProcessStatus.Cancelled)
                return new RenderResult(RenderStatus.Cancelled, null, "LibreOffice rendering was cancelled and stopped.");
            if (result.Status != OwnedProcessStatus.Exited || result.ExitCode != 0)
                return RenderResult.Failed("LibreOffice could not convert this source format.");
            cancellationToken.ThrowIfCancellationRequested();

            var outputInfo = new FileInfo(pdfPath);
            if (!outputInfo.Exists || outputInfo.Length <= 0 || outputInfo.Length > MaximumOutputBytes)
                return RenderResult.Failed("LibreOffice did not produce a bounded PDF artifact.");
            using var output = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var preflight = SourceFormatPreflight.Inspect(output, ".pdf");
            if (preflight.Status != SourcePreflightStatus.Ready)
                return RenderResult.Failed("LibreOffice output failed PDF structural safety checks.");
            output.Position = 0;
            var pageCount = await _pdfInspector.GetPageCountAsync(output, cancellationToken).ConfigureAwait(false);
            if (pageCount <= 0) return RenderResult.Failed("The PDF inspector did not verify a positive page count.");
            output.Position = 0;
            var artifact = await _artifacts.PersistPdfAsync(output, "libreoffice", version,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source-format"] = request.Format.ToString(),
                    ["engine-sha256"] = _engineSha256,
                    ["macro-policy"] = "disabled",
                    ["link-refresh-policy"] = "disabled",
                    ["profile"] = "private-temporary",
                    ["pdf-filter"] = "pdf"
                }, cancellationToken).ConfigureAwait(false);
            return new RenderResult(RenderStatus.Succeeded,
                new RenderArtifact(artifact.Artifact.ArtifactId, artifact.Artifact.Sha256, artifact.Artifact.LengthBytes, pageCount),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RenderResult(RenderStatus.Cancelled, null, "LibreOffice rendering was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or CryptographicException)
        {
            return RenderResult.Failed("LibreOffice could not safely render this source.");
        }
        finally
        {
            if (markerCreated && IsOwnedOperation(operation, ownerMarker))
                TryDeleteOperation(operation);
        }
    }

    private async Task<string?> ReadVersionAsync(Uri profileUri, string enginePath, CancellationToken cancellationToken)
    {
        var startInfo = CreateBaseStartInfo(profileUri, enginePath);
        startInfo.ArgumentList.Add("--version");
        var result = await RunOwnedProcessAsync(startInfo, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (result.Status != OwnedProcessStatus.Exited || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return null;
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(line => line.StartsWith("LibreOffice ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("LibreOfficeDev ", StringComparison.OrdinalIgnoreCase));
    }

    private ProcessStartInfo CreateBaseStartInfo(Uri profileUri, string enginePath)
    {
        var info = new ProcessStartInfo(enginePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _programDirectory
        };
        if (OperatingSystem.IsLinux())
        {
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("LD_", StringComparison.Ordinal)).ToArray())
                info.Environment.Remove(key);
            info.Environment.Remove("URE_BOOTSTRAP");
            info.Environment["LD_LIBRARY_PATH"] = _programDirectory;
            var fundamental = Path.Combine(_programDirectory, "fundamentalrc");
            if (File.Exists(fundamental)) info.Environment["URE_BOOTSTRAP"] = new Uri(fundamental).AbsoluteUri;
        }
        info.ArgumentList.Add("--headless");
        info.ArgumentList.Add("--nologo");
        info.ArgumentList.Add("--nodefault");
        info.ArgumentList.Add("--nofirststartwizard");
        info.ArgumentList.Add("-env:UserInstallation=" + profileUri.AbsoluteUri);
        return info;
    }

    private void VerifyRuntimeInstallationReadOnly()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var trustedRoot = Path.GetFullPath(_installationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        EnsureTrustedPath(trustedRoot, trustedRoot);
        EnsureTrustedPath(_enginePath, trustedRoot);
        var checkedFiles = 0;
        foreach (var library in Directory.EnumerateFiles(_installationDirectory, "*.so*", SearchOption.AllDirectories))
        {
            if (++checkedFiles > 10_000) throw new IOException("The LibreOffice runtime contains too many shared-library entries.");
            EnsureTrustedPath(library, trustedRoot);
        }
    }

    private static void EnsureTrustedPath(string path, string trustedRoot)
    {
        var root = Path.GetFullPath(trustedRoot).TrimEnd(Path.DirectorySeparatorChar);
        FileSystemInfo candidate = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var resolved = Path.GetFullPath(candidate.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path);
        if (!string.Equals(resolved, root, StringComparison.Ordinal) &&
            !resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("A LibreOffice runtime path resolves outside its trusted installation root.");

        var current = new FileInfo(resolved);
        while (current is not null)
        {
            if (current.Exists || Directory.Exists(current.FullName))
            {
                if (Statx(AtFdcwd, current.FullName, AtSymlinkNoFollow, StatxMode | StatxUid, out var metadata) != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The LibreOffice runtime permissions could not be verified.");
                var uid = GetEffectiveUserId();
                const ushort userWrite = 0x0080;
                const ushort groupWrite = 0x0010;
                const ushort otherWrite = 0x0002;
                const ushort sticky = 0x0200;
                var writableByOthers = (metadata.Mode & (groupWrite | otherWrite)) != 0;
                var protectedStickyParent = Directory.Exists(current.FullName) &&
                    metadata.UserId != uid && (metadata.Mode & sticky) != 0;
                if (((metadata.Mode & userWrite) != 0 && metadata.UserId == uid) ||
                    (writableByOthers && !protectedStickyParent))
                    throw new UnauthorizedAccessException("The LibreOffice executable or shared-library path is writable by the current user or another untrusted account.");
            }
            // The installation root can itself be protected while an ancestor
            // remains replaceable. Walk to the filesystem root as well. A
            // root-owned sticky directory (for example /tmp) is safe as a
            // parent because callers cannot rename entries owned by others.
            current = current.Directory is null ? null : new FileInfo(current.Directory.FullName);
        }
    }

    private const int AtFdcwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxMode = 0x0002;
    private const uint StatxUid = 0x0008;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Statx(int directoryFileDescriptor, string path, int flags, uint mask, out LinuxStatx buffer);

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = false)]
    private static extern uint GetEffectiveUserId();

    private async Task<string> StagePinnedEngineAsync(string operation, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(_enginePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The configured LibreOffice engine path cannot be a reparse point.");
        var staged = Path.Combine(operation, "soffice.bin");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        await using (var input = new FileStream(_enginePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(staged, options))
        {
            await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        using var engine = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(engine)).ToLowerInvariant(), _engineSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The privately staged LibreOffice engine does not match its configured digest.");
        return staged;
    }

    private async Task CopySnapshotAsync(string snapshotId, string destinationPath, CancellationToken cancellationToken)
    {
        using var input = _snapshots.OpenRead(snapshotId);
        if (input.Length <= 0 || input.Length > MaximumSourceBytes) throw new InvalidDataException("The source snapshot exceeds LibreOffice limits.");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var output = new FileStream(destinationPath, options);
        await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(OwnedProcessStatus Status, int? ExitCode, string Output)> RunOwnedProcessAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return (OwnedProcessStatus.Failed, null, string.Empty);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (OwnedProcessStatus.Failed, null, string.Empty);
        }

        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumDiagnosticCharacters);
        var stderr = ReadBoundedAsync(process.StandardError, MaximumDiagnosticCharacters);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (OwnedProcessStatus.Exited, process.ExitCode, stdout.Result + "\n" + stderr.Result);
        }
        catch (TimeoutException)
        {
            await StopOwnedProcessAsync(process).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (OwnedProcessStatus.TimedOut, null, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopOwnedProcessAsync(process).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (OwnedProcessStatus.Cancelled, null, string.Empty);
        }
        catch
        {
            await StopOwnedProcessAsync(process).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var buffer = new char[1024];
        var output = new System.Text.StringBuilder(Math.Min(limit, 1024));
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            var remaining = limit - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(count, remaining));
        }
        return output.ToString();
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            if (!process.HasExited) throw new IOException("The owned LibreOffice process could not be stopped safely.", exception);
        }
    }

    private static string ExtensionFor(SourceFormat format) => format switch
    {
        SourceFormat.Doc => ".doc", SourceFormat.Docx => ".docx", SourceFormat.Rtf => ".rtf",
        SourceFormat.Xls => ".xls", SourceFormat.Xlsx => ".xlsx",
        SourceFormat.Ppt => ".ppt", SourceFormat.Pptx => ".pptx",
        SourceFormat.Odt => ".odt", SourceFormat.Ods => ".ods", SourceFormat.Odp => ".odp",
        SourceFormat.Jpeg => ".jpg", SourceFormat.Png => ".png", SourceFormat.Tiff => ".tiff",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static void CreatePrivateOperationDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (OperatingSystem.IsWindows()) SourceSnapshotCache.ApplyPrivateWindowsPermissions(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("The renderer operation path cannot be a reparse point.");
    }

    private static void WriteOwnerMarker(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var output = new FileStream(path, options);
        using var writer = new StreamWriter(output);
        writer.Write("PaperBatch.LibreOfficeOperation.v1\n");
    }

    private static bool IsOwnedOperation(string directory, string marker)
    {
        try
        {
            return Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 &&
                File.Exists(marker) && File.ReadAllText(marker) == "PaperBatch.LibreOfficeOperation.v1\n";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void TryDeleteOperation(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
