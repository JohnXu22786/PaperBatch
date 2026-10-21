using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using PaperBatch.Core.Domain;

namespace PaperBatch.Core.Intake;

public sealed class SourceIntakeException(IntakeDiagnostic diagnostic, Exception? innerException = null)
    : IOException(diagnostic.Message, innerException)
{
    public IntakeDiagnostic Diagnostic { get; } = diagnostic;
}

public sealed record SourceFileIdentity(string FileId, long LengthBytes, long LastWriteTimeUtcTicks, FileAttributes? NativeAttributes = null);

/// <summary>Small filesystem seam for deterministic mutation and cancellation tests.</summary>
public interface ISourceFileAccess
{
    SourceFileIdentity GetPathIdentity(string path);
    Stream OpenRead(string path);
    SourceFileIdentity GetOpenedFileIdentity(Stream stream);
    Stream? OpenZoneIdentifierRead(string path) => null;
}

public sealed record SourceSnapshotCacheOptions
{
    public string RootPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperBatch", "SourceCache");
    public long MaximumBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public long MaximumSourceBytes { get; init; } = 512L * 1024 * 1024;
    public int MaximumSnapshots { get; init; } = 10_000;
    public int BufferSize { get; init; } = 64 * 1024;
}

/// <summary>
/// Stores unique opaque snapshots in a private cache. Snapshot IDs are references, not authorization tokens;
/// callers open bytes through this cache instead of receiving a predictable storage path.
/// </summary>
public sealed class SourceSnapshotCache
{
    private const string OwnerMarkerName = ".paperbatch-source-cache-v1";
    private const string MutationLockName = ".cache-mutation.lock";
    private const string MetadataName = "metadata.json";
    private const string ContentName = "content.bin";
    private const int MaximumZoneIdentifierBytes = 64 * 1024;
    private const string OwnerMarker = "PaperBatch.SourceCache.v1\n";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly SourceSnapshotCacheOptions _options;
    private readonly string _rootPath;
    private readonly ISourceFileAccess _fileAccess;
    private readonly SemaphoreSlim _rootGate;

    public SourceSnapshotCache(SourceSnapshotCacheOptions? options = null, ISourceFileAccess? fileAccess = null)
    {
        _options = options ?? new SourceSnapshotCacheOptions();
        if (string.IsNullOrWhiteSpace(_options.RootPath) || _options.MaximumBytes < 0 ||
            _options.MaximumSourceBytes < 0 || _options.MaximumSnapshots <= 0 || _options.BufferSize < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Cache paths and limits must be valid and bounded.");
        }

        _rootPath = Path.GetFullPath(_options.RootPath);
        _options = _options with { RootPath = _rootPath };
        _fileAccess = fileAccess ?? new PhysicalSourceFileAccess();
        _rootGate = RootGates.GetOrAdd(_rootPath, static _ => new SemaphoreSlim(1, 1));
        InitializeRoot();
    }

    public Task<SourceSnapshot> CaptureAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        CaptureAsync(sourcePath, _fileAccess, cancellationToken);

    public async Task<SourceSnapshot> CaptureAsync(
        string sourcePath,
        ISourceFileAccess sourceAccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceAccess);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw DiagnosticException(IntakeDiagnosticCode.InvalidPath, "The selected source path is empty or invalid.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (PathTooLongException exception)
        {
            throw DiagnosticException(IntakeDiagnosticCode.PathTooLong, "The selected source path exceeds the platform path limit.", exception);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw DiagnosticException(IntakeDiagnosticCode.InvalidPath, "The selected source path is not valid.", exception);
        }

        var lockTaken = false;
        string? temporaryDirectory = null;
        try
        {
            await _rootGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockTaken = true;
            cancellationToken.ThrowIfCancellationRequested();

            var identityBefore = sourceAccess.GetPathIdentity(fullPath);
            RejectDeviceIdentity(identityBefore);
            if (identityBefore.LengthBytes < 0)
            {
                throw DiagnosticException(IntakeDiagnosticCode.SourceChangedDuringCapture, "The source identity is invalid.");
            }

            if (identityBefore.LengthBytes > _options.MaximumSourceBytes)
            {
                throw DiagnosticException(IntakeDiagnosticCode.CacheQuotaExceeded, "The source exceeds the configured snapshot size limit.");
            }

            using var source = sourceAccess.OpenRead(fullPath);
            var openedBefore = sourceAccess.GetOpenedFileIdentity(source);
            RejectDeviceIdentity(openedBefore);
            if (openedBefore != identityBefore)
            {
                throw DiagnosticException(IntakeDiagnosticCode.SourceChangedDuringCapture,
                    "The source identity, length, or last-write time changed before snapshot capture began.");
            }

            var zoneIdentifier = ReadZoneIdentifier(sourceAccess, fullPath, cancellationToken);
            using var mutationLock = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
            EnsureCapacity(checked(identityBefore.LengthBytes + (zoneIdentifier?.LongLength ?? 0)));
            var snapshotId = "snap_" + Guid.NewGuid().ToString("N");
            temporaryDirectory = Path.Combine(_rootPath, ".tmp_" + Guid.NewGuid().ToString("N"));
            CreatePrivateDirectory(temporaryDirectory);
            var temporaryContentPath = Path.Combine(temporaryDirectory, ContentName);
            byte[] digest;
            long copiedLength = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var destination = CreatePrivateFile(temporaryContentPath, FileMode.CreateNew, _options.BufferSize))
            {
                var buffer = new byte[_options.BufferSize];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    copiedLength = checked(copiedLength + read);
                    if (copiedLength > identityBefore.LengthBytes || copiedLength > _options.MaximumSourceBytes)
                    {
                        throw DiagnosticException(IntakeDiagnosticCode.SourceChangedDuringCapture,
                            "The source length changed while its snapshot was being copied.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                digest = hash.GetHashAndReset();
            }

            var openedAfter = sourceAccess.GetOpenedFileIdentity(source);
            RejectDeviceIdentity(openedAfter);
            var pathAfter = sourceAccess.GetPathIdentity(fullPath);
            RejectDeviceIdentity(pathAfter);
            if (openedAfter != identityBefore || pathAfter != identityBefore || copiedLength != identityBefore.LengthBytes)
            {
                throw DiagnosticException(IntakeDiagnosticCode.SourceChangedDuringCapture,
                    "The source identity, length, or last-write time changed during snapshot capture.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var capturedAt = DateTimeOffset.UtcNow;
            var snapshot = new SourceSnapshot(snapshotId, Convert.ToHexString(digest).ToLowerInvariant(), copiedLength, capturedAt);
            var metadata = new CacheMetadata(1, snapshot.SnapshotId, snapshot.Sha256, snapshot.LengthBytes, snapshot.CapturedAtUtc,
                Retained: true, ZoneIdentifier: zoneIdentifier);
            WriteMetadata(Path.Combine(temporaryDirectory, MetadataName), metadata);

            // Account for the exact bytes that will be persisted, including JSON/base64 metadata overhead.
            EnsureCapacity(GetDirectoryBytes(temporaryDirectory), temporaryDirectory);

            var finalDirectory = GetSnapshotDirectory(snapshotId);
            Directory.Move(temporaryDirectory, finalDirectory);
            temporaryDirectory = null;
            return snapshot;
        }
        catch (SourceIntakeException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw DiagnosticException(IntakeDiagnosticCode.CancellationRequested, "Snapshot capture was cancelled.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or OverflowException or Win32Exception)
        {
            throw MapCaptureFailure(exception);
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }

            if (lockTaken) _rootGate.Release();
        }
    }

    public Stream OpenRead(string snapshotId)
    {
        var metadata = ReadMetadata(snapshotId);
        var path = Path.Combine(GetSnapshotDirectory(snapshotId), ContentName);
        FileStream? content = null;
        try
        {
            content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, _options.BufferSize, FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[_options.BufferSize];
            long length = 0;
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) != 0)
            {
                length = checked(length + read);
                hash.AppendData(buffer, 0, read);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (length != metadata.LengthBytes || !string.Equals(actualHash, metadata.Sha256, StringComparison.Ordinal))
            {
                throw DiagnosticException(IntakeDiagnosticCode.CacheEntryCorrupt, "The cached snapshot failed its length or digest check.");
            }

            content.Position = 0;
            return content;
        }
        catch (SourceIntakeException)
        {
            content?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or OverflowException)
        {
            content?.Dispose();
            throw DiagnosticException(IntakeDiagnosticCode.CacheEntryCorrupt, "The cached snapshot could not be verified.", exception);
        }
    }

    /// <summary>Returns the preserved Windows origin trust stream, if present, for downstream Protected View decisions.</summary>
    public Stream? OpenZoneIdentifierRead(string snapshotId)
    {
        var metadata = ReadMetadata(snapshotId);
        return metadata.ZoneIdentifier is null ? null : new MemoryStream(metadata.ZoneIdentifier, writable: false);
    }

    private static byte[]? ReadZoneIdentifier(ISourceFileAccess sourceAccess, string sourcePath, CancellationToken cancellationToken)
    {
        using var source = sourceAccess.OpenZoneIdentifierRead(sourcePath);
        if (source is null) return null;

        using var copy = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) return copy.ToArray();
            if (copy.Length + read > MaximumZoneIdentifierBytes)
            {
                throw DiagnosticException(IntakeDiagnosticCode.UnsafePackage,
                    "The source origin trust stream exceeds the bounded metadata limit.");
            }

            copy.Write(buffer, 0, read);
        }
    }

    /// <summary>Marks a snapshot as referenced by a prepared or unresolved run, protecting it from quota cleanup.</summary>
    public void Retain(string snapshotId) => UpdateRetention(snapshotId, retained: true);

    /// <summary>Releases a snapshot after every run that references it is safely resolved.</summary>
    public void Release(string snapshotId) => UpdateRetention(snapshotId, retained: false);

    private void UpdateRetention(string snapshotId, bool retained)
    {
        _rootGate.Wait();
        try
        {
            using var mutationLock = AcquireMutationLock();
            var metadata = ReadMetadata(snapshotId);
            if (metadata.Retained == retained) return;
            var updated = metadata with { Retained = retained };
            var directory = GetSnapshotDirectory(snapshotId);
            var temporaryMetadataPath = Path.Combine(directory, ".metadata_" + Guid.NewGuid().ToString("N") + ".tmp");
            var temporaryMetadataCreated = false;
            try
            {
                WriteMetadata(temporaryMetadataPath, updated);
                temporaryMetadataCreated = true;
                File.Move(temporaryMetadataPath, Path.Combine(directory, MetadataName), overwrite: true);
            }
            finally
            {
                if (temporaryMetadataCreated) TryDeleteGeneratedMetadataTemporary(temporaryMetadataPath);
            }
        }
        finally
        {
            _rootGate.Release();
        }
    }

    private async Task<FileStream> AcquireMutationLockAsync(CancellationToken cancellationToken)
    {
        var stream = OpenMutationLockFile();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryLockMutationFile(stream)) return stream;
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private FileStream AcquireMutationLock()
    {
        var stream = OpenMutationLockFile();
        try
        {
            while (true)
            {
                if (TryLockMutationFile(stream)) return stream;
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private FileStream OpenMutationLockFile()
    {
        var path = Path.Combine(_rootPath, MutationLockName);
        if (!OperatingSystem.IsWindows())
        {
            var flags = OperatingSystem.IsLinux() ? 0x00080042 : 0x01000202;
            var descriptor = OpenUnixLockFile(path, flags, 0x180);
            if (descriptor < 0) throw new IOException("The cache mutation lock file could not be opened.", new Win32Exception(Marshal.GetLastPInvokeError()));
            return new FileStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true), FileAccess.ReadWrite, 1, isAsync: false);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite,
            BufferSize = 1,
            Options = FileOptions.None
        };
        return new FileStream(path, options);
    }

    private static bool TryLockMutationFile(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                stream.Lock(0, 1);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) == 0) return true;
            var error = Marshal.GetLastPInvokeError();
            if (error is 11 or 35) return false;
            throw new IOException("The cache mutation lock could not be acquired.", new Win32Exception(error));
        }

        throw new PlatformNotSupportedException("Cross-process cache locking is not available on this platform.");
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnixLockFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    private void EnsureCapacity(long requiredBytes, string? incomingDirectory = null)
    {
        if (requiredBytes > _options.MaximumBytes)
        {
            throw DiagnosticException(IntakeDiagnosticCode.CacheQuotaExceeded, "The source exceeds the configured cache byte quota.");
        }

        ReclaimOrphanedTemporaryDirectories(incomingDirectory);
        var entries = ReadEntries();
        var temporaryDirectories = Directory.EnumerateDirectories(_rootPath, ".tmp_*", SearchOption.TopDirectoryOnly)
            .Where(directory => incomingDirectory is null || !string.Equals(directory, incomingDirectory, PathComparison))
            .ToArray();
        var usedBytes = entries.Sum(entry => GetDirectoryBytes(GetSnapshotDirectory(entry.SnapshotId)));
        foreach (var directory in temporaryDirectories)
        {
            usedBytes = checked(usedBytes + GetDirectoryBytes(directory));
        }

        var entryCount = entries.Count + temporaryDirectories.Length;
        foreach (var candidate in entries.Where(entry => !entry.Retained).OrderBy(entry => entry.CapturedAtUtc).ToArray())
        {
            if (Fits(usedBytes, requiredBytes) && entryCount < _options.MaximumSnapshots) break;
            var candidateBytes = GetDirectoryBytes(GetSnapshotDirectory(candidate.SnapshotId));
            if (DeleteEntry(candidate))
            {
                usedBytes -= candidateBytes;
                entryCount--;
                entries.Remove(candidate);
            }
        }

        if (!Fits(usedBytes, requiredBytes) || entryCount >= _options.MaximumSnapshots)
        {
            throw DiagnosticException(IntakeDiagnosticCode.CacheQuotaExceeded,
                "The source cache is at its configured quota; retained snapshots remain protected.");
        }

        bool Fits(long current, long incoming) => current <= _options.MaximumBytes && incoming <= _options.MaximumBytes - current;
    }

    private void ReclaimOrphanedTemporaryDirectories(string? excludedDirectory = null)
    {
        // Capture holds the cross-process mutation lock through publication, so matching temporary
        // directories cannot belong to a live capture while this capacity check is running.
        foreach (var directory in Directory.EnumerateDirectories(_rootPath, ".tmp_*", SearchOption.TopDirectoryOnly))
        {
            if (excludedDirectory is not null && string.Equals(directory, excludedDirectory, PathComparison)) continue;
            if (!IsOwnedTemporaryDirectory(directory)) continue;

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // An undeletable orphan remains counted against quota below.
            }
            catch (UnauthorizedAccessException)
            {
                // Do not let stale cleanup prevent unrelated intake; the directory remains counted.
            }
        }
    }

    private static bool IsOwnedTemporaryDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name.Length != 37 || !name.StartsWith(".tmp_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[5..], "N", out _))
        {
            return false;
        }

        try
        {
            if ((File.GetAttributes(directory) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            {
                return false;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    Path.GetFileName(entry) is not (ContentName or MetadataName))
                {
                    return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long GetDirectoryBytes(string directory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            total = checked(total + new FileInfo(file).Length);
        }

        return total;
    }

    private List<CacheMetadata> ReadEntries()
    {
        var entries = new List<CacheMetadata>();
        foreach (var directory in Directory.EnumerateDirectories(_rootPath, "snap_*", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileName(directory);
            entries.Add(ReadMetadata(id));
        }

        return entries;
    }

    private CacheMetadata ReadMetadata(string snapshotId)
    {
        if (!IsSnapshotId(snapshotId))
        {
            throw DiagnosticException(IntakeDiagnosticCode.CacheEntryCorrupt, "The snapshot identifier is invalid.");
        }

        var directory = GetSnapshotDirectory(snapshotId);
        var metadataPath = Path.Combine(directory, MetadataName);
        try
        {
            if (!Directory.Exists(directory) || new FileInfo(metadataPath).Length > 128 * 1024)
            {
                throw DiagnosticException(IntakeDiagnosticCode.CacheEntryCorrupt, "The cached snapshot metadata is missing or oversized.");
            }

            var metadata = JsonSerializer.Deserialize<CacheMetadata>(File.ReadAllBytes(metadataPath));
            if (metadata is null || metadata.SchemaVersion != 1 || metadata.SnapshotId != snapshotId || metadata.LengthBytes < 0 ||
                string.IsNullOrEmpty(metadata.Sha256) || metadata.Sha256.Length != 64 || metadata.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
                metadata.CapturedAtUtc.Offset != TimeSpan.Zero || metadata.ZoneIdentifier?.Length > MaximumZoneIdentifierBytes ||
                !File.Exists(Path.Combine(directory, ContentName)) ||
                new FileInfo(Path.Combine(directory, ContentName)).Length != metadata.LengthBytes)
            {
                throw DiagnosticException(IntakeDiagnosticCode.CacheEntryCorrupt, "The cached snapshot metadata is inconsistent.");
            }

            return metadata;
        }
        catch (SourceIntakeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw DiagnosticException(IntakeDiagnosticCode.CacheEntryCorrupt, "The cached snapshot metadata could not be read.", exception);
        }
    }

    private bool DeleteEntry(CacheMetadata entry)
    {
        var directory = GetSnapshotDirectory(entry.SnapshotId);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!IsMetadataTemporaryName(Path.GetFileName(path))) continue;

            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                File.Delete(path);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        var names = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!names.SetEquals([MetadataName, ContentName]))
        {
            return false;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void InitializeRoot()
    {
        try
        {
            if (Directory.Exists(_rootPath))
            {
                if ((File.GetAttributes(_rootPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw DiagnosticException(IntakeDiagnosticCode.CacheRootNotPrivate, "The cache root cannot be a file system link.");
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(_rootPath);
            }
            else
            {
                Directory.CreateDirectory(_rootPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (OperatingSystem.IsWindows())
            {
                ApplyPrivateWindowsPermissions(_rootPath);
            }
            else
            {
                var mode = File.GetUnixFileMode(_rootPath);
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                {
                    throw DiagnosticException(IntakeDiagnosticCode.CacheRootNotPrivate,
                        "The existing cache root grants group or other users access; choose a private directory.");
                }
            }

            var markerPath = Path.Combine(_rootPath, OwnerMarkerName);
            if (File.Exists(markerPath))
            {
                if (!WaitForValidOwnerMarker(markerPath))
                {
                    throw DiagnosticException(IntakeDiagnosticCode.CacheRootNotPrivate, "The cache ownership marker is invalid.");
                }
            }
            else
            {
                if (Directory.EnumerateFileSystemEntries(_rootPath).Any())
                {
                    if (!WaitForValidOwnerMarker(markerPath))
                    {
                        throw DiagnosticException(IntakeDiagnosticCode.CacheRootNotPrivate,
                            "The cache root contains files not created by this cache; use an empty app-owned directory.");
                    }
                }
                else
                {
                    try
                    {
                        using var marker = CreatePrivateFile(markerPath, FileMode.CreateNew, 256);
                        var bytes = Encoding.UTF8.GetBytes(OwnerMarker);
                        marker.Write(bytes);
                        marker.Flush(flushToDisk: true);
                    }
                    catch (IOException) when (WaitForValidOwnerMarker(markerPath))
                    {
                        // Another process initialized the same private root concurrently.
                    }
                }
            }
        }
        catch (SourceIntakeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or CryptographicException)
        {
            throw DiagnosticException(IntakeDiagnosticCode.CacheRootNotPrivate, "The cache root could not be made private.", exception);
        }
    }

    private static bool WaitForValidOwnerMarker(string markerPath)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                if (string.Equals(File.ReadAllText(markerPath, Encoding.UTF8), OwnerMarker, StringComparison.Ordinal)) return true;
            }
            catch (IOException)
            {
                // A concurrent initializer may still hold the newly created marker open.
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    internal static void ApplyPrivateWindowsPermissions(string directory)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new Win32Exception("The current Windows user SID could not be resolved.");
        }

        var sddl = $"D:P(A;OICI;FA;;;{sid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!SetFileSecurityW(directory, 0x00000004 | 0x80000000, descriptor))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private void WriteMetadata(string path, CacheMetadata metadata)
    {
        var created = false;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata);
            using var output = CreatePrivateFile(path, FileMode.CreateNew, 4096);
            created = true;
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        catch
        {
            if (created && IsMetadataTemporaryName(Path.GetFileName(path))) TryDeleteGeneratedMetadataTemporary(path);
            throw;
        }
    }

    private static bool IsMetadataTemporaryName(string fileName)
    {
        const string prefix = ".metadata_";
        const string suffix = ".tmp";
        return fileName.Length == prefix.Length + 32 + suffix.Length &&
               fileName.StartsWith(prefix, StringComparison.Ordinal) &&
               fileName.EndsWith(suffix, StringComparison.Ordinal) &&
               Guid.TryParseExact(fileName.AsSpan(prefix.Length, 32), "N", out _);
    }

    private static void TryDeleteGeneratedMetadataTemporary(string path)
    {
        if (!IsMetadataTemporaryName(Path.GetFileName(path))) return;

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0) File.Delete(path);
        }
        catch (FileNotFoundException)
        {
            // The temporary file was already removed or moved.
        }
        catch (DirectoryNotFoundException)
        {
            // The containing snapshot was removed by a concurrent owner cleanup.
        }
        catch (IOException)
        {
            // Preserve the caller's primary failure; the remaining file is still recognized during eviction.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the caller's primary failure; the remaining file is still recognized during eviction.
        }
    }

    private static FileStream CreatePrivateFile(string path, FileMode mode, int bufferSize)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = bufferSize,
            Options = FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (Path.GetFileName(path).StartsWith(".tmp_", StringComparison.Ordinal) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Preserve the original capture error; the private temporary directory remains identifiable for cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original capture error; no files outside this operation's directory are touched.
        }
    }

    private string GetSnapshotDirectory(string snapshotId) => Path.Combine(_rootPath, snapshotId);

    private static bool IsSnapshotId(string snapshotId) =>
        snapshotId.Length == 37 && snapshotId.StartsWith("snap_", StringComparison.Ordinal) &&
        Guid.TryParseExact(snapshotId[5..], "N", out _);

    private static SourceIntakeException MapCaptureFailure(Exception exception) => exception switch
    {
        PathTooLongException => DiagnosticException(IntakeDiagnosticCode.PathTooLong, "The source path exceeds the platform path limit.", exception),
        FileNotFoundException or DirectoryNotFoundException => DiagnosticException(IntakeDiagnosticCode.InputVanished, "The source disappeared before it could be captured.", exception),
        UnauthorizedAccessException => DiagnosticException(IntakeDiagnosticCode.InputUnreadable, "The source could not be opened with the current access rights.", exception),
        _ => DiagnosticException(IntakeDiagnosticCode.SnapshotCaptureFailed,
            "The source snapshot could not be captured; the source may be locked or temporarily unavailable.", exception)
    };

    private static SourceIntakeException DiagnosticException(IntakeDiagnosticCode code, string message, Exception? inner = null) =>
        new(new IntakeDiagnostic(IntakeSeverity.Error, code, message), inner);

    private static void RejectDeviceIdentity(SourceFileIdentity identity)
    {
        if (identity.NativeAttributes is { } attributes && (attributes & FileAttributes.Device) != 0)
        {
            throw DiagnosticException(IntakeDiagnosticCode.SpecialFileSkipped,
                "The source handle is a device object and cannot be captured as a document.");
        }
    }

    private sealed record CacheMetadata(
        int SchemaVersion,
        string SnapshotId,
        string Sha256,
        long LengthBytes,
        DateTimeOffset CapturedAtUtc,
        bool Retained,
        byte[]? ZoneIdentifier = null);

    internal sealed class PhysicalSourceFileAccess : ISourceFileAccess
    {
        public SourceFileIdentity GetPathIdentity(string path)
        {
            using var stream = OpenRead(path);
            return GetOpenedFileIdentity(stream);
        }

        public Stream OpenRead(string path)
        {
            EnsureNoReparsePointComponents(path);

            Stream source;
            if (OperatingSystem.IsWindows())
            {
                var handle = CreateFileW(path, 0x80000000, 0x00000001, IntPtr.Zero, 3,
                    0x00200000 | 0x08000000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw NativeOpenException(error, windows: true);
                }

                source = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            }
            else if (OperatingSystem.IsLinux())
            {
                EnsureSupportedLinuxArchitecture();
                var descriptor = OpenLinux(path, 0x00080000 | 0x00020000 | 0x00000800); // CLOEXEC, NOFOLLOW, NONBLOCK
                if (descriptor < 0)
                {
                    throw NativeOpenException(Marshal.GetLastPInvokeError(), windows: false);
                }

                source = new FileStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true), FileAccess.Read, 64 * 1024, isAsync: false);
            }
            else
            {
                throw DiagnosticException(IntakeDiagnosticCode.SnapshotCaptureFailed,
                    "This platform does not provide a safe no-follow open and handle-based identity check for source capture.");
            }

            try
            {
                EnsureNoReparsePointComponents(path);
                return source;
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        public Stream? OpenZoneIdentifierRead(string path)
        {
            if (!OperatingSystem.IsWindows()) return null;
            EnsureNoReparsePointComponents(path);
            try
            {
                return new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        private static void EnsureNoReparsePointComponents(string path)
        {
            if (SourcePathSafety.FindReparsePointComponent(path) is not null)
            {
                throw DiagnosticException(IntakeDiagnosticCode.ReparsePointSkipped,
                    "The source path traverses a reparse point and was not opened.");
            }
        }

        public SourceFileIdentity GetOpenedFileIdentity(Stream stream)
        {
            if (stream is not FileStream fileStream)
            {
                throw new ArgumentException("The physical source identity requires a file stream.", nameof(stream));
            }

            if (OperatingSystem.IsWindows())
            {
                if (!GetFileInformationByHandle(fileStream.SafeFileHandle, out var information))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                var attributes = (FileAttributes)information.FileAttributes;
                if ((attributes & FileAttributes.Device) != 0)
                {
                    throw DiagnosticException(IntakeDiagnosticCode.SpecialFileSkipped,
                        "The source handle is a device object and cannot be captured as a document.");
                }

                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw DiagnosticException(IntakeDiagnosticCode.ReparsePointSkipped, "Only regular source files can be captured.");
                }

                var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
                var lastWrite = ((long)information.LastWriteTime.High << 32) | information.LastWriteTime.Low;
                return new SourceFileIdentity($"{information.VolumeSerialNumber:x8}:{information.FileIndexHigh:x8}{information.FileIndexLow:x8}",
                    length, DateTime.FromFileTimeUtc(lastWrite).Ticks, attributes);
            }

            if (OperatingSystem.IsLinux())
            {
                EnsureSupportedLinuxArchitecture();
                if (FStat(fileStream.SafeFileHandle.DangerousGetHandle().ToInt32(), out var information) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                if ((information.Mode & 0xf000) != 0x8000)
                {
                    throw DiagnosticException(IntakeDiagnosticCode.SourceBusyOrUnavailable, "Only regular source files can be captured.");
                }

                var ticks = DateTime.UnixEpoch.Ticks + information.ModificationTime.Seconds * TimeSpan.TicksPerSecond + information.ModificationTime.Nanoseconds / 100;
                return new SourceFileIdentity($"{information.Device:x16}:{information.Inode:x16}", information.Size, ticks);
            }

            throw DiagnosticException(IntakeDiagnosticCode.SnapshotCaptureFailed,
                "This platform does not provide a safe handle-based identity check for source capture.");
        }

        private static void EnsureSupportedLinuxArchitecture()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                throw DiagnosticException(IntakeDiagnosticCode.UnsupportedArchitecture,
                    $"Linux source capture does not support the {RuntimeInformation.ProcessArchitecture} process architecture; the verified file-identity layout is x86-64 only.");
            }
        }

        private static SourceIntakeException NativeOpenException(int error, bool windows)
        {
            var code = windows ? error switch
            {
                2 or 3 => IntakeDiagnosticCode.InputVanished,
                5 => IntakeDiagnosticCode.InputUnreadable,
                32 or 33 => IntakeDiagnosticCode.SourceBusyOrUnavailable,
                206 => IntakeDiagnosticCode.PathTooLong,
                4390 or 4392 => IntakeDiagnosticCode.ReparsePointSkipped,
                _ => IntakeDiagnosticCode.SnapshotCaptureFailed
            } : error switch
            {
                2 => IntakeDiagnosticCode.InputVanished,
                13 => IntakeDiagnosticCode.InputUnreadable,
                16 => IntakeDiagnosticCode.SourceBusyOrUnavailable,
                20 => IntakeDiagnosticCode.SourceBusyOrUnavailable,
                36 => IntakeDiagnosticCode.PathTooLong,
                40 => IntakeDiagnosticCode.ReparsePointSkipped,
                _ => IntakeDiagnosticCode.SnapshotCaptureFailed
            };
            var message = code switch
            {
                IntakeDiagnosticCode.InputVanished => "The source disappeared before it could be captured.",
                IntakeDiagnosticCode.InputUnreadable => "The source could not be opened with the current access rights.",
                IntakeDiagnosticCode.SourceBusyOrUnavailable => "The source could not be opened; it may be locked, in use, or temporarily unavailable.",
                IntakeDiagnosticCode.PathTooLong => "The source path exceeds the platform path limit.",
                IntakeDiagnosticCode.ReparsePointSkipped => "The source is a reparse point or provider-managed placeholder and was not followed during capture.",
                _ => "The source could not be opened for snapshot capture."
            };
            return DiagnosticException(code, message, new Win32Exception(error));
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

        [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int OpenLinux(string path, int flags);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int FStat(int descriptor, out LinuxStat information);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public WindowsFileTime CreationTime;
            public WindowsFileTime LastAccessTime;
            public WindowsFileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowsFileTime
        {
            public uint Low;
            public uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LinuxStat
        {
            public ulong Device;
            public ulong Inode;
            public ulong LinkCount;
            public uint Mode;
            public uint UserId;
            public uint GroupId;
            public int Padding;
            public ulong SpecialDevice;
            public long Size;
            public long BlockSize;
            public long Blocks;
            public LinuxTimespec AccessTime;
            public LinuxTimespec ModificationTime;
            public LinuxTimespec ChangeTime;
            public long Reserved0;
            public long Reserved1;
            public long Reserved2;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LinuxTimespec
        {
            public long Seconds;
            public long Nanoseconds;
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileSecurityW(string path, uint information, IntPtr securityDescriptor);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
