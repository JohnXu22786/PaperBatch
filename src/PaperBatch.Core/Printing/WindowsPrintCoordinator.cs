using PaperBatch.Core.Domain;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Serialization;
using PaperBatch.Core.Rendering;

namespace PaperBatch.Core.Printing;

/// <summary>Coordinates one prepared segment against a single queue and the existing durable job state model.</summary>
public sealed class WindowsPrintCoordinator(
    SqliteRunStore store,
    IWindowsPrintBackend backend,
    Func<string>? nonceFactory = null,
    RenderArtifactStore? artifacts = null)
{
    private const int MaximumRasterizedPageCount = 500;
    private readonly Func<string> _nonceFactory = nonceFactory ?? (() => Guid.NewGuid().ToString("N"));

    /// <summary>Submits ready segments in order and stops at the first non-accepted result.</summary>
    public async Task<IReadOnlyList<PrintSubmissionResult>> SubmitRunAsync(
        PreparedRun preparedRun, string idempotencyKeyPrefix, DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedRun);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKeyPrefix);
        var results = new List<PrintSubmissionResult>();
        var jobs = store.GetJobs(preparedRun.RunId).ToArray();
        if (preparedRun.Segments.All(segment => !segment.FinalPageNumbers.IsDefaultOrEmpty) &&
            jobs.All(job => job.SegmentIndex >= 0 && job.SegmentIndex < preparedRun.Segments.Length))
        {
            jobs = jobs.OrderBy(job => preparedRun.Segments[job.SegmentIndex].FinalPageNumbers[0]).ToArray();
        }
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (job.State is JobState.AcceptedByOs or JobState.OsProcessing or JobState.OsReportsComplete) continue;
            if (job.State is not (JobState.Ready or JobState.FailedBeforeSubmit)) break;
            var result = await SubmitAsync(job.JobId, preparedRun,
                $"{idempotencyKeyPrefix}-{job.JobId}", atUtc, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (result.Status != PrintSubmissionStatus.Accepted) break;
        }
        return results;
    }

    public async Task<PrintSubmissionResult> SubmitAsync(
        string jobId,
        PreparedRun preparedRun,
        string idempotencyKey,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(preparedRun);
        var job = store.GetJob(jobId) ?? throw new InvalidOperationException("The requested print job does not exist.");
        if (!string.Equals(job.RunId, preparedRun.RunId, StringComparison.Ordinal))
            throw new InvalidOperationException("The prepared run does not own the requested job.");
        var persistedRun = store.GetPreparedRun(job.RunId)
            ?? throw new InvalidDataException("The requested print job has no persisted prepared run.");
        if (!string.Equals(PreparedRunJson.Serialize(persistedRun), PreparedRunJson.Serialize(preparedRun), StringComparison.Ordinal))
            throw new InvalidOperationException("The supplied prepared run does not match the persisted run.");
        if (preparedRun.FinalArtifact is not { } finalArtifact || artifacts is null)
            return new PrintSubmissionResult(PrintSubmissionStatus.Rejected, job,
                "A persisted final PDF artifact and configured artifact store are required for submission.");
        var storedArtifact = artifacts.GetReceipt(finalArtifact.ArtifactId).Artifact;
        if (!string.Equals(storedArtifact.Sha256, finalArtifact.Sha256, StringComparison.Ordinal) ||
            storedArtifact.LengthBytes != finalArtifact.LengthBytes)
            throw new InvalidDataException("The final PDF artifact receipt does not match the prepared run.");
        await using var pdfContent = artifacts.OpenRead(finalArtifact.ArtifactId);
        if (job.State is not (JobState.Ready or JobState.FailedBeforeSubmit))
        {
            var replay = store.BeginSubmission(jobId, idempotencyKey, atUtc);
            if (replay.Job.State == JobState.Submitting)
            {
                var unknown = store.TransitionJob(jobId, JobState.NeedsReviewUnknown,
                    "Restart found a durable submission intent without a persisted OS receipt; automatic resubmission is blocked.", atUtc);
                return new PrintSubmissionResult(PrintSubmissionStatus.NeedsReviewUnknown, unknown, "Submission outcome is unknown.");
            }
            var status = replay.Job.State switch
            {
                JobState.NeedsReviewUnknown => PrintSubmissionStatus.NeedsReviewUnknown,
                JobState.FailedBeforeSubmit => PrintSubmissionStatus.FailedBeforeSubmit,
                _ => PrintSubmissionStatus.Accepted
            };
            return new PrintSubmissionResult(status, replay.Job,
                status == PrintSubmissionStatus.NeedsReviewUnknown ? "Submission outcome is unknown." : null);
        }
        var segment = preparedRun.Segments.SingleOrDefault(value => value.Index == job.SegmentIndex)
            ?? throw new InvalidDataException("The persisted job refers to a missing prepared segment.");
        IReadOnlyList<int> finalPageNumbers;
        if (!segment.FinalPageNumbers.IsDefaultOrEmpty)
        {
            finalPageNumbers = segment.FinalPageNumbers;
        }
        else if (preparedRun.Segments.Length == 1 &&
            segment.ItemIds.SequenceEqual(preparedRun.Items.Select(item => item.ItemId), StringComparer.Ordinal))
        {
            // Schema-v2 runs had no page ownership map. Preserve legacy behavior only when the
            // sole segment is provably the complete ordered batch.
            if (finalArtifact.PageCount > MaximumRasterizedPageCount)
                return new PrintSubmissionResult(PrintSubmissionStatus.Rejected, job,
                    "This final PDF exceeds the bounded print raster page limit.");
            finalPageNumbers = Enumerable.Range(1, finalArtifact.PageCount).ToArray();
        }
        else
        {
            return new PrintSubmissionResult(PrintSubmissionStatus.Rejected, job,
                "This stored multi-segment run has no final-PDF page ownership map. Reprepare it before printing.");
        }
        var queueIdentity = segment.PrintSettings.PrinterId;
        if (string.IsNullOrWhiteSpace(queueIdentity))
            return new PrintSubmissionResult(PrintSubmissionStatus.Rejected, job, "A printer queue must be explicitly selected.");

        PrinterCapabilitySnapshot first;
        try
        {
            first = await backend.GetCapabilitiesAsync(queueIdentity, cancellationToken).ConfigureAwait(false);
            _ = WindowsPrintJobOptions.Map(queueIdentity, first, segment.PrintSettings);
        }
        catch (PrintCapabilityException exception)
        {
            return new PrintSubmissionResult(PrintSubmissionStatus.Rejected, job, exception.Message);
        }

        PrinterCapabilitySnapshot current;
        WindowsPrintJobOptions options;
        try
        {
            // Query again immediately before recording intent; driver/queue churn must not create a spool attempt.
            current = await backend.GetCapabilitiesAsync(queueIdentity, cancellationToken).ConfigureAwait(false);
            if (!SameDevice(first, current))
                throw new PrintCapabilityException("Queue identity or driver capabilities changed before submission.");
            options = WindowsPrintJobOptions.Map(queueIdentity, current, segment.PrintSettings);
        }
        catch (PrintCapabilityException exception)
        {
            return new PrintSubmissionResult(PrintSubmissionStatus.Rejected, job, exception.Message);
        }

        var nonce = _nonceFactory();
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length > 256 || nonce.Any(char.IsControl))
            throw new InvalidOperationException("The run nonce must be nonempty, bounded, and contain no control characters.");

        var intent = store.BeginSubmission(jobId, idempotencyKey, atUtc);
        if (intent.Replayed)
        {
            if (intent.Job.State == JobState.Submitting)
            {
                var unknown = store.TransitionJob(jobId, JobState.NeedsReviewUnknown,
                    "Restart found a durable submission intent without a persisted OS receipt; automatic resubmission is blocked.", atUtc);
                return new PrintSubmissionResult(PrintSubmissionStatus.NeedsReviewUnknown, unknown, "Submission outcome is unknown.");
            }
            var replayStatus = intent.Job.State switch
            {
                JobState.NeedsReviewUnknown => PrintSubmissionStatus.NeedsReviewUnknown,
                JobState.FailedBeforeSubmit => PrintSubmissionStatus.FailedBeforeSubmit,
                _ => PrintSubmissionStatus.Accepted
            };
            return new PrintSubmissionResult(replayStatus, intent.Job,
                replayStatus == PrintSubmissionStatus.NeedsReviewUnknown ? "Submission outcome is unknown." : null);
        }

        try
        {
            pdfContent.Position = 0;
            var receipt = await backend.SubmitAsync(
                new WindowsPrintSubmission(options, segment, nonce, atUtc.ToUniversalTime(), finalArtifact, pdfContent, finalPageNumbers), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(receipt.QueueIdentity, queueIdentity, StringComparison.Ordinal) ||
                !string.Equals(receipt.RunNonce, nonce, StringComparison.Ordinal) ||
                (receipt.DriverName is not null && !string.Equals(receipt.DriverName, current.DriverName, StringComparison.Ordinal)) ||
                (receipt.PortName is not null && !string.Equals(receipt.PortName, current.PortName, StringComparison.Ordinal)) ||
                (receipt.CapabilityFingerprint is not null && !string.Equals(receipt.CapabilityFingerprint, current.Fingerprint, StringComparison.Ordinal)))
                throw new InvalidDataException("The backend returned a receipt for a different queue or run nonce.");
            var accepted = store.RecordOsReceipt(jobId, idempotencyKey, receipt, atUtc).Job;
            return new PrintSubmissionResult(PrintSubmissionStatus.Accepted, accepted, null);
        }
        catch (PrintSubmissionException exception) when (!exception.MayHaveReachedOs)
        {
            var failed = store.TransitionJob(jobId, JobState.FailedBeforeSubmit,
                "The backend confirmed no OS submission side effect occurred.", atUtc);
            return new PrintSubmissionResult(PrintSubmissionStatus.FailedBeforeSubmit, failed, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var unknown = store.TransitionJob(jobId, JobState.NeedsReviewUnknown,
                "The OS submission call may have taken effect but no matching receipt was persisted.", atUtc);
            return new PrintSubmissionResult(PrintSubmissionStatus.NeedsReviewUnknown, unknown,
                $"Submission outcome is unknown ({exception.GetType().Name}).");
        }
        catch (OperationCanceledException)
        {
            var unknown = store.TransitionJob(jobId, JobState.NeedsReviewUnknown,
                "Submission was interrupted after durable intent; the OS outcome cannot be inferred.", atUtc);
            return new PrintSubmissionResult(PrintSubmissionStatus.NeedsReviewUnknown, unknown, "Submission outcome is unknown.");
        }
    }

    public async Task<StoredJob> RefreshStatusAsync(string jobId, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        var job = store.GetJob(jobId) ?? throw new InvalidOperationException("The requested print job does not exist.");
        if (job.State is JobState.FailedBeforeSubmit or JobState.OsReportsComplete or JobState.CancelObserved or JobState.UserConfirmedPaperOutput)
            return job;
        if (job.OsReceipt is null)
        {
            if (job.State == JobState.Submitting)
                return store.TransitionJob(jobId, JobState.NeedsReviewUnknown, "Restart found an intent without an OS receipt.", atUtc);
            return job;
        }

        var status = await backend.GetStatusAsync(job.OsReceipt, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            job = store.GetJob(jobId) ?? throw new InvalidOperationException("The requested print job does not exist.");
            if (job.State is JobState.FailedBeforeSubmit or JobState.OsReportsComplete or JobState.CancelObserved or JobState.UserConfirmedPaperOutput)
                return job;
            var next = status switch
            {
                PrintBackendJobStatus.Accepted => job.State,
                PrintBackendJobStatus.Processing => job.State == JobState.CancelRequested ? JobState.CancelRequested : JobState.OsProcessing,
                PrintBackendJobStatus.ReportedComplete => JobState.OsReportsComplete,
                PrintBackendJobStatus.Failed => JobState.NeedsReviewUnknown,
                PrintBackendJobStatus.Cancelled => job.State == JobState.CancelRequested
                    ? JobState.CancelObserved
                    : JobState.NeedsReviewUnknown,
                _ => JobState.NeedsReviewUnknown
            };
            if (next == job.State) return job;
            try
            {
                var updated = store.TransitionJob(jobId, next, $"OS status reported {status}.", atUtc, job.State);
                if (updated.State == next) return updated;
                // A concurrent request changed the durable state before this status was applied.
            }
            catch (InvalidOperationException) when (store.GetJob(jobId) is { } changed && changed.State != job.State)
            {
                // Re-evaluate the same response against the state that won the concurrent update.
            }
        }
    }

    public async Task<StoredJob> CancelAsync(string jobId, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        var job = store.GetJob(jobId) ?? throw new InvalidOperationException("The requested print job does not exist.");
        if (job.OsReceipt is null || job.State is not (JobState.AcceptedByOs or JobState.OsProcessing or JobState.CancelRequested))
            throw new InvalidOperationException("Only an accepted job with its exact OS receipt can be cancelled.");
        StoredJob requested;
        if (job.State == JobState.CancelRequested)
        {
            requested = job;
        }
        else
        {
            try
            {
                requested = store.TransitionJob(jobId, JobState.CancelRequested,
                    "Cancellation requested for the owned queue/job receipt.", atUtc);
            }
            catch (InvalidOperationException)
            {
                var current = store.GetJob(jobId)!;
                if (current.State == JobState.CancelRequested) requested = current;
                else if (current.State is JobState.OsReportsComplete or JobState.CancelObserved or JobState.UserConfirmedPaperOutput)
                    return current;
                else throw;
            }
        }
        await backend.CancelAsync(requested.OsReceipt!, cancellationToken).ConfigureAwait(false);
        // A successful CancelAsync only means the spooler accepted the command. Cancellation
        // becomes durable only after RefreshStatusAsync observes the terminal Cancelled status.
        return store.GetJob(jobId)!;
    }

    private static bool SameDevice(PrinterCapabilitySnapshot left, PrinterCapabilitySnapshot right) =>
        string.Equals(left.QueueIdentity, right.QueueIdentity, StringComparison.Ordinal) &&
        string.Equals(left.DriverName, right.DriverName, StringComparison.Ordinal) &&
        string.Equals(left.PortName, right.PortName, StringComparison.Ordinal) &&
        string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal);
}
