namespace PaperBatch.Core.Watch;

public sealed record WatchFolderPolicy(bool AutoSubmitTrustedRule = false, bool RuleIsTrusted = false);

public enum WatchProcessingState { PreparedForReview, AutoSubmitRequested, Failed }

public sealed record WatchProcessingResult(WatchFolderCandidate Candidate, WatchProcessingState State, string IdempotencyKey, string? FailureReason = null);

internal sealed class WatchPreparationFailedException(string message) : Exception(message);

/// <summary>Adapter to the shared coordinator. Implementations must persist and enforce the supplied idempotency key.</summary>
public interface IWatchFolderCoordinator
{
    ValueTask ProcessAsync(WatchFolderCandidate candidate, bool submitAutomatically, CancellationToken cancellationToken = default);
}

/// <summary>Connects authoritative scans to the existing coordinator without implementing or duplicating job orchestration.</summary>
public sealed class WatchFolderProcessor(WatchFolderScanner scanner, IWatchFolderCoordinator coordinator, WatchFolderPolicy? policy = null)
{
    private readonly WatchFolderScanner _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    private readonly IWatchFolderCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly WatchFolderPolicy _policy = policy ?? new WatchFolderPolicy();

    public async ValueTask<IReadOnlyList<WatchProcessingResult>> ReconcileAsync(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var results = new List<WatchProcessingResult>();
        foreach (var candidate in _scanner.Scan(now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var autoSubmit = _policy.AutoSubmitTrustedRule && _policy.RuleIsTrusted && !candidate.RequiresUserReview;
            try
            {
                await _coordinator.ProcessAsync(candidate, autoSubmit, cancellationToken);
                results.Add(new WatchProcessingResult(candidate, autoSubmit ? WatchProcessingState.AutoSubmitRequested : WatchProcessingState.PreparedForReview, candidate.IdempotencyKey));
            }
            catch (WatchPreparationFailedException exception)
            {
                results.Add(new WatchProcessingResult(candidate, WatchProcessingState.Failed, candidate.IdempotencyKey, exception.Message));
            }
        }
        return results;
    }
}

/// <summary>Debounces filesystem hints; periodic callers must still invoke ReconcileAsync to recover missed events.</summary>
public sealed class WatchFolderDebouncer(TimeSpan quietPeriod)
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastHint;
    public TimeSpan QuietPeriod { get; } = quietPeriod >= TimeSpan.Zero && quietPeriod <= TimeSpan.FromMinutes(1)
        ? quietPeriod : throw new ArgumentOutOfRangeException(nameof(quietPeriod));

    public void NotifyChange(DateTimeOffset? at = null)
    {
        lock (_gate) _lastHint = at ?? DateTimeOffset.UtcNow;
    }

    public bool TryTakeDueScan(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (_lastHint is not { } hint || (now ?? DateTimeOffset.UtcNow) - hint < QuietPeriod) return false;
            _lastHint = null;
            return true;
        }
    }
}

/// <summary>Watches for debounced hints and periodically reconciles the complete tree to recover missed events.</summary>
public sealed class WatchFolderMonitor(string rootPath, WatchFolderProcessor processor, TimeSpan debounceWindow, TimeSpan reconciliationInterval)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(processor);
        if (debounceWindow < TimeSpan.Zero || debounceWindow > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(debounceWindow));
        if (reconciliationInterval < TimeSpan.FromSeconds(1) || reconciliationInterval > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Watch folder is unavailable.");
        var hints = System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1) { SingleReader = true, SingleWriter = false, FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });
        using var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
        FileSystemEventHandler changed = (_, _) => hints.Writer.TryWrite(true);
        RenamedEventHandler renamed = (_, _) => hints.Writer.TryWrite(true);
        ErrorEventHandler error = (_, _) => hints.Writer.TryWrite(true);
        watcher.Created += changed;
        watcher.Changed += changed;
        watcher.Deleted += changed;
        watcher.Renamed += renamed;
        watcher.Error += error;
        watcher.EnableRaisingEvents = true;

        await processor.ReconcileAsync(cancellationToken: cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var hadHint = false;
            try
            {
                await hints.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(reconciliationInterval, cancellationToken);
                hadHint = true;
            }
            catch (TimeoutException) { }

            if (hadHint && debounceWindow > TimeSpan.Zero)
            {
                while (true)
                {
                    while (hints.Reader.TryRead(out _)) { }
                    try { await hints.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(debounceWindow, cancellationToken); }
                    catch (TimeoutException) { break; }
                }
            }
            await processor.ReconcileAsync(cancellationToken: cancellationToken);
        }
    }
}
