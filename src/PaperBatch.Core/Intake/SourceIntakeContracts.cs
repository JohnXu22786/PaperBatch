using System.Collections.Immutable;
using PaperBatch.Core.Domain;

namespace PaperBatch.Core.Intake;

public enum IntakeSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>Stable, path-free intake diagnostic identifiers for logs and UI presentation.</summary>
public enum IntakeDiagnosticCode
{
    InputVanished,
    InvalidPath,
    PathTooLong,
    InputUnreadable,
    SourceBusyOrUnavailable,
    ReparsePointSkipped,
    EnumerationLimitExceeded,
    CancellationRequested,
    DuplicatePath,
    UnicodePathSupported,
    RemoteStorageStatusUnknown,
    UnsupportedFormat,
    EmptySource,
    MalformedContainer,
    ExtensionSignatureMismatch,
    EncryptedOrPasswordProtected,
    UnsafePackage,
    SourceChangedDuringCapture,
    CacheQuotaExceeded,
    CacheRootNotPrivate,
    CacheEntryCorrupt,
    SnapshotCaptureFailed,
    UnsupportedArchitecture,
    SpecialFileSkipped
}

public sealed record IntakeDiagnostic(IntakeSeverity Severity, IntakeDiagnosticCode Code, string Message);

/// <summary>A user-selected path and whether a repeated path is an intentional extra copy.</summary>
public sealed record SourceInput(string Path, bool IntentionalCopy = false);

/// <summary>Provides path attributes for deterministic source-enumeration tests and adapters.</summary>
public interface ISourcePathAttributeAccess
{
    FileAttributes GetAttributes(string path);
}

public sealed record SourceEnumerationOptions
{
    public bool Recursive { get; init; }
    public int MaximumEntries { get; init; } = 100_000;
    public int MaximumDepth { get; init; } = 128;
    public int MaximumPathLength { get; init; } = 32_767;
}

/// <summary>An ephemeral selection result. Path is never copied into a prepared run or diagnostic.</summary>
public sealed record SourceEnumerationItem(
    string Path,
    bool IsDuplicate,
    string? DuplicateOfPath,
    bool IsIntentionalCopy,
    ImmutableArray<IntakeDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == IntakeSeverity.Error);
}

public sealed record SourceEnumerationResult(
    ImmutableArray<SourceEnumerationItem> Items,
    ImmutableArray<IntakeDiagnostic> Diagnostics,
    bool IsCancelled);

public static class SourceEnumerator
{
    public static SourceEnumerationResult Enumerate(
        IEnumerable<SourceInput> inputs,
        SourceEnumerationOptions? options = null,
        CancellationToken cancellationToken = default,
        ISourcePathAttributeAccess? attributeAccess = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        options ??= new SourceEnumerationOptions();
        attributeAccess ??= PhysicalSourcePathAttributeAccess.Instance;
        if (options.MaximumEntries <= 0 || options.MaximumDepth <= 0 || options.MaximumPathLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Enumeration limits must be positive.");
        }

        var items = ImmutableArray.CreateBuilder<SourceEnumerationItem>();
        var diagnostics = ImmutableArray.CreateBuilder<IntakeDiagnostic>();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var firstPaths = new Dictionary<string, string>(comparer);
        var visitedEntryCount = 0;
        var entryLimitExceeded = false;
        var cancelled = false;

        foreach (var input in inputs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                AddCancellation();
                break;
            }

            if (entryLimitExceeded)
            {
                break;
            }

            if (++visitedEntryCount > options.MaximumEntries)
            {
                AddEntryLimitFailure(input?.Path ?? string.Empty, "The selected sources exceed the configured enumeration limit.");
                break;
            }

            if (input is null || string.IsNullOrWhiteSpace(input.Path))
            {
                AddFailure(string.Empty, IntakeDiagnosticCode.InvalidPath, "The selected path is empty or invalid.");
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(input.Path);
            }
            catch (PathTooLongException)
            {
                AddFailure(input.Path, IntakeDiagnosticCode.PathTooLong, "The selected path exceeds the platform path limit.");
                continue;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                AddFailure(input.Path, IntakeDiagnosticCode.InvalidPath, "The selected path is not valid.");
                continue;
            }

            if (fullPath.Length > options.MaximumPathLength)
            {
                AddFailure(fullPath, IntakeDiagnosticCode.PathTooLong, "The selected path exceeds the configured path limit.");
                continue;
            }

            if (fullPath.Any(character => character > 0x7f))
            {
                diagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Information, IntakeDiagnosticCode.UnicodePathSupported,
                    "The selected path contains Unicode characters and will be used as supplied."));
            }

            if (IsNetworkPath(fullPath, OperatingSystem.IsWindows()))
            {
                diagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Information, IntakeDiagnosticCode.RemoteStorageStatusUnknown,
                    "The source is on a network path; provider availability can change during intake."));
            }

            FileAttributes attributes;
            try
            {
                if (SourcePathSafety.FindReparsePointComponent(fullPath) is not null)
                {
                    AddFailure(fullPath, IntakeDiagnosticCode.ReparsePointSkipped,
                        "The selected path traverses a reparse point and was skipped.");
                    continue;
                }

                attributes = attributeAccess.GetAttributes(fullPath);
            }
            catch (Exception exception) when (IsPathAccessException(exception))
            {
                AddFailure(fullPath, MapAccessFailure(exception), DescribeAccessFailure(exception));
                continue;
            }

            if ((attributes & FileAttributes.Device) != 0)
            {
                AddFailure(fullPath, IntakeDiagnosticCode.SpecialFileSkipped,
                    "The selected device object cannot be imported as a document.");
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                AddFailure(fullPath, IntakeDiagnosticCode.ReparsePointSkipped,
                    "The selected reparse point was skipped; it may be a symbolic link or a provider-managed placeholder.");
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                VisitDirectory(fullPath, depth: 0, input.IntentionalCopy);
            }
            else
            {
                AddFile(fullPath, input.IntentionalCopy);
            }
        }

        if (cancelled && diagnostics.All(diagnostic => diagnostic.Code != IntakeDiagnosticCode.CancellationRequested))
        {
            diagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Warning, IntakeDiagnosticCode.CancellationRequested,
                "Source enumeration was cancelled."));
        }

        return new SourceEnumerationResult(
            items.ToImmutable(),
            diagnostics.Concat(items.SelectMany(item => item.Diagnostics)).Distinct().ToImmutableArray(),
            cancelled);

        void VisitDirectory(string directory, int depth, bool intentionalCopy)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                AddCancellation();
                return;
            }

            if (depth > options.MaximumDepth)
            {
                AddFailure(directory, IntakeDiagnosticCode.EnumerationLimitExceeded,
                    "The selected folder exceeds the configured recursion depth.");
                return;
            }

            try
            {
                if (SourcePathSafety.FindReparsePointComponent(directory) is not null)
                {
                    AddFailure(directory, IntakeDiagnosticCode.ReparsePointSkipped,
                        "The selected folder traverses a reparse point and was skipped.");
                    return;
                }
            }
            catch (Exception exception) when (IsPathAccessException(exception))
            {
                AddFailure(directory, MapAccessFailure(exception), DescribeAccessFailure(exception));
                return;
            }

            var children = new List<string>();
            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        AddCancellation();
                        return;
                    }

                    if (++visitedEntryCount > options.MaximumEntries)
                    {
                        AddEntryLimitFailure(directory, "The selected folders exceed the configured enumeration limit.");
                        return;
                    }

                    children.Add(child);
                }
            }
            catch (Exception exception) when (IsPathAccessException(exception))
            {
                AddFailure(directory, MapAccessFailure(exception), DescribeAccessFailure(exception));
            }

            children.Sort(NaturalPathComparer.Instance);
            foreach (var child in children)
            {
                if (entryLimitExceeded)
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    AddCancellation();
                    return;
                }

                FileAttributes childAttributes;
                if (child.Length > options.MaximumPathLength)
                {
                    AddFailure(child, IntakeDiagnosticCode.PathTooLong, "The source path exceeds the configured path limit.");
                    continue;
                }

                try
                {
                    childAttributes = attributeAccess.GetAttributes(child);
                }
                catch (Exception exception) when (IsPathAccessException(exception))
                {
                    AddFailure(child, MapAccessFailure(exception), DescribeAccessFailure(exception));
                    continue;
                }

                if ((childAttributes & FileAttributes.Device) != 0)
                {
                    AddFailure(child, IntakeDiagnosticCode.SpecialFileSkipped,
                        "The device object cannot be imported as a document.");
                }
                else if ((childAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    AddFailure(child, IntakeDiagnosticCode.ReparsePointSkipped,
                        "The reparse point was skipped; it may be a symbolic link or a provider-managed placeholder.");
                }
                else if ((childAttributes & FileAttributes.Directory) != 0)
                {
                    if (options.Recursive)
                    {
                        VisitDirectory(child, depth + 1, intentionalCopy);
                    }
                }
                else
                {
                    AddFile(Path.GetFullPath(child), intentionalCopy);
                }
            }
        }

        void AddFile(string fullPath, bool intentionalCopy)
        {
            string? duplicateOf = null;
            var isDuplicate = false;
            if (firstPaths.TryGetValue(fullPath, out var firstPath))
            {
                if (intentionalCopy)
                {
                    // Keep the duplicate in the original selection position without raising a duplicate error.
                }
                else
                {
                    duplicateOf = firstPath;
                    isDuplicate = true;
                }
            }
            else
            {
                firstPaths.Add(fullPath, fullPath);
            }

            var itemDiagnostics = ImmutableArray.CreateBuilder<IntakeDiagnostic>();
            if (fullPath.Any(character => character > 0x7f))
            {
                itemDiagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Information, IntakeDiagnosticCode.UnicodePathSupported,
                    "The selected path contains Unicode characters and will be used as supplied."));
            }

            if (IsNetworkPath(fullPath, OperatingSystem.IsWindows()))
            {
                itemDiagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Information, IntakeDiagnosticCode.RemoteStorageStatusUnknown,
                    "The source is on a network path; provider availability can change during intake."));
            }

            if (isDuplicate)
            {
                itemDiagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Warning, IntakeDiagnosticCode.DuplicatePath,
                    "This selection resolves to a path already included earlier in the request."));
            }

            items.Add(new SourceEnumerationItem(fullPath, isDuplicate, duplicateOf, intentionalCopy, itemDiagnostics.ToImmutable()));
        }

        void AddFailure(string path, IntakeDiagnosticCode code, string message)
        {
            var itemDiagnostic = new IntakeDiagnostic(IntakeSeverity.Error, code, message);
            items.Add(new SourceEnumerationItem(path, false, null, false, [itemDiagnostic]));
            diagnostics.Add(itemDiagnostic);
        }

        void AddEntryLimitFailure(string path, string message)
        {
            entryLimitExceeded = true;
            AddFailure(path, IntakeDiagnosticCode.EnumerationLimitExceeded, message);
        }

        void AddCancellation()
        {
            cancelled = true;
            if (diagnostics.All(diagnostic => diagnostic.Code != IntakeDiagnosticCode.CancellationRequested))
            {
                diagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Warning, IntakeDiagnosticCode.CancellationRequested,
                    "Source enumeration was cancelled."));
            }
        }
    }

    /// <summary>Identifies UNC paths while excluding Windows local device namespaces.</summary>
    public static bool IsNetworkPath(string path, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!isWindows) return false;
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal)) return false;
        return path.StartsWith("\\\\", StringComparison.Ordinal);
    }

    private sealed class PhysicalSourcePathAttributeAccess : ISourcePathAttributeAccess
    {
        public static PhysicalSourcePathAttributeAccess Instance { get; } = new();

        public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    }

    private static bool IsPathAccessException(Exception exception) =>
        exception is PathTooLongException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException;

    private static IntakeDiagnosticCode MapAccessFailure(Exception exception) => exception switch
    {
        PathTooLongException => IntakeDiagnosticCode.PathTooLong,
        FileNotFoundException or DirectoryNotFoundException => IntakeDiagnosticCode.InputVanished,
        UnauthorizedAccessException => IntakeDiagnosticCode.InputUnreadable,
        _ => IntakeDiagnosticCode.SourceBusyOrUnavailable
    };

    private static string DescribeAccessFailure(Exception exception) => exception switch
    {
        PathTooLongException => "The source path exceeds the platform path limit.",
        FileNotFoundException or DirectoryNotFoundException => "The selected source no longer exists or is unavailable.",
        UnauthorizedAccessException => "The selected source cannot be read with the current access rights.",
        _ => "The selected source could not be read; it may be locked, in use, or temporarily unavailable."
    };

    private sealed class NaturalPathComparer : IComparer<string>
    {
        public static NaturalPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            var indexLeft = 0;
            var indexRight = 0;
            while (indexLeft < left.Length && indexRight < right.Length)
            {
                if (char.IsAsciiDigit(left[indexLeft]) && char.IsAsciiDigit(right[indexRight]))
                {
                    var endLeft = indexLeft;
                    var endRight = indexRight;
                    while (endLeft < left.Length && char.IsAsciiDigit(left[endLeft])) endLeft++;
                    while (endRight < right.Length && char.IsAsciiDigit(right[endRight])) endRight++;
                    var significantLeft = indexLeft;
                    var significantRight = indexRight;
                    while (significantLeft < endLeft - 1 && left[significantLeft] == '0') significantLeft++;
                    while (significantRight < endRight - 1 && right[significantRight] == '0') significantRight++;
                    var digitsLeft = endLeft - significantLeft;
                    var digitsRight = endRight - significantRight;
                    var comparison = digitsLeft.CompareTo(digitsRight);
                    if (comparison == 0)
                    {
                        comparison = string.Compare(left, significantLeft, right, significantRight, digitsLeft, StringComparison.Ordinal);
                    }

                    if (comparison != 0) return comparison;
                    indexLeft = endLeft;
                    indexRight = endRight;
                    continue;
                }

                var foldedLeft = char.ToUpperInvariant(left[indexLeft]);
                var foldedRight = char.ToUpperInvariant(right[indexRight]);
                var foldedComparison = foldedLeft.CompareTo(foldedRight);
                if (foldedComparison != 0) return foldedComparison;
                indexLeft++;
                indexRight++;
            }

            var lengthComparison = (left.Length - indexLeft).CompareTo(right.Length - indexRight);
            return lengthComparison != 0 ? lengthComparison : string.Compare(left, right, StringComparison.Ordinal);
        }
    }
}

internal static class SourcePathSafety
{
    public static string? FindReparsePointComponent(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return null;

        var current = root;
        foreach (var component in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return current;
        }

        return null;
    }
}

public sealed record SourcePreparationItem(
    string SourcePath,
    SourceSnapshot? Snapshot,
    SourceFormat? Format,
    bool IsReady,
    bool IsDuplicate,
    ImmutableArray<IntakeDiagnostic> Diagnostics);

public sealed record SourcePreparationResult(ImmutableArray<SourcePreparationItem> Items, bool IsCancelled);

/// <summary>Copies selected sources into the private cache before inspecting them for renderer use.</summary>
public sealed class SourceIntakeService(
    SourceSnapshotCache cache,
    Func<Stream, string, SourcePreflightOptions?, SourcePreflightResult>? preflightInspector = null)
{
    private readonly Func<Stream, string, SourcePreflightOptions?, SourcePreflightResult> _preflightInspector =
        preflightInspector ?? SourceFormatPreflight.Inspect;

    public async Task<SourcePreparationResult> PrepareAsync(
        IEnumerable<SourceInput> inputs,
        SourceEnumerationOptions? enumerationOptions = null,
        SourcePreflightOptions? preflightOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var enumeration = SourceEnumerator.Enumerate(inputs, enumerationOptions, cancellationToken);
        var results = ImmutableArray.CreateBuilder<SourcePreparationItem>(enumeration.Items.Length);

        foreach (var selected in enumeration.Items)
        {
            var diagnostics = ImmutableArray.CreateBuilder<IntakeDiagnostic>();
            diagnostics.AddRange(selected.Diagnostics);
            if (cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Warning, IntakeDiagnosticCode.CancellationRequested,
                    "Source preparation was cancelled."));
                results.Add(new SourcePreparationItem(selected.Path, null, null, false, selected.IsDuplicate, diagnostics.ToImmutable()));
                continue;
            }

            if (selected.HasErrors || (selected.IsDuplicate && !selected.IsIntentionalCopy))
            {
                results.Add(new SourcePreparationItem(selected.Path, null, null, false, selected.IsDuplicate, diagnostics.ToImmutable()));
                continue;
            }

            SourceSnapshot snapshot;
            try
            {
                snapshot = await cache.CaptureAsync(selected.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceIntakeException exception)
            {
                diagnostics.Add(exception.Diagnostic);
                results.Add(new SourcePreparationItem(selected.Path, null, null, false, selected.IsDuplicate, diagnostics.ToImmutable()));
                continue;
            }

            SourcePreflightResult preflight;
            try
            {
                using var captured = cache.OpenRead(snapshot.SnapshotId);
                preflight = _preflightInspector(captured, Path.GetExtension(selected.Path), preflightOptions);
            }
            catch (SourceIntakeException exception)
            {
                TryRelease(snapshot.SnapshotId, diagnostics);
                diagnostics.Add(exception.Diagnostic);
                results.Add(new SourcePreparationItem(selected.Path, null, null, false, selected.IsDuplicate, diagnostics.ToImmutable()));
                continue;
            }
            catch (IOException)
            {
                TryRelease(snapshot.SnapshotId, diagnostics);
                diagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Error, IntakeDiagnosticCode.CacheEntryCorrupt,
                    "The cached snapshot could not be read during preflight."));
                results.Add(new SourcePreparationItem(selected.Path, null, null, false, selected.IsDuplicate, diagnostics.ToImmutable()));
                continue;
            }

            diagnostics.AddRange(preflight.Diagnostics);
            if (preflight.Status != SourcePreflightStatus.Ready)
            {
                TryRelease(snapshot.SnapshotId, diagnostics);
                results.Add(new SourcePreparationItem(selected.Path, null, preflight.Format, false, selected.IsDuplicate, diagnostics.ToImmutable()));
                continue;
            }

            results.Add(new SourcePreparationItem(selected.Path, snapshot, preflight.Format, true, selected.IsDuplicate, diagnostics.ToImmutable()));
        }

        return new SourcePreparationResult(results.MoveToImmutable(), enumeration.IsCancelled || cancellationToken.IsCancellationRequested);

        void TryRelease(string snapshotId, ImmutableArray<IntakeDiagnostic>.Builder itemDiagnostics)
        {
            try
            {
                cache.Release(snapshotId);
            }
            catch (SourceIntakeException exception)
            {
                itemDiagnostics.Add(exception.Diagnostic with
                {
                    Severity = IntakeSeverity.Warning,
                    Message = "The rejected snapshot could not be marked for cleanup and may need later cache maintenance."
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                itemDiagnostics.Add(new IntakeDiagnostic(IntakeSeverity.Warning, IntakeDiagnosticCode.SnapshotCaptureFailed,
                    "The rejected snapshot could not be marked for cleanup and may need later cache maintenance."));
            }
        }
    }
}
