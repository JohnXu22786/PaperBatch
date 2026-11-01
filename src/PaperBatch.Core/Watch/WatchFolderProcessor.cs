using System.Diagnostics;

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

/// <summary>Debounces filesystem hints while allowing a maximum delay for reconciliation.</summary>
public sealed class WatchFolderDebouncer(TimeSpan quietPeriod, TimeSpan? maximumDelay = null)
{
    private readonly object _gate = new();
    private DateTimeOffset? _firstHint;
    private DateTimeOffset? _lastHint;
    public TimeSpan QuietPeriod { get; } = quietPeriod >= TimeSpan.Zero && quietPeriod <= TimeSpan.FromMinutes(1)
        ? quietPeriod : throw new ArgumentOutOfRangeException(nameof(quietPeriod));
    public TimeSpan? MaximumDelay { get; } = maximumDelay is null || maximumDelay >= TimeSpan.Zero && maximumDelay <= TimeSpan.FromHours(1)
        ? maximumDelay : throw new ArgumentOutOfRangeException(nameof(maximumDelay));

    public void NotifyChange(DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            var hint = at ?? DateTimeOffset.UtcNow;
            _firstHint ??= hint;
            _lastHint = hint;
        }
    }

    public bool TryTakeDueScan(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (!IsDue(now ?? DateTimeOffset.UtcNow)) return false;
            _firstHint = null;
            _lastHint = null;
            return true;
        }
    }

    public TimeSpan GetWaitTimeUntilDue(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (_firstHint is not { } firstHint || _lastHint is not { } lastHint) return TimeSpan.Zero;
            var current = now ?? DateTimeOffset.UtcNow;
            var quietRemaining = QuietPeriod - (current - lastHint);
            if (MaximumDelay is not { } maximumDelay) return quietRemaining > TimeSpan.Zero ? quietRemaining : TimeSpan.Zero;
            var maximumRemaining = maximumDelay - (current - firstHint);
            var remaining = quietRemaining < maximumRemaining ? quietRemaining : maximumRemaining;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    private bool IsDue(DateTimeOffset now)
    {
        if (_firstHint is not { } firstHint || _lastHint is not { } lastHint) return false;
        return now - lastHint >= QuietPeriod || MaximumDelay is { } maximumDelay && now - firstHint >= maximumDelay;
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
        var lastReconciledAt = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            var hadHint = false;
            try
            {
                await hints.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(reconciliationInterval, cancellationToken);
                hadHint = true;
            }
            catch (TimeoutException) { }

            var remainingUntilReconciliation = reconciliationInterval - Stopwatch.GetElapsedTime(lastReconciledAt);
            if (hadHint && debounceWindow > TimeSpan.Zero && remainingUntilReconciliation > TimeSpan.Zero)
            {
                var debouncer = new WatchFolderDebouncer(debounceWindow, remainingUntilReconciliation);
                debouncer.NotifyChange();
                while (true)
                {
                    if (hints.Reader.TryRead(out _)) debouncer.NotifyChange();
                    var waitTime = debouncer.GetWaitTimeUntilDue();
                    remainingUntilReconciliation = reconciliationInterval - Stopwatch.GetElapsedTime(lastReconciledAt);
                    if (waitTime == TimeSpan.Zero || remainingUntilReconciliation <= TimeSpan.Zero) break;
                    if (waitTime > remainingUntilReconciliation) waitTime = remainingUntilReconciliation;
                    try
                    {
                        await hints.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(waitTime, cancellationToken);
                        debouncer.NotifyChange();
                    }
                    catch (TimeoutException) { break; }
                }
            }
            await processor.ReconcileAsync(cancellationToken: cancellationToken);
            lastReconciledAt = Stopwatch.GetTimestamp();
        }
    }
}
