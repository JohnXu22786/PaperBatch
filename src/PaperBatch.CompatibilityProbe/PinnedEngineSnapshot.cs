using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PaperBatch.CompatibilityProbe;

internal sealed class PinnedEngineSnapshot : IDisposable
{
    private const uint MfdAllowSealing = 0x0002;
    private const int FAddSeals = 1033;
    private const int SealSeal = 0x0001;
    private const int SealShrink = 0x0002;
    private const int SealGrow = 0x0004;
    private const int SealWrite = 0x0008;
    internal const long DefaultMaximumSnapshotBytes = 512L * 1024 * 1024;
    private const long MaximumRuntimeComponentBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumRuntimeComponentEntries = 100_000;

    private readonly FileStream? _snapshot;
    private readonly SafeFileHandle? _runtimeDirectory;
    private readonly string _engineFileName;
    private readonly string _engineFileIdentity;

    private PinnedEngineSnapshot(
        string launchPath,
        string sha256,
        string runtimeDirectoryPath,
        string engineFileName,
        string engineFileIdentity,
        string runtimeComponentsSha256,
        FileStream? snapshot,
        SafeFileHandle? runtimeDirectory)
    {
        LaunchPath = launchPath;
        Sha256 = sha256;
        RuntimeDirectoryPath = runtimeDirectoryPath;
        RuntimeComponentsSha256 = runtimeComponentsSha256;
        _engineFileName = engineFileName;
        _engineFileIdentity = engineFileIdentity;
        _snapshot = snapshot;
        _runtimeDirectory = runtimeDirectory;
    }

    internal string LaunchPath { get; }

    internal string Sha256 { get; }

    internal string RuntimeDirectoryPath { get; }

    internal string RuntimeComponentsSha256 { get; }

    internal void VerifyRuntimeComponentsUnchanged()
    {
        var observed = ComputeRuntimeComponentsSha256(RuntimeDirectoryPath, _engineFileName, _engineFileIdentity);
        if (!string.Equals(observed, RuntimeComponentsSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "LibreOffice runtime components changed while the process was running; version evidence is blocked.");
        }
    }

    internal static PinnedEngineSnapshot Create(
        FileStream sourceFile,
        string sourcePath,
        string expectedSha256,
        long maximumSnapshotBytes = DefaultMaximumSnapshotBytes)
    {
        if (maximumSnapshotBytes < 0 || maximumSnapshotBytes > DefaultMaximumSnapshotBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSnapshotBytes),
                $"Snapshot limits must be between 0 and {DefaultMaximumSnapshotBytes} bytes.");
        }

        if (OperatingSystem.IsLinux())
        {
            return CreateLinuxSnapshot(sourceFile, sourcePath, expectedSha256, maximumSnapshotBytes);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Pinned LibreOffice launch is supported only on Linux and Windows.");
        }

        var sourceSha256 = HashSourceWithinLimit(sourceFile, maximumSnapshotBytes);
        if (!string.Equals(sourceSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The direct engine SHA-256 does not match the caller-supplied expected digest.");
        }

        // The caller keeps this source open with FileShare.Read, which denies write and delete sharing on Windows.
        // Resolve from the opened handle so swapping a symlink or junction cannot redirect the later launch.
        var launchPath = LockedFilePath.Resolve(sourcePath, sourceFile.SafeFileHandle);
        var runtimeDirectoryPath = Path.GetDirectoryName(launchPath) ?? Environment.CurrentDirectory;
        var engineFileIdentity = LockedFilePath.GetFileIdentity(sourceFile.SafeFileHandle);
        var runtimeComponentsSha256 = ComputeRuntimeComponentsSha256(
            runtimeDirectoryPath,
            Path.GetFileName(launchPath),
            engineFileIdentity);
        return new PinnedEngineSnapshot(
            launchPath,
            sourceSha256,
            runtimeDirectoryPath,
            Path.GetFileName(launchPath),
            engineFileIdentity,
            runtimeComponentsSha256,
            snapshot: null,
            runtimeDirectory: null);
    }

    internal static string HashSourceWithinLimit(FileStream sourceFile, long maximumBytes = DefaultMaximumSnapshotBytes)
    {
        if (maximumBytes < 0 || maximumBytes > DefaultMaximumSnapshotBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                $"Snapshot limits must be between 0 and {DefaultMaximumSnapshotBytes} bytes.");
        }

        sourceFile.Position = 0;
        try
        {
            if (sourceFile.Length > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The selected executable exceeds the {maximumBytes / (1024 * 1024)} MiB inspection limit.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long bytesHashed = 0;
            while (true)
            {
                var remainingWithSentinel = maximumBytes - bytesHashed + 1;
                var readLimit = (int)Math.Min(buffer.Length, remainingWithSentinel);
                var bytesRead = sourceFile.Read(buffer, 0, readLimit);
                if (bytesRead == 0)
                {
                    break;
                }

                bytesHashed += bytesRead;
                if (bytesHashed > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"The selected executable exceeds the {maximumBytes / (1024 * 1024)} MiB inspection limit.");
                }

                hash.AppendData(buffer, 0, bytesRead);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            sourceFile.Position = 0;
        }
    }

    internal static void ConfigureLinuxLaunchEnvironment(
        IDictionary<string, string?> environment,
        string programDirectory)
    {
        foreach (var variable in environment.Keys
                     .Where(name => name.StartsWith("LD_", StringComparison.Ordinal))
                     .ToArray())
        {
            environment.Remove(variable);
        }

        environment.Remove("URE_BOOTSTRAP");
        environment["LD_LIBRARY_PATH"] = programDirectory;
        var fundamental = Path.Combine(programDirectory, "fundamentalrc");
        if (File.Exists(fundamental))
        {
            environment["URE_BOOTSTRAP"] = new Uri(fundamental).AbsoluteUri;
        }
    }

    public void Dispose()
    {
        try
        {
            _snapshot?.Dispose();
        }
        finally
        {
            _runtimeDirectory?.Dispose();
        }
    }

    private static PinnedEngineSnapshot CreateLinuxSnapshot(
        FileStream sourceFile,
        string sourcePath,
        string expectedSha256,
        long maximumSnapshotBytes)
    {
        if (sourceFile.Length > maximumSnapshotBytes)
        {
            throw new InvalidDataException(
                $"The selected engine exceeds the {maximumSnapshotBytes / (1024 * 1024)} MiB snapshot limit.");
        }

        var enginePath = LockedFilePath.ResolveTargetPath(sourcePath, sourceFile.SafeFileHandle);
        var runtimeDirectory = LockedFilePath.OpenLinuxDirectoryForOpenedFile(enginePath, sourceFile.SafeFileHandle);
        var descriptor = MemfdCreate("paperbatch-u0-engine", MfdAllowSealing);
        if (descriptor < 0)
        {
            runtimeDirectory.Dispose();
            throw NativeIOException("Could not create the Linux engine snapshot.");
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        FileStream? snapshot = null;
        try
        {
            snapshot = new FileStream(handle, FileAccess.ReadWrite);
            sourceFile.Position = 0;
            CopyWithinLimit(sourceFile, snapshot, maximumSnapshotBytes);
            snapshot.Flush();
            if (Fchmod(descriptor, 0x0100 | 0x0040) != 0)
            {
                throw NativeIOException("Could not mark the Linux engine snapshot executable.");
            }

            if (Fcntl(descriptor, FAddSeals, SealSeal | SealShrink | SealGrow | SealWrite) != 0)
            {
                throw NativeIOException("Could not seal the Linux engine snapshot against writes and resizing.");
            }

            snapshot.Position = 0;
            var snapshotSha256 = Convert.ToHexString(SHA256.HashData(snapshot)).ToLowerInvariant();
            snapshot.Position = 0;
            if (!string.Equals(snapshotSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The copied Linux engine snapshot does not match the caller-supplied expected digest.");
            }

            var launchPath = $"/proc/{Environment.ProcessId}/fd/{descriptor}";
            if (!File.Exists(launchPath))
            {
                throw new IOException("Linux procfs does not expose the sealed engine snapshot descriptor.");
            }

            var runtimeDirectoryPath = LockedFilePath.LinuxDescriptorPath(runtimeDirectory);
            var engineFileIdentity = LockedFilePath.GetFileIdentity(sourceFile.SafeFileHandle);
            var runtimeComponentsSha256 = ComputeRuntimeComponentsSha256(
                runtimeDirectoryPath,
                Path.GetFileName(enginePath),
                engineFileIdentity);
            return new PinnedEngineSnapshot(
                launchPath,
                snapshotSha256,
                runtimeDirectoryPath,
                Path.GetFileName(enginePath),
                engineFileIdentity,
                runtimeComponentsSha256,
                snapshot,
                runtimeDirectory);
        }
        catch
        {
            if (snapshot is null)
            {
                handle.Dispose();
            }
            else
            {
                snapshot.Dispose();
            }
            runtimeDirectory.Dispose();
            throw;
        }
    }

    private static IOException NativeIOException(string message) => new(
        $"{message} (errno {Marshal.GetLastPInvokeError()}).");

    private static string ComputeRuntimeComponentsSha256(
        string directoryPath,
        string engineFileName,
        string engineFileIdentity)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<(string Path, string RelativePath)>();
        pending.Push((directoryPath, string.Empty));
        long bytesHashed = 0;
        var entriesSeen = 0;

        while (pending.Count > 0)
        {
            var (currentPath, relativeDirectory) = pending.Pop();
            var children = EnumerateRuntimeEntries(
                Directory.EnumerateFileSystemEntries(currentPath),
                ref entriesSeen);
            foreach (var childPath in children)
            {
                var name = Path.GetFileName(childPath);
                var relativePath = relativeDirectory.Length == 0
                    ? name
                    : relativeDirectory + "/" + name;
                var attributes = File.GetAttributes(childPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The LibreOffice runtime directory contains a symbolic link or reparse point that cannot be safely fingerprinted.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    AppendRuntimeComponentHeader(hash, "D", childPath, relativePath, attributes, length: null);
                    pending.Push((childPath, relativePath));
                    continue;
                }

                using var component = LockedFilePath.OpenReadOnlyRegularFile(childPath, FileShare.Read);
                if (relativeDirectory.Length == 0
                    && comparer.Equals(name, engineFileName)
                    && string.Equals(
                        LockedFilePath.GetFileIdentity(component.SafeFileHandle),
                        engineFileIdentity,
                        StringComparison.Ordinal))
                {
                    var currentAttributes = File.GetAttributes(childPath);
                    if ((currentAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    {
                        throw new IOException("The pinned engine directory entry changed while its identity was being inspected.");
                    }

                    using var currentEntry = LockedFilePath.OpenReadOnlyRegularFile(childPath, FileShare.Read);
                    if (!string.Equals(
                            LockedFilePath.GetFileIdentity(currentEntry.SafeFileHandle),
                            engineFileIdentity,
                            StringComparison.Ordinal))
                    {
                        throw new IOException("The pinned engine directory entry changed while its identity was being inspected.");
                    }

                    continue;
                }

                var componentLength = component.Length;
                if (componentLength < 0 || bytesHashed + componentLength > MaximumRuntimeComponentBytes)
                {
                    throw new IOException("The LibreOffice runtime directory exceeds the 4 GiB content inspection limit.");
                }

                AppendRuntimeComponentHeader(hash, "F", childPath, relativePath, attributes, componentLength);
                var buffer = new byte[1024 * 1024];
                long componentBytes = 0;
                while (true)
                {
                    var remainingWithSentinel = MaximumRuntimeComponentBytes - bytesHashed - componentBytes + 1;
                    var readLimit = (int)Math.Min(buffer.Length, remainingWithSentinel);
                    var count = component.Read(buffer, 0, readLimit);
                    if (count == 0)
                    {
                        break;
                    }

                    componentBytes += count;
                    if (bytesHashed + componentBytes > MaximumRuntimeComponentBytes
                        || componentBytes > componentLength)
                    {
                        throw new IOException("A LibreOffice runtime component changed or exceeded the 4 GiB content inspection limit.");
                    }

                    hash.AppendData(buffer, 0, count);
                }

                if (componentBytes != componentLength || component.Length != componentLength)
                {
                    throw new IOException("A LibreOffice runtime component changed while its identity was being inspected.");
                }

                bytesHashed += componentBytes;
                hash.AppendData([0]);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string[] EnumerateRuntimeEntries(
        IEnumerable<string> entries,
        ref int entriesSeen,
        int maximumEntries = MaximumRuntimeComponentEntries)
    {
        var children = new List<string>();
        foreach (var entry in entries)
        {
            entriesSeen++;
            if (entriesSeen > maximumEntries)
            {
                throw new IOException("The LibreOffice runtime directory exceeds the 100,000-entry inspection limit.");
            }

            children.Add(entry);
        }

        children.Sort((left, right) => StringComparer.Ordinal.Compare(Path.GetFileName(left), Path.GetFileName(right)));
        return children.ToArray();
    }

    private static void AppendRuntimeComponentHeader(
        IncrementalHash hash,
        string kind,
        string fullPath,
        string relativePath,
        FileAttributes attributes,
        long? length)
    {
        var mode = OperatingSystem.IsWindows()
            ? ((int)attributes).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : ((int)File.GetUnixFileMode(fullPath)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var header = System.Text.Encoding.UTF8.GetBytes(
            $"{kind}\0{relativePath}\0{mode}\0{length?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty}\0");
        hash.AppendData(header);
    }

    private static void CopyWithinLimit(Stream source, Stream destination, long maximumBytes)
    {
        var buffer = new byte[1024 * 1024];
        long copiedBytes = 0;
        while (true)
        {
            var remaining = maximumBytes - copiedBytes;
            var readLimit = (int)Math.Min(buffer.Length, remaining + 1);
            var count = source.Read(buffer, 0, readLimit);
            if (count == 0)
            {
                return;
            }

            if (count > remaining)
            {
                throw new InvalidDataException(
                    $"The selected engine exceeds the {maximumBytes / (1024 * 1024)} MiB snapshot limit.");
            }

            destination.Write(buffer, 0, count);
            copiedBytes += count;
        }
    }

    [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int MemfdCreate(string name, uint flags);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int Fchmod(int fileDescriptor, uint mode);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fileDescriptor, int command, int argument);
}
