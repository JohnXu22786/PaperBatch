using PaperBatch.Core.Domain;
using PaperBatch.Core.Ipc;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Preparation;
using PaperBatch.Core.Printing;
using PaperBatch.Core.Rendering;
using System.ComponentModel;
using System.Text.Json;

internal static class PrintingTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PartialFailureEvidenceLimit = "The partial case checks segment page maps [1, 2] and [3, 4] and rasterized pages [1, 2] and [3, 4], alongside segment/job ordering, ambiguous state and no replay. This verifies per-segment selection through the fake orchestration path only; it does not validate a native queue or physical output.";
    private const string PageSelectionEvidenceLimit = "Per-segment page selection is verified in the partial-failure scenario by matching its segment page maps and rasterized pages [1, 2] and [3, 4]. This is fake orchestration evidence only, not native queue or physical-output validation.";

    internal static void PartialFailureReportAccuratelyDescribesPageSelection()
    {
        Assert.Contains("segment page maps [1, 2] and [3, 4]", PartialFailureEvidenceLimit);
        Assert.Contains("rasterized pages [1, 2] and [3, 4]", PartialFailureEvidenceLimit);
        Assert.False(PartialFailureEvidenceLimit.Contains("does not verify per-segment page selection", StringComparison.Ordinal));
        Assert.False(PageSelectionEvidenceLimit.Contains("is not verified", StringComparison.Ordinal));
        Assert.Contains("fake orchestration", PartialFailureEvidenceLimit);
        Assert.Contains("fake orchestration", PageSelectionEvidenceLimit);
    }

    internal static void NativeQueueEnumerationIsReadOnlyAndBounded()
    {
        if (!OperatingSystem.IsWindows()) return;
        var queues = WindowsPrintCapabilityEnumerator.EnumerateLocalQueues();
        Assert.True(queues.All(queue => !string.IsNullOrWhiteSpace(queue.QueueIdentity) &&
            !string.IsNullOrWhiteSpace(queue.DisplayName) && !string.IsNullOrWhiteSpace(queue.DriverName) &&
            !string.IsNullOrWhiteSpace(queue.PortName) && queue.Options.ContainsKey(PrintOption.Media)));
    }

    internal static void InitialEnumPrintersFailureIsNotTreatedAsEmpty()
    {
        Win32Exception? denied = null;
        try
        {
            WindowsPrintCapabilityEnumerator.GetInitialEnumerationBufferSize(false, 5, 0);
        }
        catch (Win32Exception exception)
        {
            denied = exception;
        }
        Assert.True(denied is not null, "A failed initial enumeration with no buffer must surface its Win32 error.");
        Assert.Equal(5, denied!.NativeErrorCode);
        Assert.Equal(0u, WindowsPrintCapabilityEnumerator.GetInitialEnumerationBufferSize(true, 0, 0));
        Assert.Equal(128u, WindowsPrintCapabilityEnumerator.GetInitialEnumerationBufferSize(false, 122, 128));
        Assert.Throws<Win32Exception>(() =>
            WindowsPrintCapabilityEnumerator.GetInitialEnumerationBufferSize(false, 122, 0));
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
        var retryBackend = new TestOnlySimulatedPrintBackend(Capabilities());
        var retry = new WindowsPrintCoordinator(knownFailureFixture.Store, retryBackend,
            static () => "known-failure-retry", knownFailureFixture.Artifacts)
            .SubmitAsync(knownFailureFixture.Job.JobId, knownFailureFixture.PreparedRun, "known-failure-key", FixedTime.AddMinutes(1))
            .GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.Accepted, retry.Status);
        Assert.Equal(1, retryBackend.SubmissionCount);
        Assert.Equal(JobState.AcceptedByOs, knownFailureFixture.Store.GetJob(knownFailureFixture.Job.JobId)!.State);

        using var cancellationFixture = new Fixture();
        var cancellationBackend = new TestOnlySimulatedPrintBackend(Capabilities()) { ThrowCancellationBeforeSubmit = true };
        var cancellationCoordinator = new WindowsPrintCoordinator(cancellationFixture.Store, cancellationBackend,
            static () => "known-cancellation", cancellationFixture.Artifacts);
        var cancellation = cancellationCoordinator.SubmitAsync(cancellationFixture.Job.JobId,
            cancellationFixture.PreparedRun, "known-cancellation-key", FixedTime).GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.FailedBeforeSubmit, cancellation.Status);
        Assert.Equal(JobState.FailedBeforeSubmit,
            cancellationFixture.Store.GetJob(cancellationFixture.Job.JobId)!.State);
        cancellationBackend.ThrowCancellationBeforeSubmit = false;
        var retriedCancellation = cancellationCoordinator.SubmitAsync(cancellationFixture.Job.JobId,
            cancellationFixture.PreparedRun, "known-cancellation-key", FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(PrintSubmissionStatus.Accepted, retriedCancellation.Status);
        Assert.Equal(2, cancellationBackend.SubmissionCount);
        Assert.Equal(JobState.AcceptedByOs,
            cancellationFixture.Store.GetJob(cancellationFixture.Job.JobId)!.State);
    }

    internal static void ConcurrentDuplicateSubmissionKeepsActiveIntent()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var submissionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.PendingSubmissionRelease = submissionRelease;
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-active", fixture.Artifacts);
        var firstTask = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "active-submit-key", FixedTime);
        PrintSubmissionResult? duplicate = null;
        PrintSubmissionResult? first = null;

        try
        {
            Assert.True(backend.SubmissionStarted.Task.Wait(TimeSpan.FromSeconds(5)),
                "The first backend submission did not reach its deterministic gate.");
            duplicate = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "active-submit-key", FixedTime)
                .GetAwaiter().GetResult();
            Assert.Equal(JobState.Submitting, fixture.Store.GetJob(fixture.Job.JobId)!.State);
        }
        finally
        {
            submissionRelease.TrySetResult();
            first = firstTask.GetAwaiter().GetResult();
        }

        Assert.Equal(PrintSubmissionStatus.Accepted, duplicate!.Status);
        Assert.Equal(JobState.Submitting, duplicate.Job!.State);
        Assert.Equal(PrintSubmissionStatus.Accepted, first!.Status);
        Assert.Equal(JobState.AcceptedByOs, first.Job!.State);
        Assert.Equal(1, backend.SubmissionCount);
        Assert.Equal(1, backend.OsSubmissionCount);
        Assert.Equal(1, backend.Receipts.Count);
        Assert.Equal(backend.Receipts.Single(), fixture.Store.GetJob(fixture.Job.JobId)!.OsReceipt);
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
        var cancel = coordinator.CancelAsync(fixture.Job.JobId, FixedTime.AddMinutes(2)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelRequested, cancel.State);
        Assert.Equal("queue-identity", backend.LastQueueIdentity);
        Assert.Equal("job-1", backend.LastJobId);
        Assert.Equal("run-nonce-1", backend.LastRunNonce);
        Assert.Equal(1, backend.CancelCount);

        backend.LastStatus = PrintBackendJobStatus.ReportedComplete;
        var afterCancel = coordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(3)).GetAwaiter().GetResult();
        Assert.Equal(JobState.OsReportsComplete, afterCancel.State);
    }

    internal static void CapabilityLookupFailureIsFailedBeforeSubmitAndRetryable()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities()) { ThrowCapabilityLookupBeforeSubmit = true };
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "capability-retry", fixture.Artifacts);

        var first = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "capability-retry-key", FixedTime)
            .GetAwaiter().GetResult();

        Assert.Equal(PrintSubmissionStatus.FailedBeforeSubmit, first.Status);
        Assert.Equal(JobState.FailedBeforeSubmit, fixture.Store.GetJob(fixture.Job.JobId)!.State);
        Assert.Equal(0, backend.OsSubmissionCount);

        backend.ThrowCapabilityLookupBeforeSubmit = false;
        var retry = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "capability-retry-key", FixedTime.AddMinutes(1))
            .GetAwaiter().GetResult();

        Assert.Equal(PrintSubmissionStatus.Accepted, retry.Status);
        Assert.Equal(1, backend.OsSubmissionCount);
        Assert.Equal(2, backend.SubmissionCount);
        Assert.Equal(JobState.AcceptedByOs, fixture.Store.GetJob(fixture.Job.JobId)!.State);
    }

    internal static void RemovedQueueDuringCapabilityLookupIsRejectedBeforeSubmission()
    {
        foreach (var missingQueueRead in new[] { 1, 2 })
        {
            using var fixture = new Fixture();
            var backend = new TestOnlySimulatedPrintBackend(Capabilities())
            {
                MissingQueueOnCapabilityRead = missingQueueRead
            };
            var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "unused", fixture.Artifacts);

            var result = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun,
                $"removed-queue-read-{missingQueueRead}", FixedTime).GetAwaiter().GetResult();

            Assert.Equal(PrintSubmissionStatus.Rejected, result.Status);
            Assert.Contains("no longer present", result.Reason!);
            Assert.Equal(0, backend.SubmissionCount);
            Assert.Equal(0, backend.OsSubmissionCount);
            Assert.Equal(JobState.Ready, fixture.Store.GetJob(fixture.Job.JobId)!.State);
            Assert.Equal(1, fixture.Store.GetTransitions(fixture.Job.JobId).Length);
        }
    }

    internal static void RemovedQueueDuringSubmitReturnsStructuredIpcRejection()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities()) { MissingQueueOnCapabilityRead = 1 };
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "unused", fixture.Artifacts);
        var handler = new ProductionCoordinatorRequestHandler(fixture.Store, coordinator);
        var pipeName = "paperbatch-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeCoordinatorServer(pipeName, handler.HandleAsync);
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunAsync(shutdown.Token);
        try
        {
            var transport = new NamedPipeLocalCoordinatorTransport(pipeName);
            using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { arguments = new[] { fixture.Job.JobId } }));
            var request = new IpcRequestEnvelope(IpcJson.CurrentProtocolVersion, Guid.NewGuid(),
                "cli.submit", "removed-queue-ipc", payload.RootElement.Clone());
            var response = transport.SendAsync(request).AsTask().GetAwaiter().GetResult();

            Assert.True(response.Error is null, "A removed queue should return the normal structured submit response.");
            Assert.Equal("Rejected", response.Payload!.Value.GetProperty("status").GetString());
            Assert.Equal(JobState.Ready, fixture.Store.GetJob(fixture.Job.JobId)!.State);
            Assert.Equal(1, fixture.Store.GetTransitions(fixture.Job.JobId).Length);
            Assert.Equal(0, backend.OsSubmissionCount);
        }
        finally
        {
            shutdown.Cancel();
            try { serverTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    internal static void AcceptedCancellationIsObservedOnlyAfterCancelledStatus()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1", fixture.Artifacts);
        coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime).GetAwaiter().GetResult();

        var cancel = coordinator.CancelAsync(fixture.Job.JobId, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelRequested, cancel.State);

        backend.LastStatus = PrintBackendJobStatus.Cancelled;
        var observed = coordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(2)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelObserved, observed.State);
    }

    internal static void UnknownStatusDoesNotConfirmCancellationAfterRequest()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1", fixture.Artifacts);
        coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime).GetAwaiter().GetResult();
        var cancel = coordinator.CancelAsync(fixture.Job.JobId, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelRequested, cancel.State);

        backend.LastStatus = PrintBackendJobStatus.Processing;
        var continuing = coordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(2)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelRequested, continuing.State);

        backend.LastStatus = PrintBackendJobStatus.Unknown;
        var refreshed = coordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(3)).GetAwaiter().GetResult();
        Assert.Equal(JobState.NeedsReviewUnknown, refreshed.State);
        Assert.True(refreshed.State != JobState.CancelObserved);
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

    internal static void SubmissionRejectsLegacyMultiSegmentRunWithoutOwnershipMap()
    {
        using var source = new Fixture();
        var first = source.PreparedRun.Items[0];
        var second = first with { ItemId = "item-2" };
        var segments = new[]
        {
            new CompatibleSegment("segment-1", 0, [first.ItemId], "compat", first.PrintSettings),
            new CompatibleSegment("segment-2", 1, [second.ItemId], "compat", second.PrintSettings)
        };
        var legacy = PreparedRun.Create(source.PreparedRun.RunId, source.PreparedRun.CreatedAtUtc,
            source.PreparedRun.SnapshotFingerprint, [first, second], segments, source.PreparedRun.FinalArtifact);
        using var fixture = new Fixture(legacy);
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "legacy-multi", fixture.Artifacts);

        var result = coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "legacy-multi-submit", FixedTime)
            .GetAwaiter().GetResult();

        Assert.Equal(PrintSubmissionStatus.Rejected, result.Status);
        Assert.Contains("Reprepare", result.Reason!);
        Assert.Equal(0, backend.CapabilityReads);
        Assert.Equal(0, backend.SubmissionCount);
        Assert.Equal(JobState.Ready, fixture.Store.GetJob(fixture.Job.JobId)!.State);
    }

    internal static void CoordinatorOrdersContiguousSegmentJobsByFinalPageOrder()
    {
        using var source = new Fixture();
        var first = source.PreparedRun.Items[0];
        var second = first with { ItemId = "item-2" };
        var settings = first.PrintSettings;
        var segments = new[]
        {
            new CompatibleSegment("segment-1", 0, [first.ItemId], "compat", settings, [2]),
            new CompatibleSegment("segment-2", 1, [second.ItemId], "compat", settings, [1])
        };
        var twoPagePdf = PdfCompositionTests.TwoPagePdf();
        var artifactReceipt = source.Artifacts.PersistPdfAsync(new MemoryStream(twoPagePdf), "test-renderer", "1",
            new Dictionary<string, string>()).GetAwaiter().GetResult();
        var finalArtifact = new FinalPdfArtifact(artifactReceipt.Artifact.ArtifactId, artifactReceipt.Artifact.Sha256,
            artifactReceipt.Artifact.LengthBytes, 2);
        var run = PreparedRun.Create(source.PreparedRun.RunId, source.PreparedRun.CreatedAtUtc,
            source.PreparedRun.SnapshotFingerprint, [first, second], segments, finalArtifact);
        using var fixture = new Fixture(run, twoPagePdf, 2);
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "segment-pages", fixture.Artifacts);

        var results = coordinator.SubmitRunAsync(run, "segment-pages-submit", FixedTime).GetAwaiter().GetResult();

        Assert.Equal(2, results.Count);
        Assert.True(results.All(result => result.Status == PrintSubmissionStatus.Accepted));
        Assert.True(backend.SubmittedPageNumbers[0].SequenceEqual([1]));
        Assert.True(backend.SubmittedPageNumbers[1].SequenceEqual([2]));
    }

    internal static void CoordinatorSubmitsSplitOwnerBlocksInComposedOrder()
    {
        using var source = new Fixture();
        var first = source.PreparedRun.Items[0];
        var second = first with { ItemId = "item-2" };
        var settings = first.PrintSettings;
        var artifact = source.PreparedRun.FinalArtifact! with { PageCount = 3 };
        var segments = new[]
        {
            new CompatibleSegment("first-block", 0, [first.ItemId], "compat", settings, [1]),
            new CompatibleSegment("second-document", 1, [second.ItemId], "compat", settings, [2]),
            new CompatibleSegment("first-copy", 2, [first.ItemId], "compat", settings, [3])
        };
        var run = PreparedRun.Create("run-split-owner-blocks", FixedTime, Hash, [first, second], segments, artifact);
        using var fixture = new Fixture(run, RendererTests.OnePagePdf(), 3);
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "split-owner-blocks", fixture.Artifacts);

        var results = coordinator.SubmitRunAsync(run, "split-owner-blocks-submit", FixedTime).GetAwaiter().GetResult();

        Assert.Equal(3, results.Count);
        Assert.True(results.All(result => result.Status == PrintSubmissionStatus.Accepted));
        Assert.True(backend.SubmittedPageNumbers.SelectMany(pages => pages).SequenceEqual([1, 2, 3]));
        Assert.True(backend.SubmittedPageNumbers.Select(pages => pages.Single()).SequenceEqual([1, 2, 3]));
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

    internal static void CheckedInSinglePdfPrintContractRemainsRegression()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_PATH");
        var runtimeHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_SHA256");
        if (string.IsNullOrWhiteSpace(runtimePath) || string.IsNullOrWhiteSpace(runtimeHash))
        {
            Console.WriteLine("SKIP checked-in single-PDF print regression (configured pinned PDFium runtime is unavailable).");
            return;
        }

        using var single = new BatchFixture(runtimePath, runtimeHash);
        var prepared = single.Prepare("checked-in-single-pdf-regression-key", sourceCount: 1);
        Assert.True(prepared.Succeeded, string.Join("; ", prepared.Diagnostics));
        Assert.Equal(1, prepared.Run!.Items.Length);
        Assert.Equal(2, prepared.Run.FinalArtifact!.PageCount);
        var persistedPageSizes = single.GetSavedPageSizes(prepared.Run.FinalArtifact);
        var expectedPdf = single.ReadPersistedFinalPdf(prepared.Run.FinalArtifact);
        var native = new FakeGdiNative(Capabilities());
        var rasterizer = new FakeGdiRasterizer();
        var coordinator = new WindowsPrintCoordinator(single.Runs,
            new WindowsGdiPrintBackend(native, rasterizer), static () => "single-pdf-regression", single.Artifacts);
        var submitted = coordinator.SubmitRunAsync(prepared.Run, "single-pdf-regression-submit", FixedTime).GetAwaiter().GetResult();
        Assert.Equal(1, submitted.Count);
        Assert.Equal(PrintSubmissionStatus.Accepted, submitted[0].Status, SubmissionFailureDetails(submitted[0]));
        Assert.True(persistedPageSizes.SequenceEqual(rasterizer.RasterizedPageSizes.Single()),
            "The fake rasterizer should preserve the persisted PDFium page geometry.");
        Assert.True(expectedPdf.SequenceEqual(rasterizer.ReceivedBytes.Single()));
        Assert.Equal(2, native.Documents.Single().WritePageCount);
        Assert.Equal("simulated-job-1", single.Runs.GetJob(prepared.Persistence!.Jobs.Single().JobId)!.OsReceipt!.OsJobId);
    }

    internal static void CheckedInBatchFlowsThroughPreparationCompositionAndPrintOrchestration()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_PATH");
        var runtimeHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_SHA256");
        if (string.IsNullOrWhiteSpace(runtimePath) || string.IsNullOrWhiteSpace(runtimeHash))
        {
            Console.WriteLine("SKIP checked-in fixture print path (configured pinned PDFium runtime is unavailable).");
            return;
        }

        using var batch = new BatchFixture(runtimePath, runtimeHash);
        var firstPrepared = batch.Prepare("checked-in-batch-contract-key");
        Assert.True(firstPrepared.Succeeded, string.Join("; ", firstPrepared.Diagnostics));
        var run = firstPrepared.Run!;
        var finalArtifact = run.FinalArtifact!;
        Assert.Equal(2, run.Items.Length);
        Assert.Equal(batch.SourceHashes[0], run.Items[0].Source.Sha256);
        Assert.Equal(batch.SourceHashes[1], run.Items[1].Source.Sha256);
        Assert.Equal("paperbatch.pdf-pass-through", run.Items[0].Render.RendererId);
        Assert.Equal("paperbatch.builtin-image", run.Items[1].Render.RendererId);
        Assert.True(run.Items[0].PageMap.SequenceEqual([new PageMapping(1, 1), new PageMapping(2, 2)]));
        Assert.True(run.Items[1].PageMap.SequenceEqual([new PageMapping(1, 1)]));
        Assert.Equal(3, finalArtifact.PageCount);
        Assert.Equal(3, batch.GetSavedPageCount(finalArtifact));
        Assert.True(batch.SavedPageContainsRedMarker(finalArtifact, 3),
            "The final page should contain the checked-in PNG marker after the two ordered PDF pages.");
        var persistedPageSizes = batch.GetSavedPageSizes(finalArtifact);
        Assert.Equal(1, run.Segments.Length);
        Assert.True(run.Segments[0].ItemIds.SequenceEqual(run.Items.Select(item => item.ItemId)));
        Assert.True(run.Segments[0].FinalPageNumbers.SequenceEqual([1, 2, 3]));
        var jobs = firstPrepared.Persistence!.Jobs;
        Assert.Equal(1, jobs.Length);
        Assert.Equal(0, jobs[0].SegmentIndex);

        var expectedPdf = batch.ReadPersistedFinalPdf(finalArtifact);
        var native = new FakeGdiNative(Capabilities());
        var rasterizer = new FakeGdiRasterizer();
        var backend = new WindowsGdiPrintBackend(native, rasterizer);
        var nonceIndex = 0;
        var coordinator = new WindowsPrintCoordinator(batch.Runs, backend,
            () => $"batch-print-run-{Interlocked.Increment(ref nonceIndex)}", batch.Artifacts);
        var results = coordinator.SubmitRunAsync(run, "checked-in-batch-submit-key", FixedTime).GetAwaiter().GetResult();

        Assert.Equal(1, results.Count);
        Assert.Equal(PrintSubmissionStatus.Accepted, results[0].Status, SubmissionFailureDetails(results[0]));
        Assert.True(persistedPageSizes.SequenceEqual(rasterizer.RasterizedPageSizes.Single()),
            "The fake rasterizer should preserve the composed PDFium page geometry.");
        Assert.Equal(1, native.StartDocCount);
        Assert.True(expectedPdf.SequenceEqual(rasterizer.ReceivedBytes.Single()));
        Assert.Equal(finalArtifact.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rasterizer.ReceivedBytes.Single())).ToLowerInvariant());
        Assert.Equal(finalArtifact.LengthBytes, rasterizer.ReceivedBytes.Single().LongLength);
        var submission = native.Submissions.Single();
        Assert.True(submission.Segment.ItemIds.SequenceEqual(run.Items.Select(item => item.ItemId)));
        Assert.Equal("queue-identity", submission.Options.QueueIdentity);
        Assert.Equal("Letter", submission.Options.Media);
        Assert.Equal("Tray1", submission.Options.Tray);
        Assert.Equal("Color", submission.Options.Color);
        Assert.Equal("LongEdge", submission.Options.Duplex);
        Assert.Equal(1, submission.Segment.PrintSettings.Copies);
        var document = native.Documents.Single();
        Assert.Equal(3, document.StartPageCount);
        Assert.Equal(3, document.WritePageCount);
        Assert.Equal(3, document.EndPageCount);
        Assert.Equal(1, document.EndDocCount);
        Assert.True(document.Disposed);
        var storedJob = batch.Runs.GetJob(jobs[0].JobId)!;
        var receipt = storedJob.OsReceipt ?? throw new InvalidOperationException("The batch coordinator did not persist its fake OS job receipt.");
        Assert.Equal(JobState.AcceptedByOs, storedJob.State);
        Assert.Equal("queue-identity", receipt.QueueIdentity);
        Assert.Equal("simulated-job-1", receipt.OsJobId);
        Assert.Equal("batch-print-run-1", receipt.RunNonce);
        Assert.Equal("Test Driver", receipt.DriverName);

        native.Status = PrintBackendJobStatus.ReportedComplete;
        var completed = coordinator.RefreshStatusAsync(jobs[0].JobId, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(JobState.OsReportsComplete, completed.State);
        Assert.True(completed.State != JobState.UserConfirmedPaperOutput);

        using var partial = new BatchFixture(runtimePath, runtimeHash);
        var partialPrepared = partial.Prepare("checked-in-batch-partial-key",
            [new LayoutDocumentSettings(Copies: 1), new LayoutDocumentSettings(Copies: 2)]);
        Assert.True(partialPrepared.Succeeded, string.Join("; ", partialPrepared.Diagnostics));
        var partialRun = partialPrepared.Run!;
        Assert.Equal(2, partialRun.Items.Length);
        Assert.Equal(4, partialRun.FinalArtifact!.PageCount);
        Assert.Equal(4, partial.GetSavedPageCount(partialRun.FinalArtifact));
        Assert.Equal(2, partialRun.Segments.Length);
        Assert.True(partialRun.Segments[0].FinalPageNumbers.SequenceEqual([1, 2]));
        Assert.True(partialRun.Segments[1].FinalPageNumbers.SequenceEqual([3, 4]));
        Assert.Equal(2, partialPrepared.Persistence!.Jobs.Length);
        Assert.True(partialRun.Segments.Select(segment => segment.ItemIds.Single())
            .SequenceEqual(partialRun.Items.Select(item => item.ItemId)));
        var partialNative = new FakeGdiNative(Capabilities(), failOnEndDocNumber: 2);
        var partialRasterizer = new FakeGdiRasterizer();
        var partialCoordinator = new WindowsPrintCoordinator(partial.Runs,
            new WindowsGdiPrintBackend(partialNative, partialRasterizer),
            () => $"batch-partial-run-{Interlocked.Increment(ref nonceIndex)}", partial.Artifacts);
        var partialJobs = partialPrepared.Persistence.Jobs;
        var partialResults = partialCoordinator.SubmitRunAsync(partialRun, "checked-in-batch-partial-submit-key", FixedTime)
            .GetAwaiter().GetResult();
        Assert.Equal(2, partialResults.Count);
        Assert.Equal(PrintSubmissionStatus.Accepted, partialResults[0].Status);
        Assert.Equal(PrintSubmissionStatus.NeedsReviewUnknown, partialResults[1].Status);
        Assert.Equal(2, partialNative.StartDocCount);
        Assert.Equal(2, partialNative.Submissions.Count);
        Assert.True(partialRasterizer.RasterizedPageNumbers[0].SequenceEqual([1, 2]));
        Assert.True(partialRasterizer.RasterizedPageNumbers[1].SequenceEqual([3, 4]));
        Assert.Equal(2, partialNative.Documents[0].WritePageCount);
        Assert.Equal(2, partialNative.Documents[1].WritePageCount);
        Assert.True(partialNative.Submissions.All(value => value.Options.QueueIdentity == "queue-identity" &&
            value.Options.Media == "Letter" && value.Options.Tray == "Tray1" && value.Options.Color == "Color" &&
            value.Options.Duplex == "LongEdge"));
        var firstPartialJob = partial.Runs.GetJob(partialJobs[0].JobId)!;
        var secondPartialJob = partial.Runs.GetJob(partialJobs[1].JobId)!;
        Assert.Equal(0, firstPartialJob.SegmentIndex);
        Assert.Equal(1, secondPartialJob.SegmentIndex);
        Assert.Equal(JobState.AcceptedByOs, firstPartialJob.State);
        Assert.Equal("simulated-job-1", firstPartialJob.OsReceipt!.OsJobId);
        Assert.Equal("batch-partial-run-2", firstPartialJob.OsReceipt.RunNonce);
        Assert.Equal(JobState.NeedsReviewUnknown, secondPartialJob.State);
        Assert.True(secondPartialJob.OsReceipt is null);
        Assert.Equal(2, partialNative.Documents.Count);
        Assert.True(partialNative.Documents.All(value => value.Disposed));
        Assert.Equal(2, partialNative.Documents.Select(value => value.OsJobId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1, partialNative.Documents[1].EndDocCount);

        var submissionsBeforeReplay = partialNative.StartDocCount;
        var retryAfterAmbiguous = partialCoordinator.SubmitRunAsync(partialRun,
            "checked-in-batch-partial-submit-key", FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(0, retryAfterAmbiguous.Count);
        Assert.Equal(2, partialNative.StartDocCount);
        Assert.Equal(JobState.NeedsReviewUnknown, partial.Runs.GetJob(partialJobs[1].JobId)!.State);

        var reportPath = Environment.GetEnvironmentVariable("PAPERBATCH_PRINT_PATH_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var report = new
            {
                schemaVersion = 2,
                status = "passed",
                commit = RequiredEnvironment("PB_SOURCE_COMMIT"),
                runIdentity = new
                {
                    workflow = RequiredEnvironment("GITHUB_WORKFLOW"),
                    runId = RequiredEnvironment("GITHUB_RUN_ID"),
                    runAttempt = RequiredEnvironment("GITHUB_RUN_ATTEMPT")
                },
                os = RequiredEnvironment("RUNNER_OS"),
                runner = RequiredEnvironment("RUNNER_NAME"),
                runnerImage = RequiredEnvironment("ImageOS"),
                runnerImageVersion = RequiredEnvironment("ImageVersion"),
                toolVersions = new
                {
                    dotnetSdk = RequiredEnvironment("PAPERBATCH_DOTNET_SDK_VERSION"),
                    dotnetRuntime = Environment.Version.ToString(),
                    pdfiumVersion = RequiredEnvironment("PAPERBATCH_PDFIUM_TEST_VERSION"),
                    pdfiumSha256 = RequiredEnvironment("PAPERBATCH_PDFIUM_TEST_SHA256")
                },
                sourceFixtures = batch.SourceFixtures.Select((fixture, index) => new
                {
                    order = index + 1,
                    fixture.path,
                    sha256 = batch.SourceHashes[index],
                    lengthBytes = batch.SourceBytes[index].LongLength,
                    format = fixture.format,
                    renderer = index == 0 ? "paperbatch.pdf-pass-through" : "paperbatch.builtin-image"
                }),
                batchPrint = new
                {
                    sourceCount = run.Items.Length,
                    sourceOrder = run.Items.Select(item => item.Source.Sha256).ToArray(),
                    finalArtifact = new { sha256 = finalArtifact.Sha256, lengthBytes = finalArtifact.LengthBytes, pageCount = finalArtifact.PageCount },
                    segmentCount = run.Segments.Length,
                    jobCount = jobs.Length,
                    exactArtifactBytesSubmitted = expectedPdf.SequenceEqual(rasterizer.ReceivedBytes.Single()),
                    simulatedSubmission = "passed",
                    submissions = native.Submissions.Select(value => new
                    {
                        segmentIndex = value.Segment.Index,
                        itemIds = value.Segment.ItemIds,
                        queueIdentity = value.Options.QueueIdentity,
                        media = value.Options.Media,
                        tray = value.Options.Tray,
                        color = value.Options.Color,
                        duplex = value.Options.Duplex,
                        copies = value.Segment.PrintSettings.Copies
                    }),
                    gdi = new
                    {
                        startDoc = native.StartDocCount,
                        startPage = document.StartPageCount,
                        writePage = document.WritePageCount,
                        endPage = document.EndPageCount,
                        endDoc = document.EndDocCount
                    },
                    receipt = new { storedJobId = storedJob.JobId, osJobId = receipt.OsJobId, receipt.RunNonce, receipt.QueueIdentity },
                    reportedCompleteState = completed.State.ToString()
                },
                partialFailure = new
                {
                    sourceCount = partialRun.Items.Length,
                    pageCount = partialRun.FinalArtifact.PageCount,
                    segmentCount = partialRun.Segments.Length,
                    jobCount = partialJobs.Length,
                    evidenceLimit = PartialFailureEvidenceLimit,
                    outcomes = partialResults.Select(result => result.Status.ToString()).ToArray(),
                    submissions = partialNative.Submissions.Select((value, index) => new
                    {
                        segmentIndex = value.Segment.Index,
                        itemIds = value.Segment.ItemIds,
                        outcome = partialResults[index].Status.ToString(),
                        queueIdentity = value.Options.QueueIdentity,
                        media = value.Options.Media,
                        tray = value.Options.Tray,
                        color = value.Options.Color,
                        duplex = value.Options.Duplex,
                        copies = value.Segment.PrintSettings.Copies
                    }),
                    firstStoredJobId = firstPartialJob.JobId,
                    ambiguousStoredJobId = secondPartialJob.JobId,
                    firstJobReceipt = firstPartialJob.OsReceipt!.OsJobId,
                    ambiguousJobState = secondPartialJob.State.ToString(),
                    submissionsBeforeReplay,
                    submissionsAfterReplay = partialNative.StartDocCount,
                    automaticResubmit = false
                },
                nativeQueueInventory = new { status = "not_run", reason = "The test injects a fake GDI/Winspool adapter." },
                nativeRealQueueSubmission = new { status = "not_run", reason = "No real queue is queried or submitted by this test." },
                physicalPaperOutput = new { status = "unavailable", reason = "The fake boundary creates no OS job and produces no pages." },
                scenarios = new[]
                {
                    new { scenarioId = "simulated_batch_submission", outcome = "passed", evidenceLimit = "The injected fake GDI/Winspool boundary creates no OS print job." },
                    new { scenarioId = "batch_partial_ambiguous_stop", outcome = "passed", evidenceLimit = "The second fake submission becomes unknown after StartDoc; replay does not submit again." },
                    new { scenarioId = "native_queue_inventory", outcome = "not_run", evidenceLimit = "No native printer queue is enumerated." },
                    new { scenarioId = "native_real_queue_submission", outcome = "not_run", evidenceLimit = "No OS print job is created." },
                    new { scenarioId = "physical_paper_output", outcome = "unavailable", evidenceLimit = "No physical output is produced or observed." }
                },
                evidenceLimits = new[]
                {
                    "The batch test exercises two checked-in sources: a two-page PDF followed by a one-page RGB PNG.",
                    "The current model groups these matching printer settings into one segment and one job; per-item layout copy expansion is separately tested as two ordered segments/jobs.",
                    PageSelectionEvidenceLimit,
                    "Production renderer formats are PDF, JPEG and PNG; this batch sample directly exercises PDF and PNG, not JPEG or office document conversion.",
                    "The test proves PaperBatch orchestration to an injected fake boundary only; it does not prove OS queue acceptance or physical paper output."
                }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Print path evidence requires {name}.");

    private sealed class FakeGdiRasterizer : IWindowsPrintPdfRasterizer
    {
        public List<byte[]> ReceivedBytes { get; } = [];
        public List<PageSize[]> RasterizedPageSizes { get; } = [];
        public List<int[]> RasterizedPageNumbers { get; } = [];

        public async Task<IReadOnlyList<WindowsPrintRasterPage>> RasterizeAsync(
            Stream pdf, int expectedPageCount, CancellationToken cancellationToken = default)
            => await RasterizePagesAsync(pdf, expectedPageCount, Enumerable.Range(1, expectedPageCount).ToArray(), cancellationToken);

        public async Task<IReadOnlyList<WindowsPrintRasterPage>> RasterizePagesAsync(
            Stream pdf, int expectedPageCount, IReadOnlyList<int> pageNumbers, CancellationToken cancellationToken = default)
        {
            using var content = new MemoryStream();
            await pdf.CopyToAsync(content, cancellationToken);
            ReceivedBytes.Add(content.ToArray());
            var pages = pageNumbers
                .Select(page => new WindowsPrintRasterPage(page, new PageSize(612, 792), new PdfRasterPage(17, 22, 68, new byte[1496])))
                .ToArray();
            RasterizedPageSizes.Add(pages.Select(page => page.PdfSize).ToArray());
            RasterizedPageNumbers.Add(pages.Select(page => page.Number).ToArray());
            return pages;
        }
    }

    private sealed class FakeGdiNative(PrinterCapabilitySnapshot capabilities, int failOnEndDocNumber = 0) : IWindowsGdiNativeAdapter
    {
        public int StartDocCount { get; private set; }
        public List<WindowsPrintSubmission> Submissions { get; } = [];
        public List<FakeGdiDocument> Documents { get; } = [];
        public PrintBackendJobStatus Status { get; set; } = PrintBackendJobStatus.Processing;

        public Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default) =>
            Task.FromResult(capabilities);

        public Task<IWindowsGdiDocument> StartDocAsync(
            WindowsPrintSubmission submission, IReadOnlyList<WindowsPrintRasterPage> pages,
            CancellationToken cancellationToken = default)
        {
            StartDocCount++;
            Submissions.Add(submission);
            var document = new FakeGdiDocument(pages.Count, $"simulated-job-{StartDocCount}", StartDocCount == failOnEndDocNumber);
            Documents.Add(document);
            return Task.FromResult<IWindowsGdiDocument>(document);
        }

        public Task<PrintBackendJobStatus> GetStatusAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default) => Task.FromResult(Status);
        public Task<bool> CancelAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeGdiDocument(int expectedPages, string osJobId, bool failOnEndDoc) : IWindowsGdiDocument
    {
        public string OsJobId => osJobId;
        public int StartPageCount { get; private set; }
        public int WritePageCount { get; private set; }
        public int EndPageCount { get; private set; }
        public int EndDocCount { get; private set; }
        public bool Disposed { get; private set; }

        public Task StartPageAsync(CancellationToken cancellationToken = default) { StartPageCount++; return Task.CompletedTask; }
        public Task WritePageAsync(WindowsPrintRasterPage page, CancellationToken cancellationToken = default)
        { WritePageCount++; return Task.CompletedTask; }
        public Task EndPageAsync(CancellationToken cancellationToken = default) { EndPageCount++; return Task.CompletedTask; }
        public Task EndDocAsync(CancellationToken cancellationToken = default)
        {
            EndDocCount++;
            if (EndDocCount != 1 || expectedPages != EndPageCount)
                throw new InvalidOperationException("Unexpected GDI page/document sequence.");
            if (failOnEndDoc)
                throw new IOException("The deterministic fake lost the completion receipt after StartDoc.");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class BatchFixture : IDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "paperbatch-batch-print-" + Guid.NewGuid().ToString("N"));
        private readonly PreparedArtifactCoordinator _preparation;
        private readonly PdfiumArtifactInspector _inspector;

        public BatchFixture(string runtimePath, string runtimeHash)
        {
            Directory.CreateDirectory(_temporary);
            SourceFixtures =
            [
                ("tests/fixtures/synthetic_documents/synthetic_two_page.pdf",
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic_two_page.pdf"), "PDF",
                    "ab35f052c3792879195636d8f69d356e66c3f026ec246d31ebc73e00527caeee", 958L),
                ("tests/fixtures/print_batch/synthetic_batch_marker.png",
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic_batch_marker.png"), "PNG",
                    "9f1d47567cf62d3c3998f12f998afc0bd787ea4ffdc979e5d1f7fc46c17c10e7", 173L)
            ];
            SourceBytes = SourceFixtures.Select(fixture => File.ReadAllBytes(fixture.Item2)).ToArray();
            SourceHashes = SourceBytes.Select(bytes => Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()).ToArray();
            Assert.True(SourceHashes.SequenceEqual(SourceFixtures.Select(fixture => fixture.expectedSha256)));
            Assert.True(SourceBytes.Select(bytes => bytes.LongLength).SequenceEqual(SourceFixtures.Select(fixture => fixture.expectedLength)));
            Snapshots = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(_temporary, "snapshots") });
            Artifacts = new RenderArtifactStore(Path.Combine(_temporary, "artifacts"));
            Runs = new SqliteRunStore(Path.Combine(_temporary, "runs.db"));
            Runs.Initialize();
            var renderer = new RendererCoordinator(
            [
                new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(Snapshots, Artifacts)),
                new BuiltInImageRendererAdapter(Snapshots, Artifacts)
            ]);
            _inspector = new PdfiumArtifactInspector(runtimePath, runtimeHash);
            _preparation = new PreparedArtifactCoordinator(new SourceIntakeService(Snapshots), Snapshots, renderer,
                _inspector, _inspector, new PdfiumPageCompositionAdapter(runtimePath, runtimeHash), Artifacts, Runs);
        }

        public (string path, string fullPath, string format, string expectedSha256, long expectedLength)[] SourceFixtures { get; }
        public byte[][] SourceBytes { get; }
        public string[] SourceHashes { get; }
        public SourceSnapshotCache Snapshots { get; }
        public RenderArtifactStore Artifacts { get; }
        public SqliteRunStore Runs { get; }

        public PreparedArtifactResult Prepare(string requestKey, IReadOnlyList<LayoutDocumentSettings>? itemLayoutSettings = null,
            int sourceCount = 2)
        {
            var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.LongEdge,
                "Letter", Orientation.Portrait, ScalingMode.ShrinkToFit, "queue-identity", "Tray1");
            var inputs = SourceFixtures.Take(sourceCount).Select((fixture, index) => new SourceInput(fixture.fullPath,
                ExpectedSha256: SourceHashes[index])).ToArray();
            return _preparation.PrepareAsync(inputs, settings,
                new LayoutOptions(NUp: NUpMode.One, SheetSize: new PageSize(612, 792)), requestKey,
                new RetentionMetadata(FixedTime.AddDays(1), RetainWhileUnresolved: true), FixedTime,
                perItemPrintSettings: null, perItemLayoutSettings: itemLayoutSettings).GetAwaiter().GetResult();
        }

        public byte[] ReadPersistedFinalPdf(FinalPdfArtifact artifact)
        {
            using var persisted = Artifacts.OpenRead(artifact.ArtifactId);
            using var content = new MemoryStream();
            persisted.CopyTo(content);
            var bytes = content.ToArray();
            Assert.Equal(artifact.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
            Assert.Equal(artifact.LengthBytes, bytes.LongLength);
            return bytes;
        }

        public int GetSavedPageCount(FinalPdfArtifact artifact)
        {
            using var persisted = Artifacts.OpenRead(artifact.ArtifactId);
            return _inspector.GetPageSizesAsync(persisted, CancellationToken.None).GetAwaiter().GetResult().Count;
        }

        public IReadOnlyList<PageSize> GetSavedPageSizes(FinalPdfArtifact artifact)
        {
            using var persisted = Artifacts.OpenRead(artifact.ArtifactId);
            return _inspector.GetPageSizesAsync(persisted, CancellationToken.None).GetAwaiter().GetResult();
        }

        public bool SavedPageContainsRedMarker(FinalPdfArtifact artifact, int pageNumber)
        {
            using var persisted = Artifacts.OpenRead(artifact.ArtifactId);
            var raster = _inspector.RenderPageAtDpiAsync(persisted, pageNumber, 72, 1024, CancellationToken.None)
                .GetAwaiter().GetResult();
            for (var offset = 0; offset + 3 < raster.BgraPixels.Length; offset += 4)
            {
                var blue = raster.BgraPixels[offset];
                var green = raster.BgraPixels[offset + 1];
                var red = raster.BgraPixels[offset + 2];
                if (red > 180 && green < 100 && blue < 100) return true;
            }
            return false;
        }

        public void Dispose()
        {
            try { Directory.Delete(_temporary, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
        var twoPagePdf = PdfCompositionTests.TwoPagePdf();
        var stored = seed.Artifacts.PersistPdfAsync(new MemoryStream(twoPagePdf), "test-renderer", "1",
            new Dictionary<string, string>()).GetAwaiter().GetResult();
        var finalArtifact = new FinalPdfArtifact(stored.Artifact.ArtifactId, stored.Artifact.Sha256,
            stored.Artifact.LengthBytes, 2);
        var firstSegment = new CompatibleSegment("segment-1", 0, [firstItem.ItemId], "compat-1", Settings(), [1]);
        var secondSegment = new CompatibleSegment("segment-2", 1, [secondItem.ItemId], "compat-2", Settings(), [2]);
        var run = PreparedRun.Create("run-multi", FixedTime, Hash, [firstItem, secondItem],
            [firstSegment, secondSegment], finalArtifact);

        using var fixture = new Fixture(run, twoPagePdf, 2);
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

    internal static void RunSubmissionRetriesKnownNoEffectSegmentWithoutResubmittingAcceptedSegments()
    {
        using var seed = new Fixture();
        var firstItem = seed.PreparedRun.Items[0];
        var secondItem = firstItem with { ItemId = "item-2" };
        var twoPagePdf = PdfCompositionTests.TwoPagePdf();
        var stored = seed.Artifacts.PersistPdfAsync(new MemoryStream(twoPagePdf), "test-renderer", "1",
            new Dictionary<string, string>()).GetAwaiter().GetResult();
        var finalArtifact = new FinalPdfArtifact(stored.Artifact.ArtifactId, stored.Artifact.Sha256,
            stored.Artifact.LengthBytes, 2);
        var firstSegment = new CompatibleSegment("segment-1", 0, [firstItem.ItemId], "compat-1", Settings(), [1]);
        var secondSegment = new CompatibleSegment("segment-2", 1, [secondItem.ItemId], "compat-2", Settings(), [2]);
        var run = PreparedRun.Create("run-known-no-effect-retry", FixedTime, Hash, [firstItem, secondItem],
            [firstSegment, secondSegment], finalArtifact);

        using var fixture = new Fixture(run, twoPagePdf, 2);
        var backend = new TestOnlySimulatedPrintBackend(Capabilities()) { ThrowCancellationOnSubmissionNumber = 2 };
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "retryable-nonce", fixture.Artifacts);

        var firstAttempt = coordinator.SubmitRunAsync(run, "retryable-key", FixedTime).GetAwaiter().GetResult();
        Assert.Equal(2, firstAttempt.Count);
        Assert.Equal(PrintSubmissionStatus.Accepted, firstAttempt[0].Status);
        Assert.Equal(PrintSubmissionStatus.FailedBeforeSubmit, firstAttempt[1].Status);
        Assert.Equal(JobState.AcceptedByOs, fixture.Store.GetJobs(run.RunId)[0].State);
        Assert.Equal(JobState.FailedBeforeSubmit, fixture.Store.GetJobs(run.RunId)[1].State);
        Assert.Equal(2, backend.SubmissionCount);

        var retry = coordinator.SubmitRunAsync(run, "retryable-key", FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
        Assert.Equal(1, retry.Count);
        Assert.Equal(PrintSubmissionStatus.Accepted, retry[0].Status);
        Assert.Equal(3, backend.SubmissionCount);
        Assert.Equal(JobState.AcceptedByOs, fixture.Store.GetJobs(run.RunId)[0].State);
        Assert.Equal(JobState.AcceptedByOs, fixture.Store.GetJobs(run.RunId)[1].State);
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
        Assert.Equal(JobState.CancelRequested, firstFixture.Store.GetJob(firstFixture.Job.JobId)!.State);
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

    internal static void DelayedProcessingStatusDoesNotOverwriteCancellation()
    {
        using var fixture = new Fixture();
        var backend = new TestOnlySimulatedPrintBackend(Capabilities());
        var coordinator = new WindowsPrintCoordinator(fixture.Store, backend, static () => "run-nonce-1", fixture.Artifacts);
        coordinator.SubmitAsync(fixture.Job.JobId, fixture.PreparedRun, "submit-key", FixedTime).GetAwaiter().GetResult();

        var pendingStatus = new TaskCompletionSource<PrintBackendJobStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.PendingStatusResponse = pendingStatus.Task;
        var refreshTask = coordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(1));
        backend.StatusRequested.Task.GetAwaiter().GetResult();

        var cancelled = coordinator.CancelAsync(fixture.Job.JobId, FixedTime.AddMinutes(2)).GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelRequested, cancelled.State);
        Assert.Equal(JobState.CancelRequested, fixture.Store.GetJob(fixture.Job.JobId)!.State);
        var staleUpdate = fixture.Store.TransitionJob(fixture.Job.JobId, JobState.OsProcessing,
            "A delayed Processing response was based on the earlier accepted state.", FixedTime.AddMinutes(2), JobState.AcceptedByOs);
        Assert.Equal(JobState.CancelRequested, staleUpdate.State);

        pendingStatus.SetResult(PrintBackendJobStatus.Processing);
        var afterDelayedProcessing = refreshTask.GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelRequested, afterDelayedProcessing.State);
        Assert.Equal(JobState.CancelRequested, fixture.Store.GetJob(fixture.Job.JobId)!.State);

        backend.PendingStatusResponse = null;
        backend.LastStatus = PrintBackendJobStatus.Cancelled;
        var afterCancellationObserved = coordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(3))
            .GetAwaiter().GetResult();
        Assert.Equal(JobState.CancelObserved, afterCancellationObserved.State);
        Assert.Equal(JobState.CancelObserved, fixture.Store.GetJob(fixture.Job.JobId)!.State);
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
        Assert.Equal(JobState.CancelRequested, retried.State);
        backend.LastStatus = PrintBackendJobStatus.Cancelled;
        retried = restartedCoordinator.RefreshStatusAsync(fixture.Job.JobId, FixedTime.AddMinutes(3)).GetAwaiter().GetResult();
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

    private static string SubmissionFailureDetails(PrintSubmissionResult result)
    {
        var reason = result.Reason ?? "<no reason provided>";
        if (reason.Length > 512) reason = reason[..512];
        return $"Job state: {result.Job?.State.ToString() ?? "<no stored job>"}; reason: {reason}";
    }

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
        private int _osSubmissionCount;
        private int _cancelCount;
        public PrinterCapabilitySnapshot? CapabilitiesOnSecondRead { get; init; }
        public bool ThrowAfterSubmitSideEffect { get; init; }
        public bool ThrowBeforeSubmit { get; init; }
        public bool ThrowCancellationBeforeSubmit { get; set; }
        public bool ThrowCapabilityLookupBeforeSubmit { get; set; }
        public int? MissingQueueOnCapabilityRead { get; init; }
        public int ThrowCancellationOnSubmissionNumber { get; init; }
        public bool ThrowCapabilityAfterSubmit { get; init; }
        public bool CancelReturnsFalse { get; set; }
        public int SubmissionCount => Volatile.Read(ref _submissionCount);
        public int OsSubmissionCount => Volatile.Read(ref _osSubmissionCount);
        public int CancelCount => Volatile.Read(ref _cancelCount);
        public int CapabilityReads => Volatile.Read(ref _capabilityReads);
        public string? LastQueueIdentity { get; private set; }
        public string? LastJobId { get; private set; }
        public string? LastRunNonce { get; private set; }
        public string? LastArtifactId { get; private set; }
        public byte[]? LastPdfBytes { get; private set; }
        public PrintBackendJobStatus LastStatus { get; set; } = PrintBackendJobStatus.Accepted;
        public Action? OnCancel { get; set; }
        public Task<PrintBackendJobStatus>? PendingStatusResponse { get; set; }
        public TaskCompletionSource StatusRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? PendingSubmissionRelease { get; set; }
        public TaskCompletionSource SubmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentBag<OsJobReceipt> Receipts { get; } = [];
        public List<int[]> SubmittedPageNumbers { get; } = [];

        public Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default)
        {
            var capabilityRead = Interlocked.Increment(ref _capabilityReads);
            if (capabilityRead == MissingQueueOnCapabilityRead)
                return Task.FromException<PrinterCapabilitySnapshot>(new KeyNotFoundException("The selected local printer queue is no longer present."));
            return Task.FromResult(capabilityRead >= 2 ? CapabilitiesOnSecondRead ?? capabilities : capabilities);
        }

        public async Task<OsJobReceipt> SubmitAsync(WindowsPrintSubmission submission, CancellationToken cancellationToken = default)
        {
            var submissionNumber = Interlocked.Increment(ref _submissionCount);
            SubmittedPageNumbers.Add(submission.FinalPageNumbers.ToArray());
            LastQueueIdentity = submission.Options.QueueIdentity;
            LastRunNonce = submission.RunNonce;
            LastArtifactId = submission.Artifact.ArtifactId;
            using (var content = new MemoryStream()) { submission.PdfContent.CopyTo(content); LastPdfBytes = content.ToArray(); }
            if (ThrowBeforeSubmit) throw new PrintSubmissionException("native backend rejected before StartDoc", mayHaveReachedOs: false);
            if (ThrowCapabilityLookupBeforeSubmit)
                _ = WindowsGdiNativeAdapter.GetCapabilitySnapshotBeforeStartDoc<PrinterCapabilitySnapshot>(
                    () => throw new IOException("queue disappeared during capability enumeration"));
            if (ThrowCancellationBeforeSubmit || submissionNumber == ThrowCancellationOnSubmissionNumber)
                throw new PrintSubmissionException("cancelled before StartDoc", mayHaveReachedOs: false,
                new OperationCanceledException(cancellationToken));
            Interlocked.Increment(ref _osSubmissionCount);
            if (ThrowCapabilityAfterSubmit) throw new PrintCapabilityException("capabilities changed inside submit");
            if (ThrowAfterSubmitSideEffect) throw new IOException("receipt channel interrupted after acceptance");
            var receipt = new OsJobReceipt(LastQueueIdentity, "job-1", submission.RunNonce, FixedTime,
                "Test Driver", "PORT1", submission.Options.CapabilityFingerprint);
            if (PendingSubmissionRelease is { } pendingSubmissionRelease)
            {
                SubmissionStarted.TrySetResult();
                await pendingSubmissionRelease.Task.ConfigureAwait(false);
            }
            Receipts.Add(receipt);
            return receipt;
        }

        public Task<PrintBackendJobStatus> GetStatusAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default)
        {
            LastQueueIdentity = receipt.QueueIdentity;
            LastJobId = receipt.OsJobId;
            LastRunNonce = receipt.RunNonce;
            if (PendingStatusResponse is { } pendingStatus)
            {
                StatusRequested.TrySetResult();
                return pendingStatus;
            }
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
        public Fixture(PreparedRun? runOverride = null, byte[]? finalPdf = null, int finalPageCount = 1)
        {
            Directory.CreateDirectory(_temporary);
            Store = new SqliteRunStore(Path.Combine(_temporary, "runs.db"));
            Store.Initialize();
            Artifacts = new PaperBatch.Core.Rendering.RenderArtifactStore(Path.Combine(_temporary, "artifacts"));
            var pdf = finalPdf ?? RendererTests.OnePagePdf();
            var storedArtifact = Artifacts.PersistPdfAsync(new MemoryStream(pdf), "test-renderer", "1", new Dictionary<string, string>()).GetAwaiter().GetResult();
            var finalArtifact = new FinalPdfArtifact(storedArtifact.Artifact.ArtifactId, storedArtifact.Artifact.Sha256,
                storedArtifact.Artifact.LengthBytes, finalPageCount);
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
