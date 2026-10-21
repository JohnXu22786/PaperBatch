using System.Security.Cryptography;
using System.Text;

namespace PaperBatch.Core.Watch;

public sealed record WatchFolderOptions(
    string RootPath,
    string RuleId,
    TimeSpan StabilityWindow,
    string? OutputDirectoryName = null,
    bool IncludeOfficeDocuments = true)
{
    public WatchFolderOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RootPath);
        if (string.IsNullOrWhiteSpace(RuleId) || RuleId.Length > 128 || RuleId.Any(char.IsControl))
            throw new ArgumentException("Rule ID must be nonempty, bounded, and contain no control characters.", nameof(RuleId));
        if (StabilityWindow < TimeSpan.Zero || StabilityWindow > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(StabilityWindow), "Stability window must be between zero and ten minutes.");
        if (OutputDirectoryName is { Length: > 128 } || OutputDirectoryName?.Any(char.IsControl) == true)
            throw new ArgumentException("Output directory name must be bounded and contain no control characters.", nameof(OutputDirectoryName));
        return this;
    }
}

public enum WatchFileKind { Pdf, Image, Office }

public sealed record WatchFolderCandidate(string FullPath, string RelativePath, WatchFileKind Kind, long Length, DateTime LastWriteUtc, string Sha256, string IdempotencyKey, bool RequiresUserReview);

/// <summary>Authoritative bounded directory scan. Call after debounced filesystem hints and periodically for reconciliation.</summary>
public sealed class WatchFolderScanner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif" };
    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".pptm", ".wps", ".et", ".dps" };
    private readonly WatchFolderOptions _options;
    private readonly Dictionary<string, Observation> _observations;
    private readonly StringComparer _pathComparer;
    private readonly StringComparison _pathComparison;
    private readonly string _root;
    private readonly IWatchFolderFileAccess _fileAccess;
    private readonly string _rootIdentity;
    private readonly object _scanGate = new();

    public WatchFolderScanner(WatchFolderOptions options, int maximumFiles = 10_000, IWatchFolderFileAccess? fileAccess = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        if (maximumFiles is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        MaximumFiles = maximumFiles;
        _root = Path.GetFullPath(_options.RootPath);
        _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _observations = new Dictionary<string, Observation>(_pathComparer);
        _fileAccess = fileAccess ?? new PhysicalWatchFolderFileAccess();
        _rootIdentity = _fileAccess.GetRootIdentity(_root);
    }

    public int MaximumFiles { get; }
    public const long MaximumFileBytes = 512L * 1024 * 1024;

    public IReadOnlyList<WatchFolderCandidate> Scan(DateTimeOffset? observedAt = null)
    {
        lock (_scanGate) return ScanCore(observedAt);
    }

    private IReadOnlyList<WatchFolderCandidate> ScanCore(DateTimeOffset? observedAt)
    {
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Watch folder is unavailable.");
        if (_fileAccess.GetRootIdentity(_root) != _rootIdentity) throw new IOException("Watch folder root was replaced after the scanner was created.");
        EnsureRootHasNoReparsePointAncestors();
        var now = (observedAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var found = new List<WatchFolderCandidate>();
        var seen = new HashSet<string>(_pathComparer);
        var pending = new Stack<string>();
        pending.Push(_root);
        var inspected = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;

            IEnumerator<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            using (entries)
            while (true)
            {
                string entry;
                try { if (!entries.MoveNext()) break; entry = entries.Current; }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { break; }
                if (++inspected > MaximumFiles) throw new InvalidDataException("Watch folder scan exceeded its configured entry limit.");
                FileAttributes entryAttributes;
                try { entryAttributes = File.GetAttributes(entry); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if ((entryAttributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((entryAttributes & FileAttributes.Directory) != 0)
                {
                    if (!IsOutputDirectory(entry)) pending.Push(entry);
                    continue;
                }

                if (IsIgnored(entry)) continue;
                if (!TryClassify(Path.GetExtension(entry), out var kind)) continue;
                var fullPath = Path.GetFullPath(entry);
                if (!IsWithinRoot(fullPath)) continue;
                seen.Add(fullPath);
                if (TryObserve(fullPath, kind, now) is { } candidate) found.Add(candidate);
            }
        }

        foreach (var stale in _observations.Keys.Where(path => !seen.Contains(path)).ToArray()) _observations.Remove(stale);
        return found.OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private WatchFolderCandidate? TryObserve(string path, WatchFileKind kind, DateTime now)
    {
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists || before.Length < 0 || before.Length > MaximumFileBytes) return null;
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            if (!_observations.TryGetValue(path, out var observation) || observation.Length != length || observation.ModifiedUtc != modified)
            {
                _observations[path] = new Observation(length, modified, now);
                return null;
            }
            if (now - observation.FirstSeenUtc < _options.StabilityWindow) return null;

            var relative = Path.GetRelativePath(_root, path);
            string digest;
            using (var stream = _fileAccess.OpenRead(_root, _rootIdentity, relative))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var openedIdentity = _fileAccess.GetOpenedIdentity(stream);
                if (openedIdentity.LengthBytes != length || openedIdentity.LastWriteTimeUtcTicks != modified.Ticks) return null;
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaximumFileBytes) return null;
                    hash.AppendData(buffer, 0, read);
                }
                if (total != length || _fileAccess.GetOpenedIdentity(stream) != openedIdentity) return null;
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            var generation = string.Join("\n", _options.RuleId, _root, relative, length.ToString(System.Globalization.CultureInfo.InvariantCulture), modified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), digest);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generation))).ToLowerInvariant();
            return new WatchFolderCandidate(path, relative, kind, length, modified, digest, key, kind == WatchFileKind.Office);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private void EnsureRootHasNoReparsePointAncestors()
    {
        var current = _root;
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Watch folder cannot be beneath a linked path.");
            }
            catch (FileNotFoundException) { }
            current = Directory.GetParent(current)?.FullName;
        }
    }

    private bool IsOutputDirectory(string path) => _options.OutputDirectoryName is { Length: > 0 } name &&
        string.Equals(Path.GetFileName(path), name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsIgnored(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return name.StartsWith("~$", StringComparison.Ordinal) || extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".temp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) || extension.Equals(".download", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("~", StringComparison.Ordinal);
    }

    private bool TryClassify(string extension, out WatchFileKind kind)
    {
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) { kind = WatchFileKind.Pdf; return true; }
        if (ImageExtensions.Contains(extension)) { kind = WatchFileKind.Image; return true; }
        if (_options.IncludeOfficeDocuments && OfficeExtensions.Contains(extension)) { kind = WatchFileKind.Office; return true; }
        kind = default;
        return false;
    }

    private bool IsWithinRoot(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, _pathComparison) && !Path.IsPathRooted(relative);
    }

    private sealed record Observation(long Length, DateTime ModifiedUtc, DateTime FirstSeenUtc);
}
