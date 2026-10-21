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

    internal static void NativeGdiCapabilityBoundaryIsReadOnly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var queues = WindowsPrintCapabilityEnumerator.EnumerateLocalQueues();
        if (queues.IsEmpty) return;
        var adapter = new WindowsGdiNativeAdapter();
        foreach (var queue in queues)
        {
            var current = adapter.GetCapabilitiesAsync(queue.QueueIdentity).GetAwaiter().GetResult();
            Assert.Equal(queue.QueueIdentity, current.QueueIdentity);
            Assert.True(current.NativeTokens.ContainsKey(PrintOption.Media));
            Assert.True(current.NativeTokens.ContainsKey(PrintOption.Tray));
            foreach (var option in new[] { PrintOption.Media, PrintOption.Tray })
            {
                if (current.Options[option].State != CapabilityState.Supported) continue;
                Assert.Equal(current.Options[option].Values.Length, current.NativeTokens[option].Count);
                Assert.True(current.Options[option].Values.All(current.NativeTokens[option].ContainsKey));
            }
        }
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
        Assert.Throws<PrintCapabilityException>(() => WindowsPrintJobOptions.Map(
            "queue-identity", capabilities with
            {
                Options = capabilities.Options.SetItem(PrintOption.Copies,
                    new PrintOptionCapability(CapabilityState.Unknown, [], "driver did not report a copy limit"))
            }, settings));
        Assert.Throws<PrintCapabilityException>(() => WindowsPrintJobOptions.Map("queue-identity", capabilities,
            settings with { Copies = 1000 }));
    }

    internal static void CapabilityMutationStopsBeforeSubmission()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities()) { CapabilitiesOnSecondRead = ChangedCapabilities() };
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1", fixture.Artifacts);
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
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "mutated-run-nonce", fixture.Artifacts);

        Assert.Throws<InvalidOperationException>(() => coordinator.SubmitAsync(
            fixture.Job.JobId, mutated, "mutated-run-key", FixedTime).GetAwaiter().GetResult());
        Assert.Equal(0, backend.CapabilityReads);
        Assert.Equal(0, backend.SubmissionCount);
        Assert.Equal(JobState.Ready, fixture.Store.GetJob(fixture.Job.JobId)!.State);

        var fakeDigest = new string('b', 64);
        var mutatedArtifact = fixture.PreparedRun.FinalArtifact! with
        {
            ArtifactId = "artifact_" + fakeDigest,
            Sha256 = fakeDigest
        };
        var artifactMutated = PreparedRun.Create(fixture.PreparedRun.RunId, fixture.PreparedRun.CreatedAtUtc,
            fixture.PreparedRun.SnapshotFingerprint, fixture.PreparedRun.Items, fixture.PreparedRun.Segments, mutatedArtifact);
        Assert.Throws<InvalidOperationException>(() => coordinator.SubmitAsync(
            fixture.Job.JobId, artifactMutated, "mutated-artifact-key", FixedTime).GetAwaiter().GetResult());
        Assert.Equal(0, backend.CapabilityReads);
        Assert.Equal(0, backend.SubmissionCount);
    }

    internal static void AmbiguousSubmissionIsNotRetriedAfterRestart()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities()) { ThrowAfterSubmitSideEffect = true };
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1", fixture.Artifacts);
        var first = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime)
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, first.Status);
        Assert.Equal(1, backend.SubmissionCount);

        var restarted = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-2", fixture.Artifacts);
        var replay = restarted.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime.AddMinutes(1))
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, replay.Status);
        Assert.Equal(1, backend.SubmissionCount);
        Assert.Equal(JobState.NeedsReviewUnknown, fixture.Store.GetJob(fixture.Job.JobId)!.State);

        using var postEffectFixture = new Fixture();
        var postEffectBackend = new TestOnlySimulatedPrintBackend(Capabilities()) { ThrowCapabilityAfterSubmit = true };
        var postEffect = new WindowsPrintCoordinator(postEffectFixture.Store, postEffectBackend, static () => "post-effect", postEffectFixture.Artifacts)
            .SubmitAsync(postEffectFixture.Job.JobId, postEffectFixture.PreparedRun, "post-effect-key", FixedTime)
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, postEffect.Status);
        Assert.Equal(JobState.NeedsReviewUnknown, postEffectFixture.Store.GetJob(postEffectFixture.Job.JobId)!.State);

        using var crashFixture = new Fixture();
        crashFixture.Store.BeginSubmission(crashFixture.Job.JobId, "crash-before-os", FixedTime);
        var untouchedBackend = new TestOnlySimulatedPrintBackend(Capabilities());
        var afterCrash = new WindowsPrintCoordinator(crashFixture.Store, untouchedBackend, static () => "unused-nonce", crashFixture.Artifacts);
        var reconciled = afterCrash.SubmitAsync(crashFixture.Job.JobId, crashFixture.PreparedRun, "crash-before-os", FixedTime.AddMinutes(1))
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, reconciled.Status);
        Assert.Equal(0, untouchedBackend.SubmissionCount);
        Assert.Equal(0, untouchedBackend.CapabilityReads);
        Assert.Equal(JobState.NeedsReviewUnknown, crashFixture.Store.GetJob(crashFixture.Job.JobId)!.State);

        using var knownFailureFixture = new Fixture();
        var knownFailureBackend = new TestOnlySimulatedPrintBackend(Capabilities()) { ThrowBeforeSubmit = true };
        var knownFailure = new WindowsPrintCoordinator(knownFailureFixture.Store, knownFailureBackend, static () => "known-failure", knownFailureFixture.Artifacts)
            .SubmitAsync(knownFailureFixture.Job.JobId, knownFailureFixture.PreparedRun, "known-failure-key", FixedTime)
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.FailedBeforeSubmit, knownFailure.Status);
        Assert.Equal(JobState.FailedBeforeSubmit, knownFailureFixture.Store.GetJob(knownFailureFixture.Job.JobId)!.State);
    }

    internal static void StatusAndCancellationAreScopedToOwnedReceipt()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1", fixture.Artifacts);
        var submitted = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime)
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.Accepted, submitted.Status);
        Assert.Equal(fixture.PreparedRun.FinalArtifact!.ArtifactId, backend.LastArtifactId);
        Assert.True(RendererTests.OnePagePdf().SequenceEqual(backend.LastPdfBytes!));
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

    internal static void SubmissionRejectsLegacyRunWithoutFinalArtifact()
    {
        using var fixture = new Fixture();
        var legacy = PreparedRun.Create(fixture.PreparedRun.RunId, fixture.PreparedRun.CreatedAtUtc,
            fixture.PreparedRun.SnapshotFingerprint, fixture.PreparedRun.Items, fixture.PreparedRun.Segments);
        // Persist the legacy run and create a matching job in a fresh store so stale-run checking passes.
        using var legacyFixture = new Fixture(legacy);
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(legacyFixture.Store, backend, static () => "legacy", legacyFixture.Artifacts);

        var result = coordinator.SubmitAsync(legacyFixture.Job.JobId, legacyFixture.PreparedRun, "legacy-submit", FixedTime)
            .GetAwaiter().GetResult();

        Assert.Equal(PrintSubmissionStatus.Rejected, result.Status);
        Assert.Equal(0, backend.CapabilityReads);
        Assert.Equal(0, backend.SubmissionCount);
    }

    internal static void SubmissionPassesTheExactPersistedFinalPdf()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "exact-artifact", fixture.Artifacts);

        var result = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "exact-artifact-key", FixedTime)
            .GetAwaiter().GetResult();

        Assert.Equal(PrintSubmissionStatus.Accepted, result.Status);
        Assert.Equal(fixture.PreparedRun.FinalArtifact!.ArtifactId, backend.LastArtifactId);
        Assert.True(RendererTests.OnePagePdf().SequenceEqual(backend.LastPdfBytes!));
    }

    internal static void MissingExplicitQueueIsRejectedBeforeCapabilityLookup()
    {
        using var baseline = new Fixture();
        var segment = baseline.PreparedRun.Segments[0] with { PrintSettings = Settings() with { PrinterId = null } };
        var item = baseline.PreparedRun.Items[0] with { PrintSettings = Settings() with { PrinterId = null } };
        var run = PreparedRun.Create("run-no-queue", FixedTime, Hash, [item], [segment], baseline.PreparedRun.FinalArtifact);
        using var fixture = new Fixture(run);
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "unused", fixture.Artifacts);

        var result = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "no-queue-key", FixedTime)
            .GetAwaiter().GetResult();

        Assert.Equal(PrintSubmissionStatus.Rejected, result.Status);
        Assert.Equal(0, backend.CapabilityReads);
        Assert.Equal(0, backend.SubmissionCount);
        Assert.Equal(JobState.Ready, fixture.Store.GetJob(fixture.Job.JobId)!.State);
    }

    internal static void RunSubmissionIsSequentialAndDoesNotResumeUnknownSegments()
    {
        using var seed = new Fixture();
        var firstItem = seed.PreparedRun.Items[0];
        var secondItem = firstItem with { ItemId = "item-2" };
        var firstSegment = new CompatibleSegment("segment-1", 0, [firstItem.ItemId], "compat-1", Settings());
        var secondSegment = new CompatibleSegment("segment-2", 1, [secondItem.ItemId], "compat-2", Settings());
        var run = PreparedRun.Create("run-multi", FixedTime, Hash, [firstItem, secondItem],
            [firstSegment, secondSegment], seed.PreparedRun.FinalArtifact);

        using var fixture = new Fixture(run);
        var uncertainBackend = new TestOnlySimulatedPrintBackend(Capabilities()) { ThrowAfterSubmitSideEffect = true };
        var coordinator = new WindowsPrintCoordinator(fixture.Store, uncertainBackend, static () => "multi-nonce", fixture.Artifacts);
        var firstAttempt = coordinator.SubmitRunAsync(run, "multi-key", FixedTime).GetAwaiter().GetResult();
        Assert.Equal(1, firstAttempt.Count);
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, firstAttempt[0].Status);
        Assert.Equal(1, uncertainBackend.SubmissionCount);
        Assert.Equal(JobState.Ready, fixture.Store.GetJobs(run.RunId)[1].State);
        Assert.Equal(0, coordinator.SubmitRunAsync(run, "multi-key", FixedTime.AddMinutes(1)).GetAwaiter().GetResult().Count);
        Assert.Equal(1, uncertainBackend.SubmissionCount);
    }

    internal static void OsCompletionDoesNotConfirmPaperOutput()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1", fixture.Artifacts);
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
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var first = new WindowsPrintCoordinator(firstFixture.Store, backend, static () => "nonce-first", firstFixture.Artifacts);
        var second = new WindowsPrintCoordinator(secondFixture.Store, backend, static () => "nonce-second", secondFixture.Artifacts);
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
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1", fixture.Artifacts);
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
        var backend = new TestOnlySimulatedPrintBackend(Capabilities()) { CancelReturnsFalse = true };
        var firstCoordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "cancel-retry-nonce", fixture.Artifacts);
        firstCoordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "cancel-retry-submit", FixedTime).GetAwaiter().GetResult();
        var first = firstCoordinator.CancelAsync(fixture.Job.JobId, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelRequested, first.State);

        backend.CancelReturnsFalse = false;
        var restartedCoordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "unused-after-restart", fixture.Artifacts);
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
            [PrintOption.Duplex] = new(CapabilityState.Supported, ["Simplex", "LongEdge", "ShortEdge"], null),
            [PrintOption.Copies] = new(CapabilityState.Supported, ["1..999"], null)
        });

    private static PrinterCapabilitySnapshot ChangedCapabilities() => Capabilities() with
    {
        Fingerprint = "capability-v2",
        Options = Capabilities().Options.SetItem(PrintOption.Media,
            new PrintOptionCapability(CapabilityState.Supported, ["A4"], null))
    };

    private static PrintSettings Settings() => new(1, ColorMode.Color, DuplexMode.LongEdge,
        "Letter", Orientation.Portrait, ScalingMode.ShrinkToFit, "queue-identity", "Tray1");

    private sealed class TestOnlySimulatedPrintBackend(PrinterCapabilitySnapshot capabilities) : IWindowsPrintBackend
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
        public string? LastArtifactId { get; private set; }
        public byte[]? LastPdfBytes { get; private set; }
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
            LastArtifactId = submission.Artifact.ArtifactId;
            using (var content = new MemoryStream()) { submission.PdfContent.CopyTo(content); LastPdfBytes = content.ToArray(); }
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
        public Fixture(PreparedRun? runOverride = null)
        {
            Directory.CreateDirectory(_temporary);
            Store = new SqliteRunStore(Path.Combine(_temporary, "runs.db"));
            Store.Initialize();
            Artifacts = new PaperBatch.Core.Rendering.RenderArtifactStore(Path.Combine(_temporary, "artifacts"));
            var pdf = RendererTests.OnePagePdf();
            var storedArtifact = Artifacts.PersistPdfAsync(new MemoryStream(pdf), "test-renderer", "1", new Dictionary<string, string>()).GetAwaiter().GetResult();
            var finalArtifact = new FinalPdfArtifact(storedArtifact.Artifact.ArtifactId, storedArtifact.Artifact.Sha256, storedArtifact.Artifact.LengthBytes, 1);
            var item = new PreparedItem("item-1", new SourceSnapshot("source", Hash, 1, FixedTime),
                new RendererResult("renderer", "1", "artifact", Hash, 1, 1, new RendererCapabilities(1, ["application/pdf"])),
                [new PageMapping(1, 1)], Settings());
            var segment = new CompatibleSegment("segment-1", 0, [item.ItemId], "compat", Settings());
            PreparedRun = runOverride ?? PaperBatch.Core.Domain.PreparedRun.Create("run-1", FixedTime, Hash, [item], [segment], finalArtifact);
            Job = Store.CreateRun(PreparedRun, "run-key", new RetentionMetadata(FixedTime.AddDays(1), true)).Jobs[0];
        }
        public SqliteRunStore Store { get; }
        public PaperBatch.Core.Rendering.RenderArtifactStore Artifacts { get; }
        public PreparedRun PreparedRun { get; }
        public StoredJob Job { get; }
        public void Dispose() => Directory.Delete(_temporary, recursive: true);
    }
}
