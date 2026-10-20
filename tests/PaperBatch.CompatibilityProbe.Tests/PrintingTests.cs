using PaperBatch.Core.Domain;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Printing;

internal static class PrintingTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal static void NativeQueueEnumerationIsReadOnlyAndBounded()
    {
        if (!OperatingSystem.IsWindows()) return;
        var queues = WindowsPrintCapabilityEnumerator.EnumerateLocalQueues();
        Assert.True(queues.All(queue => !string.IsNullOrWhiteSpace(queue.QueueIdentity) &&
            !string.IsNullOrWhiteSpace(queue.DisplayName) && !string.IsNullOrWhiteSpace(queue.DriverName) &&
            !string.IsNullOrWhiteSpace(queue.PortName) && queue.Options.ContainsKey(PrintOption.Media)));
    }

    internal static void CapabilitiesRejectUnsupportedAndMapExactOptions()
    {
        var capabilities = Capabilities();
        var settings = Settings();
        var mapped = WindowsPrintJobOptions.Map("queue-identity", capabilities, settings);
        Assert.Equal("Letter", mapped.Media);
        Assert.Equal("Tray1", mapped.Tray);
        Assert.Equal("Color", mapped.Color);
        Assert.Equal("LongEdge", mapped.Duplex);

        Assert.Throws<PrintCapabilityException>(() => WindowsPrintJobOptions.Map(
            "queue-identity", capabilities with
            {
                Options = capabilities.Options.SetItem(PrintOption.Media,
                    new PrintOptionCapability(CapabilityState.Unsupported, ["A4"], null))
            }, settings));
        Assert.Throws<PrintCapabilityException>(() => WindowsPrintJobOptions.Map(
            "queue-identity", capabilities with
            {
                Options = capabilities.Options.SetItem(PrintOption.Tray,
                    new PrintOptionCapability(CapabilityState.Unknown, [], "driver did not report bins"))
            }, settings));
        Assert.Throws<PrintCapabilityException>(() => WindowsPrintJobOptions.Map(
            "queue-identity", capabilities with
            {
                Options = capabilities.Options.SetItem(PrintOption.Color,
                    new PrintOptionCapability(CapabilityState.Unavailable, [], "access denied"))
            }, settings));
    }

    internal static void CapabilityMutationStopsBeforeSubmission()
    {
        using var fixture = new Fixture();
        var backend = new FakePrintBackend(Capabilities()) { CapabilitiesOnSecondRead = ChangedCapabilities() };
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1");
        var result = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime)
            .GetAwaiter().GetResult();

        Assert.Equal(PrintSubmissionStatus.Rejected, result.Status);
        Assert.Equal(0, backend.SubmissionCount);
        Assert.Equal(JobState.Ready, fixture.Store.GetJob(fixture.Job.JobId)!.State);
    }

    internal static void SameRunIdWithMutatedPreparedRunIsRejectedBeforeCapabilityOrSubmission()
    {
        using var fixture = new Fixture();
        var mutatedSegment = fixture.PreparedRun.Segments[0] with { SegmentId = "different-segment" };
        var mutated = PreparedRun.Create(fixture.PreparedRun.RunId, fixture.PreparedRun.CreatedAtUtc,
            fixture.PreparedRun.SnapshotFingerprint, fixture.PreparedRun.Items, [mutatedSegment]);
        var backend = new FakePrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "mutated-run-nonce");

        Assert.Throws<InvalidOperationException>(() => coordinator.SubmitAsync(
            fixture.Job.JobId, mutated, "mutated-run-key", FixedTime).GetAwaiter().GetResult());
        Assert.Equal(0, backend.CapabilityReads);
        Assert.Equal(0, backend.SubmissionCount);
        Assert.Equal(JobState.Ready, fixture.Store.GetJob(fixture.Job.JobId)!.State);
    }

    internal static void AmbiguousSubmissionIsNotRetriedAfterRestart()
    {
        using var fixture = new Fixture();
        var backend = new FakePrintBackend(Capabilities()) { ThrowAfterSubmitSideEffect = true };
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1");
        var first = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime)
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, first.Status);
        Assert.Equal(1, backend.SubmissionCount);

        var restarted = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-2");
        var replay = restarted.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime.AddMinutes(1))
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, replay.Status);
        Assert.Equal(1, backend.SubmissionCount);
        Assert.Equal(JobState.NeedsReviewUnknown, fixture.Store.GetJob(fixture.Job.JobId)!.State);

        using var postEffectFixture = new Fixture();
        var postEffectBackend = new FakePrintBackend(Capabilities()) { ThrowCapabilityAfterSubmit = true };
        var postEffect = new WindowsPrintCoordinator(postEffectFixture.Store, postEffectBackend, static () => "post-effect")
            .SubmitAsync(postEffectFixture.Job.JobId, postEffectFixture.PreparedRun, "post-effect-key", FixedTime)
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, postEffect.Status);
        Assert.Equal(JobState.NeedsReviewUnknown, postEffectFixture.Store.GetJob(postEffectFixture.Job.JobId)!.State);

        using var crashFixture = new Fixture();
        crashFixture.Store.BeginSubmission(crashFixture.Job.JobId, "crash-before-os", FixedTime);
        var untouchedBackend = new FakePrintBackend(Capabilities());
        var afterCrash = new WindowsPrintCoordinator(crashFixture.Store, untouchedBackend, static () => "unused-nonce");
        var reconciled = afterCrash.SubmitAsync(crashFixture.Job.JobId, crashFixture.PreparedRun, "crash-before-os", FixedTime.AddMinutes(1))
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, reconciled.Status);
        Assert.Equal(0, untouchedBackend.SubmissionCount);
        Assert.Equal(0, untouchedBackend.CapabilityReads);
        Assert.Equal(JobState.NeedsReviewUnknown, crashFixture.Store.GetJob(crashFixture.Job.JobId)!.State);

        using var knownFailureFixture = new Fixture();
        var knownFailureBackend = new FakePrintBackend(Capabilities()) { ThrowBeforeSubmit = true };
        var knownFailure = new WindowsPrintCoordinator(knownFailureFixture.Store, knownFailureBackend, static () => "known-failure")
            .SubmitAsync(knownFailureFixture.Job.JobId, knownFailureFixture.PreparedRun, "known-failure-key", FixedTime)
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.FailedBeforeSubmit, knownFailure.Status);
        Assert.Equal(JobState.FailedBeforeSubmit, knownFailureFixture.Store.GetJob(knownFailureFixture.Job.JobId)!.State);
    }

    internal static void StatusAndCancellationAreScopedToOwnedReceipt()
    {
        using var fixture = new Fixture();
        var backend = new FakePrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1");
        var submitted = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime)
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.Accepted, submitted.Status);
        var persistedReceipt = fixture.Store.GetJob(fixture.Job.JobId)!.OsReceipt!;
        Assert.Equal("Test Driver", persistedReceipt.DriverName);
        Assert.Equal("PORT1", persistedReceipt.PortName);
        Assert.Equal("capability-v1", persistedReceipt.CapabilityFingerprint);

        backend.LastStatus = PrintBackendJobStatus.Processing;
        Assert.Equal(JobState.OsProcessing, coordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(1))
            .GetAwaiter().GetResult().State);
        coordinator.CancelAsync(fixture.Job.JobId, FixedTime.AddMinutes(2)).GetAwaiter().GetResult();
        Assert.Equal("queue-identity", backend.LastQueueIdentity);
        Assert.Equal("job-1", backend.LastJobId);
        Assert.Equal("run-nonce-1", backend.LastRunNonce);
        Assert.Equal(1, backend.CancelCount);

        backend.LastStatus = PrintBackendJobStatus.ReportedComplete;
        var afterCancel = coordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(3)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelObserved, afterCancel.State);
    }

    internal static void OsCompletionDoesNotConfirmPaperOutput()
    {
        using var fixture = new Fixture();
        var backend = new FakePrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1");
        coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime).GetAwaiter().GetResult();
        backend.LastStatus = PrintBackendJobStatus.ReportedComplete;

        var result = coordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(JobState.OsReportsComplete, result.State);
        Assert.True(result.State != JobState.UserConfirmedPaperOutput);
    }

    internal static void ConcurrentJobsKeepDistinctReceipts()
    {
        using var firstFixture = new Fixture();
        using var secondFixture = new Fixture();
        var backend = new FakePrintBackend(Capabilities());
        var first = new WindowsPrintCoordinator(firstFixture.Store, backend, static () => "nonce-first");
        var second = new WindowsPrintCoordinator(secondFixture.Store, backend, static () => "nonce-second");
        Task.WaitAll(
            first.SubmitAsync(firstFixture.Job.JobId, firstFixture.PreparedRun, "key-first", FixedTime),
            second.SubmitAsync(secondFixture.Job.JobId, secondFixture.PreparedRun, "key-second", FixedTime));

        Assert.Equal(2, backend.SubmissionCount);
        Assert.True(backend.Receipts.Select(receipt => receipt.RunNonce).Order(StringComparer.Ordinal)
            .SequenceEqual(["nonce-first", "nonce-second"], StringComparer.Ordinal));
        Assert.Equal("job-1", backend.Receipts.First().OsJobId);
        first.CancelAsync(firstFixture.Job.JobId, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal("nonce-first", backend.LastRunNonce);
        Assert.Equal("job-1", backend.LastJobId);
        Assert.Equal(JobState.CancelObserved, firstFixture.Store.GetJob(firstFixture.Job.JobId)!.State);
        Assert.Equal(JobState.AcceptedByOs, secondFixture.Store.GetJob(secondFixture.Job.JobId)!.State);
    }

    internal static void CancellationRacePreservesOsStatus()
    {
        using var fixture = new Fixture();
        var backend = new FakePrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1");
        coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime).GetAwaiter().GetResult();
        backend.OnCancel = () => fixture.Store.TransitionJob(fixture.Job.JobId, JobState.OsReportsComplete,
            "OS completion won the cancel race.", FixedTime.AddMinutes(2));

        var result = coordinator.CancelAsync(fixture.Job.JobId, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(JobState.OsReportsComplete, result.State);
        Assert.Equal(JobState.OsReportsComplete, fixture.Store.GetJob(fixture.Job.JobId)!.State);
    }

    internal static void CancellationRetryAfterRestartUsesReceipt()
    {
        using var fixture = new Fixture();
        var backend = new FakePrintBackend(Capabilities()) { CancelReturnsFalse = true };
        var firstCoordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "cancel-retry-nonce");
        firstCoordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "cancel-retry-submit", FixedTime).GetAwaiter().GetResult();
        var first = firstCoordinator.CancelAsync(fixture.Job.JobId, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelRequested, first.State);

        backend.CancelReturnsFalse = false;
        var restartedCoordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "unused-after-restart");
        var retried = restartedCoordinator.CancelAsync(fixture.Job.JobId, FixedTime.AddMinutes(2)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelObserved, retried.State);
        Assert.Equal(2, backend.CancelCount);
        Assert.Equal("queue-identity", backend.LastQueueIdentity);
        Assert.Equal("job-1", backend.LastJobId);
        Assert.Equal("cancel-retry-nonce", backend.LastRunNonce);
    }

    private static PrinterCapabilitySnapshot Capabilities() => new(
        "queue-identity", "Test Queue", "Test Driver", "PORT1", "capability-v1",
        new Dictionary<PrintOption, PrintOptionCapability>
        {
            [PrintOption.Media] = new(CapabilityState.Supported, ["Letter", "A4"], null),
            [PrintOption.Tray] = new(CapabilityState.Supported, ["Tray1"], null),
            [PrintOption.Color] = new(CapabilityState.Supported, ["Color", "Monochrome"], null),
            [PrintOption.Duplex] = new(CapabilityState.Supported, ["Simplex", "LongEdge", "ShortEdge"], null)
        });

    private static PrinterCapabilitySnapshot ChangedCapabilities() => Capabilities() with
    {
        Fingerprint = "capability-v2",
        Options = Capabilities().Options.SetItem(PrintOption.Media,
            new PrintOptionCapability(CapabilityState.Supported, ["A4"], null))
    };

    private static PrintSettings Settings() => new(1, ColorMode.Color, DuplexMode.LongEdge,
        "Letter", Orientation.Portrait, ScalingMode.ShrinkToFit, "queue-identity", "Tray1");

    private sealed class FakePrintBackend(PrinterCapabilitySnapshot capabilities) : IWindowsPrintBackend
    {
        private int _capabilityReads;
        private int _submissionCount;
        private int _cancelCount;
        public PrinterCapabilitySnapshot? CapabilitiesOnSecondRead { get; init; }
        public bool ThrowAfterSubmitSideEffect { get; init; }
        public bool ThrowBeforeSubmit { get; init; }
        public bool ThrowCapabilityAfterSubmit { get; init; }
        public bool CancelReturnsFalse { get; set; }
        public int SubmissionCount => Volatile.Read(ref _submissionCount);
        public int CancelCount => Volatile.Read(ref _cancelCount);
        public int CapabilityReads => Volatile.Read(ref _capabilityReads);
        public string? LastQueueIdentity { get; private set; }
        public string? LastJobId { get; private set; }
        public string? LastRunNonce { get; private set; }
        public PrintBackendJobStatus LastStatus { get; set; } = PrintBackendJobStatus.Accepted;
        public Action? OnCancel { get; set; }
        public System.Collections.Concurrent.ConcurrentBag<OsJobReceipt> Receipts { get; } = [];

        public Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default) =>
            Task.FromResult(++_capabilityReads >= 2 ? CapabilitiesOnSecondRead ?? capabilities : capabilities);

        public Task<OsJobReceipt> SubmitAsync(WindowsPrintSubmission submission, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _submissionCount);
            LastQueueIdentity = submission.Options.QueueIdentity;
            LastRunNonce = submission.RunNonce;
            if (ThrowBeforeSubmit) throw new PrintSubmissionException("native backend rejected before StartDoc", mayHaveReachedOs: false);
            if (ThrowCapabilityAfterSubmit) throw new PrintCapabilityException("capabilities changed inside submit");
            if (ThrowAfterSubmitSideEffect) throw new IOException("receipt channel interrupted after acceptance");
            var receipt = new OsJobReceipt(LastQueueIdentity, "job-1", submission.RunNonce, FixedTime,
                "Test Driver", "PORT1", submission.Options.CapabilityFingerprint);
            Receipts.Add(receipt);
            return Task.FromResult(receipt);
        }

        public Task<PrintBackendJobStatus> GetStatusAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default)
        {
            LastQueueIdentity = receipt.QueueIdentity;
            LastJobId = receipt.OsJobId;
            LastRunNonce = receipt.RunNonce;
            return Task.FromResult(LastStatus);
        }

        public Task<bool> CancelAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _cancelCount);
            LastQueueIdentity = receipt.QueueIdentity;
            LastJobId = receipt.OsJobId;
            LastRunNonce = receipt.RunNonce;
            OnCancel?.Invoke();
            return Task.FromResult(!CancelReturnsFalse);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "paperbatch-print-test-" + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Directory.CreateDirectory(_temporary);
            Store = new SqliteRunStore(Path.Combine(_temporary, "runs.db"));
            Store.Initialize();
            var item = new PreparedItem("item-1", new SourceSnapshot("source", Hash, 1, FixedTime),
                new RendererResult("renderer", "1", "artifact", Hash, 1, 1, new RendererCapabilities(1, ["application/pdf"])),
                [new PageMapping(1, 1)], Settings());
            var segment = new CompatibleSegment("segment-1", 0, [item.ItemId], "compat", Settings());
            PreparedRun = PaperBatch.Core.Domain.PreparedRun.Create("run-1", FixedTime, Hash, [item], [segment]);
            Job = Store.CreateRun(PreparedRun, "run-key", new RetentionMetadata(FixedTime.AddDays(1), true)).Jobs[0];
        }
        public SqliteRunStore Store { get; }
        public PreparedRun PreparedRun { get; }
        public StoredJob Job { get; }
        public void Dispose() => Directory.Delete(_temporary, recursive: true);
    }
}
