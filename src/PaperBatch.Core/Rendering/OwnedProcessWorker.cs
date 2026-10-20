using System.ComponentModel;
using System.Diagnostics;

namespace PaperBatch.Core.Rendering;

public enum OwnedProcessStatus
{
    Exited,
    TimedOut,
    Cancelled,
    Failed
}

public sealed record OwnedProcessResult(OwnedProcessStatus Status, int? ProcessId, int? ExitCode, string? Message);

/// <summary>
/// Runs one directly launched child process and retains its process handle through completion.
/// Timeout and cancellation terminate only this process and its descendants, then wait for exit.
/// This contains crashes but is not a security sandbox.
/// </summary>
public static class OwnedProcessWorker
{
    public static async Task<OwnedProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            throw new ArgumentException("A direct executable path is required.", nameof(startInfo));
        }

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new OwnedProcessResult(OwnedProcessStatus.Cancelled, null, null, "Process launch was cancelled.");
        }

        if (startInfo.UseShellExecute)
        {
            throw new ArgumentException("Owned process execution requires shell execution to be disabled.", nameof(startInfo));
        }

        if (startInfo.RedirectStandardInput || startInfo.RedirectStandardOutput || startInfo.RedirectStandardError)
        {
            throw new ArgumentException("Owned process execution does not permit unconsumed redirected streams.", nameof(startInfo));
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new OwnedProcessResult(OwnedProcessStatus.Failed, null, null, "The renderer process did not start.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new OwnedProcessResult(OwnedProcessStatus.Failed, null, null, "The renderer process could not be started.");
        }

        var processId = process.Id;
        try
        {
            var exitTask = process.WaitForExitAsync();
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await exitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await exitTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }

            return new OwnedProcessResult(OwnedProcessStatus.Exited, processId, process.ExitCode, null);
        }
        catch (TimeoutException)
        {
            return await StopOwnedProcessAsync(process, processId, OwnedProcessStatus.TimedOut).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await StopOwnedProcessAsync(process, processId, OwnedProcessStatus.Cancelled).ConfigureAwait(false);
        }
        catch (Exception) when (process.HasExited)
        {
            return new OwnedProcessResult(OwnedProcessStatus.Exited, processId, process.ExitCode, null);
        }
        catch (Exception)
        {
            return await StopOwnedProcessAsync(process, processId, OwnedProcessStatus.Failed).ConfigureAwait(false);
        }
    }

    private static async Task<OwnedProcessResult> StopOwnedProcessAsync(
        Process process,
        int processId,
        OwnedProcessStatus requestedStatus)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            return new OwnedProcessResult(requestedStatus, processId, null,
                requestedStatus switch
                {
                    OwnedProcessStatus.TimedOut => "The renderer process exceeded its time limit and was stopped.",
                    OwnedProcessStatus.Cancelled => "The renderer process was cancelled and stopped.",
                    _ => "The renderer process failed and was stopped."
                });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            if (process.HasExited)
            {
                return new OwnedProcessResult(requestedStatus, processId, null, "The renderer process stopped.");
            }

            return new OwnedProcessResult(OwnedProcessStatus.Failed, processId, null,
                "The renderer process could not be stopped safely.");
        }
    }
}
