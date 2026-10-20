using System.Collections.Immutable;
using System.Diagnostics;
using PaperBatch.Core.Intake;

namespace PaperBatch.Core.Rendering;

/// <summary>The context in which a renderer may be invoked.</summary>
public enum RendererExecutionContext
{
    BackgroundWorker,
    InteractiveDesktop
}

public enum RenderStatus
{
    Succeeded,
    Unsupported,
    Failed,
    TimedOut,
    Cancelled,
    RequiresUserReview
}

/// <summary>Identifies one immutable source snapshot and the requested adapter context.</summary>
public sealed record RenderRequest(
    string SnapshotId,
    SourceFormat Format,
    RendererExecutionContext ExecutionContext);

/// <summary>Render output metadata; paths and source contents are intentionally excluded.</summary>
public sealed record RenderArtifact(string ArtifactId, string Sha256, long LengthBytes, int PageCount);

public sealed record RenderResult(RenderStatus Status, RenderArtifact? Artifact, string? Message)
{
    public static RenderResult Unsupported(string message) => new(RenderStatus.Unsupported, null, message);
    public static RenderResult Failed(string message) => new(RenderStatus.Failed, null, message);
}

/// <summary>
/// Adapter boundary. Implementations own every process and handle they create and must finish
/// cancellation cleanup before completing the returned task. This is crash containment, not a sandbox.
/// </summary>
public interface IRendererAdapter
{
    /// <summary>Capability checks must be bounded, side-effect free, and observe cancellation.</summary>
    ValueTask<bool> CanRenderAsync(RenderRequest request, CancellationToken cancellationToken);
    /// <summary>Owns and cleans every process and handle it starts, including on cancellation.</summary>
    Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs a single selected adapter. It never retries through another renderer.</summary>
public sealed class RendererCoordinator
{
    private readonly ImmutableArray<IRendererAdapter> _adapters;

    public RendererCoordinator(IEnumerable<IRendererAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToImmutableArray();
        if (_adapters.Any(adapter => adapter is null))
        {
            throw new ArgumentException("Renderer adapters cannot contain null entries.", nameof(adapters));
        }
    }

    public async Task<RenderResult> RenderAsync(
        RenderRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SnapshotId))
        {
            throw new ArgumentException("A source snapshot identifier is required.", nameof(request));
        }

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new RenderResult(RenderStatus.Cancelled, null, "Rendering was cancelled.");
        }

        var stopwatch = Stopwatch.StartNew();
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            IRendererAdapter? adapter = null;
            foreach (var candidate in _adapters)
            {
                var supports = await AwaitWithinDeadlineAsync(
                    candidate.CanRenderAsync(request, workerCancellation.Token).AsTask(),
                    timeout, stopwatch, cancellationToken, workerCancellation).ConfigureAwait(false);
                if (!supports) continue;
                adapter = candidate;
                break;
            }

            if (adapter is null)
            {
                return RenderResult.Unsupported("No renderer is available for this format and execution context.");
            }

            var result = await AwaitWithinDeadlineAsync(
                adapter.RenderAsync(request, workerCancellation.Token),
                timeout, stopwatch, cancellationToken, workerCancellation).ConfigureAwait(false);
            return result ?? RenderResult.Failed("The selected renderer returned no result.");
        }
        catch (TimeoutException)
        {
            CancelSafely(workerCancellation);
            return new RenderResult(RenderStatus.TimedOut, null, "The selected renderer exceeded its time limit.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelSafely(workerCancellation);
            return new RenderResult(RenderStatus.Cancelled, null, "Rendering was cancelled.");
        }
        catch (Exception)
        {
            return RenderResult.Failed("The selected renderer failed.");
        }
    }

    private static async Task<T> AwaitWithinDeadlineAsync<T>(
        Task<T> operation,
        TimeSpan timeout,
        Stopwatch stopwatch,
        CancellationToken callerToken,
        CancellationTokenSource workerCancellation)
    {
        try
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                return await operation.WaitAsync(callerToken).ConfigureAwait(false);
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException();
            }

            return await operation.WaitAsync(remaining, callerToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            CancelSafely(workerCancellation);
            await AwaitCancellationCleanupAsync(operation).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            CancelSafely(workerCancellation);
            await AwaitCancellationCleanupAsync(operation).ConfigureAwait(false);
            throw;
        }
    }

    private static void CancelSafely(CancellationTokenSource cancellation)
    {
        if (cancellation.IsCancellationRequested) return;
        try
        {
            cancellation.Cancel();
        }
        catch (Exception)
        {
            // Run every registered callback; keep cleanup/status reporting independent of callback errors.
        }
    }

    private static async Task AwaitCancellationCleanupAsync(Task renderTask)
    {
        try
        {
            await renderTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Preserve the timeout or caller-cancellation result; adapter cleanup is complete.
        }
    }
}
