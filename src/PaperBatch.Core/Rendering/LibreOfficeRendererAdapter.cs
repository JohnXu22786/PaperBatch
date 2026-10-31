using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ComponentModel;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Properties;

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
    // The live monitor terminates at the artifact ceiling; only Linux also receives a kernel-
    // enforced RLIMIT_FSIZE. Windows has no per-process file-size quota in this launch path.
    private const long OutputLimitKillThresholdBytes = MaximumOutputBytes;
    private const int MaximumDiagnosticCharacters = 4096;
    private const string LinuxPrlimitExecutablePath = "/usr/bin/prlimit";
    private const string ConversionLockDirectoryName = "LibreOfficeConversionLocks";
    private const string ConversionLockFileName = "conversion.lock";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OutputMonitorInterval = TimeSpan.FromMilliseconds(1);
    private static readonly SemaphoreSlim ConversionSlots = new(1, 1);
    private static readonly HashSet<SourceFormat> SupportedFormats =
    [
        SourceFormat.Docx, SourceFormat.Rtf, SourceFormat.Xlsx, SourceFormat.Pptx,
        SourceFormat.Odt, SourceFormat.Ods, SourceFormat.Odp
    ];

    internal enum ConversionProcessStatus
    {
        Exited,
        TimedOut,
        Cancelled,
        Failed,
        OutputLimitExceeded
    }

    private readonly SourceSnapshotCache _snapshots;
    private readonly RenderArtifactStore _artifacts;
    private readonly IPdfArtifactInspector? _pdfInspector;
    private readonly string _enginePath;
    private readonly string _engineSha256;
    private readonly string _programDirectory;
    private readonly string _installationDirectory;
    private readonly string _launcherPath;

    public LibreOfficeRendererAdapter(
        SourceSnapshotCache snapshots,
        RenderArtifactStore artifacts,
        IPdfArtifactInspector? pdfInspector,
        string directEnginePath,
        string trustedEngineSha256)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _pdfInspector = pdfInspector;
        ArgumentException.ThrowIfNullOrWhiteSpace(directEnginePath);
        if (trustedEngineSha256 is null || trustedEngineSha256.Length != 64 || !trustedEngineSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A trusted direct-engine SHA-256 pin is required.", nameof(trustedEngineSha256));
        _enginePath = Path.GetFullPath(directEnginePath);
        const string expectedEngineFileName = "soffice.bin";
        var fileNameComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFileName(_enginePath), expectedEngineFileName, fileNameComparison))
            throw new ArgumentException("The pinned LibreOffice engine path must identify the package's soffice.bin executable.", nameof(directEnginePath));
        _engineSha256 = trustedEngineSha256.ToLowerInvariant();
        _programDirectory = Path.GetDirectoryName(_enginePath) ?? throw new ArgumentException("The direct engine must have a parent directory.", nameof(directEnginePath));
        _installationDirectory = Directory.GetParent(_programDirectory)?.FullName ??
            throw new ArgumentException("The direct engine must be inside a LibreOffice installation directory.", nameof(directEnginePath));
        _launcherPath = Path.Combine(_programDirectory, OperatingSystem.IsWindows() ? "soffice.com" : "soffice");
    }

    public ValueTask<bool> CanRenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExecutionContext != RendererExecutionContext.BackgroundWorker)
            return RejectCapability("document conversion requires the background-worker execution context.");
        if (!SupportedFormats.Contains(request.Format))
            return RejectCapability("the source format is not supported by the LibreOffice adapter.");
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
            return RejectCapability("LibreOffice conversion is supported only on Windows and Linux.");
        if (!File.Exists(_enginePath) || !File.Exists(_launcherPath))
            return RejectCapability("the configured LibreOffice engine or launcher file is missing.");
        if (OperatingSystem.IsWindows() && !File.Exists(Path.Combine(_programDirectory, "fundamental.ini")))
            return RejectCapability("the configured LibreOffice runtime is missing program/fundamental.ini.");

        try
        {
            VerifyRuntimeInstallationReadOnly();
            var info = new FileInfo(_enginePath);
            if ((File.GetAttributes(_enginePath) & FileAttributes.ReparsePoint) != 0)
                return RejectCapability("the configured engine is a reparse point.");
            if (OperatingSystem.IsLinux() &&
                (File.GetUnixFileMode(_enginePath) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                return RejectCapability("the configured Linux engine is not executable.");
            if (info.Length <= 0 || info.Length > 64 * 1024 * 1024)
                return RejectCapability("the configured engine length is outside its allowed bounds.");
            using var input = new FileStream(_enginePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested();
            return string.Equals(digest, _engineSha256, StringComparison.Ordinal)
                ? ValueTask.FromResult(true)
                : RejectCapability("the configured engine did not match its pinned SHA-256.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or Win32Exception or NotSupportedException or System.Security.SecurityException)
        {
            return RejectCapability($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static ValueTask<bool> RejectCapability(string reason)
    {
        if (Environment.GetEnvironmentVariable("PAPERBATCH_LO_DIAGNOSTICS") == "1")
            Console.Error.WriteLine($"LibreOffice runtime capability check rejected: {reason}");
        return ValueTask.FromResult(false);
    }

    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        if (!SupportedFormats.Contains(request.Format)) return RenderResult.Unsupported("LibreOffice does not support this source format.");
        if (!await CanRenderAsync(request, cancellationToken).ConfigureAwait(false))
            return RenderResult.Unsupported("The pinned LibreOffice engine is unavailable for this execution context.");

        var operation = string.Empty;
        var ownerMarker = string.Empty;
        var markerCreated = false;
        IDisposable? inProcessSlot = null;
        FileStream? crossProcessSlot = null;
        try
        {
            inProcessSlot = await AcquireInProcessConversionSlotAsync(ProcessTimeout, cancellationToken).ConfigureAwait(false);
            if (inProcessSlot is null)
                return new RenderResult(RenderStatus.TimedOut, null, "LibreOffice conversion did not acquire its in-process slot before timeout.");
            crossProcessSlot = await AcquireCrossProcessConversionSlotAsync(cancellationToken, ProcessTimeout).ConfigureAwait(false);
            operation = Path.Combine(Path.GetTempPath(), "paperbatch-lo-" + Guid.NewGuid().ToString("N"));
            CreatePrivateOperationDirectory(operation);
            ownerMarker = Path.Combine(operation, ".paperbatch-lo-operation-v1");
            WriteOwnerMarker(ownerMarker);
            markerCreated = true;
            var profileUri = LibreOfficeProfilePolicy.CreatePrivateProfile(operation);
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
            var version = await ReadVersionAsync(profileUri, cancellationToken).ConfigureAwait(false);
            if (version is null) return RenderResult.Failed("LibreOffice did not provide a usable version receipt.");

            var startInfo = CreateBaseStartInfo(profileUri);
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add("pdf");
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(operation);
            startInfo.ArgumentList.Add(sourcePath);
            startInfo = CreateFileSizeLimitedStartInfo(startInfo, MaximumOutputBytes);
            var result = await RunOwnedProcessAsync(startInfo, ProcessTimeout, cancellationToken,
                pdfPath, OutputLimitKillThresholdBytes).ConfigureAwait(false);
            if (result.Status == ConversionProcessStatus.TimedOut)
                return new RenderResult(RenderStatus.TimedOut, null, "LibreOffice exceeded its rendering time limit and was stopped.");
            if (result.Status == ConversionProcessStatus.Cancelled)
                return new RenderResult(RenderStatus.Cancelled, null, "LibreOffice rendering was cancelled and stopped.");
            if (result.Status == ConversionProcessStatus.OutputLimitExceeded)
                return RenderResult.Failed("LibreOffice output crossed the monitored stop threshold and was terminated.");
            if (result.Status != ConversionProcessStatus.Exited || result.ExitCode != 0)
            {
                if (Environment.GetEnvironmentVariable("PAPERBATCH_LO_DIAGNOSTICS") == "1")
                {
                    Console.Error.WriteLine($"LibreOffice export failed: status={result.Status}, exit={result.ExitCode}, output={result.Output}");
                }
                return RenderResult.Failed("LibreOffice could not convert this source format.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            var outputInfo = new FileInfo(pdfPath);
            if (!outputInfo.Exists || outputInfo.Length <= 0 || outputInfo.Length > MaximumOutputBytes)
                return RenderResult.Failed("LibreOffice did not produce a nonempty PDF within the accepted size limit.");
            using var output = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var preflight = SourceFormatPreflight.Inspect(output, ".pdf");
            if (preflight.Status != SourcePreflightStatus.Ready)
                return RenderResult.Failed("LibreOffice output failed PDF structural safety checks.");
            output.Position = 0;
            var pageCount = _pdfInspector is null ? 0 :
                await _pdfInspector.GetPageCountAsync(output, cancellationToken).ConfigureAwait(false);
            if (_pdfInspector is not null && pageCount <= 0)
                return RenderResult.Failed("The PDF inspector did not verify a positive page count.");
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
        catch (TimeoutException)
        {
            return new RenderResult(RenderStatus.TimedOut, null, "LibreOffice conversion did not acquire its per-user slot before timeout.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException or CryptographicException)
        {
            if (Environment.GetEnvironmentVariable("PAPERBATCH_LO_DIAGNOSTICS") == "1")
            {
                var diagnostic = $"{exception.GetType().Name}: {exception.Message}";
                if (diagnostic.Length > MaximumDiagnosticCharacters)
                    diagnostic = diagnostic[..MaximumDiagnosticCharacters];
                Console.Error.WriteLine($"LibreOffice rendering failed before completion: {diagnostic}");
            }
            return RenderResult.Failed("LibreOffice could not safely render this source.");
        }
        finally
        {
            if (markerCreated && IsOwnedOperation(operation, ownerMarker))
                TryDeleteOperation(operation);
            crossProcessSlot?.Dispose();
            inProcessSlot?.Dispose();
        }
    }

    internal static async Task<IDisposable?> AcquireInProcessConversionSlotAsync(
        TimeSpan acquisitionTimeout, CancellationToken cancellationToken)
    {
        if (acquisitionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(acquisitionTimeout));
        if (!await ConversionSlots.WaitAsync(acquisitionTimeout, cancellationToken).ConfigureAwait(false))
            return null;
        return new SemaphoreReleaseLease(ConversionSlots);
    }

    private sealed class SemaphoreReleaseLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }

    internal static async Task<FileStream> AcquireCrossProcessConversionSlotAsync(CancellationToken cancellationToken)
        => await AcquireCrossProcessConversionSlotAsync(cancellationToken, ProcessTimeout).ConfigureAwait(false);

    internal static async Task<FileStream> AcquireCrossProcessConversionSlotAsync(
        CancellationToken cancellationToken, TimeSpan acquisitionTimeout)
    {
        if (acquisitionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(acquisitionTimeout));
        var appData = AppDataPaths.LocalApplicationData;
        if (string.IsNullOrWhiteSpace(appData))
            throw new IOException("The current user's local application data directory is unavailable.");
        var lockDirectory = Path.Combine(appData, "PaperBatch", ConversionLockDirectoryName);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(lockDirectory);
            SourceSnapshotCache.ApplyPrivateWindowsPermissions(lockDirectory);
        }
        else
        {
            Directory.CreateDirectory(lockDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(lockDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if ((File.GetAttributes(lockDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The LibreOffice conversion lock directory cannot be a reparse point.");

        var lockPath = Path.Combine(lockDirectory, ConversionLockFileName);
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The LibreOffice conversion lock file cannot be a reparse point.");
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var wait = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, options);
            }
            catch (IOException exception)
            {
                if (wait.Elapsed >= acquisitionTimeout)
                    throw new TimeoutException("Timed out waiting for the per-user LibreOffice conversion lock.", exception);
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> ReadVersionAsync(Uri profileUri, CancellationToken cancellationToken)
    {
        var startInfo = CreateBaseStartInfo(profileUri);
        startInfo.ArgumentList.Add("--version");
        var result = await RunOwnedProcessAsync(startInfo, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (result.Status != ConversionProcessStatus.Exited || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            if (Environment.GetEnvironmentVariable("PAPERBATCH_LO_DIAGNOSTICS") == "1")
                Console.Error.WriteLine($"LibreOffice version query failed: status={result.Status}, exit={result.ExitCode}, output={result.Output}");
            return null;
        }
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(line => line.StartsWith("LibreOffice ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("LibreOfficeDev ", StringComparison.OrdinalIgnoreCase));
    }

    internal ProcessStartInfo CreateBaseStartInfo(Uri profileUri)
    {
        // The packaged launcher performs required process setup through oosplash. The direct
        // soffice.bin image is the pinned engine, but it is not the command-line entry point.
        var info = new ProcessStartInfo(_launcherPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _programDirectory
        };
        if (Environment.GetEnvironmentVariable("PAPERBATCH_LO_DIAGNOSTICS") == "1")
            info.Environment["SAL_LOG"] = "+WARN";
        if (OperatingSystem.IsLinux())
        {
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("LD_", StringComparison.Ordinal)).ToArray())
                info.Environment.Remove(key);
            info.Environment.Remove("URE_BOOTSTRAP");
            info.Environment["LD_LIBRARY_PATH"] = _programDirectory;
            var fundamental = Path.Combine(_programDirectory, "fundamentalrc");
            if (File.Exists(fundamental)) info.Environment["URE_BOOTSTRAP"] = new Uri(fundamental).AbsoluteUri;
        }
        else if (OperatingSystem.IsWindows())
        {
            info.Environment.Remove("URE_BOOTSTRAP");
            var fundamental = Path.Combine(_programDirectory, "fundamental.ini");
            info.Environment["URE_BOOTSTRAP"] = new Uri(fundamental).AbsoluteUri;
            var existingPath = info.Environment.TryGetValue("PATH", out var path) ? path : string.Empty;
            info.Environment["PATH"] = string.IsNullOrEmpty(existingPath)
                ? _programDirectory
                : _programDirectory + Path.PathSeparator + existingPath;
        }
        info.ArgumentList.Add("--headless");
        info.ArgumentList.Add("--nologo");
        info.ArgumentList.Add("--nodefault");
        info.ArgumentList.Add("--nofirststartwizard");
        info.ArgumentList.Add("-env:UserInstallation=" + profileUri.AbsoluteUri);
        return info;
    }

    internal static ProcessStartInfo CreateFileSizeLimitedStartInfo(
        ProcessStartInfo engineStartInfo, long maximumFileBytes)
    {
        if (!OperatingSystem.IsLinux()) return engineStartInfo;
        if (maximumFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        var limited = new ProcessStartInfo(LinuxPrlimitExecutablePath)
        {
            WorkingDirectory = engineStartInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = engineStartInfo.CreateNoWindow,
            RedirectStandardOutput = engineStartInfo.RedirectStandardOutput,
            RedirectStandardError = engineStartInfo.RedirectStandardError
        };
        // The wrapper starts with the parent environment. Rebuild it from the already-sanitized
        // engine environment so removed loader variables do not reappear before exec.
        limited.Environment.Clear();
        foreach (var (name, value) in engineStartInfo.Environment) limited.Environment[name] = value;
        limited.ArgumentList.Add($"--fsize={maximumFileBytes}:{maximumFileBytes}");
        limited.ArgumentList.Add(engineStartInfo.FileName);
        foreach (var argument in engineStartInfo.ArgumentList) limited.ArgumentList.Add(argument);
        return limited;
    }

    private void VerifyRuntimeInstallationReadOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsRuntimeInstallationReadOnly();
            return;
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (GetEffectiveUserId() == 0)
            throw new UnauthorizedAccessException("The bounded LibreOffice runtime cannot be launched with an effective root identity.");
        EnsureTrustedPath(LinuxPrlimitExecutablePath, "/usr");
        if ((File.GetUnixFileMode(LinuxPrlimitExecutablePath) &
             (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new UnauthorizedAccessException("The trusted Linux file-size limit launcher is not executable.");
        var trustedRoot = Path.GetFullPath(_installationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        EnsureTrustedPath(trustedRoot, trustedRoot);
        EnsureTrustedPath(_enginePath, trustedRoot);
        EnsureTrustedPath(_launcherPath, trustedRoot);
        EnsureTrustedPath(Path.Combine(_programDirectory, "oosplash"), trustedRoot);
        var checkedFiles = 0;
        foreach (var library in Directory.EnumerateFiles(_installationDirectory, "*.so*", SearchOption.AllDirectories))
        {
            if (++checkedFiles > 10_000) throw new IOException("The LibreOffice runtime contains too many shared-library entries.");
            EnsureTrustedPath(library, trustedRoot);
        }
    }

    [SupportedOSPlatform("windows")]
    private void VerifyWindowsRuntimeInstallationReadOnly()
    {
        var root = Path.GetFullPath(_installationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();
        if (!programFiles.Any(path => root.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("The LibreOffice runtime must be installed below a protected Program Files directory.");
        var protectedProgramFiles = programFiles.First(path => root.StartsWith(
            path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        var reachedProgramFiles = false;
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
        {
            if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("A LibreOffice runtime ancestor cannot be a reparse point.");
            VerifyWindowsRuntimeEntryAcl(current.FullName);
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    protectedProgramFiles.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                reachedProgramFiles = true;
                break;
            }
        }
        if (!reachedProgramFiles)
            throw new UnauthorizedAccessException("The LibreOffice installation is not beneath the verified Program Files directory.");

        var pending = new Stack<string>();
        pending.Push(root);
        var checkedEntries = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("A LibreOffice runtime directory cannot be a reparse point.");
            VerifyWindowsRuntimeEntryAcl(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++checkedEntries > 20_000)
                    throw new IOException("The LibreOffice runtime contains too many entries.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("A LibreOffice runtime entry cannot be a reparse point.");
                VerifyWindowsRuntimeEntryAcl(entry);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        foreach (var path in new[] { _enginePath, _launcherPath })
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("The configured LibreOffice engine or launcher escapes its protected installation.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsRuntimeEntryAcl(string path)
    {
        var security = Directory.Exists(path)
            ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ??
            throw new UnauthorizedAccessException("A LibreOffice runtime entry has no resolvable owner SID.");
        if (!IsTrustedWindowsWriter(owner.Value))
            throw new UnauthorizedAccessException("A LibreOffice runtime entry is owned by an untrusted account.");

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
            typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                !GrantsWindowsWriteAccess(rule.FileSystemRights))
                continue;
            if (rule.IdentityReference is SecurityIdentifier sid)
            {
                if (IsTrustedWindowsWriter(sid.Value)) continue;
                if (string.Equals(sid.Value, "S-1-3-0", StringComparison.Ordinal) &&
                    IsTrustedWindowsWriter(owner.Value)) continue; // Creator Owner resolves to the already-verified trusted owner.
            }
            throw new UnauthorizedAccessException($"LibreOffice runtime entry '{path}' grants write access to untrusted SID {rule.IdentityReference.Value} ({rule.FileSystemRights}).");
        }
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsTrustedWindowsWriter(string sid) =>
        string.Equals(sid, "S-1-5-18", StringComparison.Ordinal) || // Local System
        string.Equals(sid, "S-1-5-32-544", StringComparison.Ordinal) || // Builtin Administrators
        string.Equals(sid, "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", StringComparison.Ordinal); // TrustedInstaller

    [SupportedOSPlatform("windows")]
    internal static bool GrantsWindowsWriteAccess(FileSystemRights rights) =>
        (rights & UntrustedWindowsWriteRights) != 0;

    [SupportedOSPlatform("windows")]
    private const FileSystemRights UntrustedWindowsWriteRights = FileSystemRights.WriteData |
        FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

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
                    throw new UnauthorizedAccessException($"The LibreOffice executable or shared-library path '{current.FullName}' is writable by the current user or another untrusted account (mode 0x{Convert.ToInt32(metadata.Mode):X}, owner UID {metadata.UserId}).");
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

    internal static async Task<(ConversionProcessStatus Status, int? ExitCode, string Output)> RunOwnedProcessAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken,
        string? boundedOutputPath = null, long? maximumOutputBytes = null)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) return (ConversionProcessStatus.Failed, null, string.Empty);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (ConversionProcessStatus.Failed, null, string.Empty);
        }

        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumDiagnosticCharacters);
        var stderr = ReadBoundedAsync(process.StandardError, MaximumDiagnosticCharacters);
        try
        {
            var waitForExit = process.WaitForExitAsync(cancellationToken);
            if (boundedOutputPath is not null && maximumOutputBytes is not null)
            {
                using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var outputLimit = MonitorOutputSizeAsync(process, boundedOutputPath, maximumOutputBytes.Value,
                    monitorCancellation.Token);
                var timeoutTask = Task.Delay(timeout, cancellationToken);
                var completed = await Task.WhenAny(waitForExit, outputLimit, timeoutTask).ConfigureAwait(false);
                if (completed == outputLimit && await outputLimit.ConfigureAwait(false))
                {
                    await StopOwnedProcessAsync(process).ConfigureAwait(false);
                    monitorCancellation.Cancel();
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    return (ConversionProcessStatus.OutputLimitExceeded, null, string.Empty);
                }
                if (completed == timeoutTask)
                {
                    await timeoutTask.ConfigureAwait(false);
                    throw new TimeoutException();
                }
                await waitForExit.ConfigureAwait(false);
                monitorCancellation.Cancel();
                if (IsOutputSizeOverLimit(boundedOutputPath, maximumOutputBytes.Value))
                {
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    return (ConversionProcessStatus.OutputLimitExceeded, null, string.Empty);
                }
            }
            else
            {
                await waitForExit.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (ConversionProcessStatus.Exited, process.ExitCode, stdout.Result + "\n" + stderr.Result);
        }
        catch (TimeoutException)
        {
            await StopOwnedProcessAsync(process).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (ConversionProcessStatus.TimedOut, null, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopOwnedProcessAsync(process).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (ConversionProcessStatus.Cancelled, null, string.Empty);
        }
        catch
        {
            await StopOwnedProcessAsync(process).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> MonitorOutputSizeAsync(
        Process process, string outputPath, long maximumBytes, CancellationToken cancellationToken)
    {
        while (!process.HasExited)
        {
            if (IsOutputSizeOverLimit(outputPath, maximumBytes)) return true;
            await Task.Delay(OutputMonitorInterval, cancellationToken).ConfigureAwait(false);
        }
        return IsOutputSizeOverLimit(outputPath, maximumBytes);
    }

    private static bool IsOutputSizeOverLimit(string path, long maximumBytes)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > maximumBytes; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
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
