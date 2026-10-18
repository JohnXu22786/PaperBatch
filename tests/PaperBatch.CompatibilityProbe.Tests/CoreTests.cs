using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Ipc;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Recipes;
using PaperBatch.Core.Serialization;

internal static class CoreTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 10, 18, 12, 30, 0, TimeSpan.Zero);
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal static void PreparedRunInvariantsRejectInvalidHashesAndCopyCollections()
    {
        var item = CreateItem();
        var segment = CreateSegment(item.ItemId);
        var items = new List<PreparedItem> { item };
        var segments = new List<CompatibleSegment> { segment };
        var run = PreparedRun.Create("run-1", FixedTime, Hash, items, segments);

        items.Clear();
        segments.Clear();

        Assert.Equal(1, run.Items.Length);
        Assert.Equal(1, run.Segments.Length);
        Assert.Throws<ArgumentException>(() => PreparedRun.Create(
            "run-invalid", FixedTime, "not-a-sha256", [item], [CreateSegment(item.ItemId)]));
        Assert.Throws<ArgumentException>(() => PreparedRun.Create(
            "run-uncovered", FixedTime, Hash, [item], [CreateSegment("other-item")]));
        Assert.Throws<ArgumentException>(() => PreparedRun.Create(
            "run-invalid-enum", FixedTime, Hash,
            [item with { PrintSettings = item.PrintSettings with { ColorMode = (ColorMode)99 } }],
            [CreateSegment(item.ItemId)]));
        Assert.Throws<ArgumentException>(() => PreparedRun.Create(
            "run-incompatible-segment", FixedTime, Hash, [item],
            [CreateSegment(item.ItemId) with { PrintSettings = item.PrintSettings with { ColorMode = ColorMode.Monochrome } }]));
    }

    internal static void RunAggregationPreservesUncertaintyAndOsCompletion()
    {
        Assert.Equal(RunState.NeedsReviewUnknown, RunStateAggregator.Aggregate([JobState.NeedsReviewUnknown]));
        Assert.Equal(RunState.AcceptedByOs, RunStateAggregator.Aggregate([JobState.AcceptedByOs]));
        Assert.Equal(RunState.OsReportsComplete, RunStateAggregator.Aggregate([JobState.OsReportsComplete]));
        Assert.True(RunStateAggregator.Aggregate([JobState.OsReportsComplete]) != RunState.UserConfirmedPaperOutput);
        Assert.Equal(RunState.UserConfirmedPaperOutput, RunStateAggregator.Aggregate([JobState.UserConfirmedPaperOutput]));
        Assert.Equal("InProgress", RunStateAggregator.Aggregate([JobState.Ready, JobState.OsReportsComplete]).ToString());
        Assert.Equal(RunState.InProgress, RunStateAggregator.Aggregate([JobState.OsReportsComplete, JobState.UserConfirmedPaperOutput]));
        Assert.Equal(RunState.InProgress, RunStateAggregator.Aggregate([JobState.AcceptedByOs, JobState.OsReportsComplete]));
        Assert.Equal(RunState.InProgress, RunStateAggregator.Aggregate([JobState.NeedsReviewUnknown, JobState.Ready]));
    }

    internal static void SqliteMigrationsAreIdempotentAndPreserveV1Records()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "runs.db");
        CreateLegacyV1Database(databasePath);

        var store = new SqliteRunStore(databasePath);
        store.Initialize();
        store.Initialize();

        Assert.Equal(SqliteRunStore.CurrentSchemaVersion, ReadUserVersion(databasePath));
        Assert.Equal("legacy-run", store.GetRun("legacy-run")!.RunId);
        Assert.Equal("ready", store.GetRun("legacy-run")!.State.ToString().ToLowerInvariant());
        Assert.True(store.GetRun("legacy-run")!.Retention.RetainWhileUnresolved);
        Assert.Equal("ok", ReadScalar(databasePath, "PRAGMA quick_check;"));
    }

    internal static void SqliteRejectsFutureSchemaVersionsWithoutDowngrading()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "future.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<NotSupportedException>(() => new SqliteRunStore(databasePath).Initialize());
        Assert.Equal(999, ReadUserVersion(databasePath));
    }

    internal static void SqliteRejectsUndefinedPersistedEnumValues()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "corrupt-state.db");
        var store = new SqliteRunStore(databasePath);
        store.Initialize();
        var created = store.CreateRun(
            CreateRun("run-1", "segment-1"),
            "create-key",
            new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true));
        var jobId = created.Jobs[0].JobId;

        ExecuteSql(databasePath, "UPDATE runs SET state = '999' WHERE run_id = 'run-1';");
        Assert.Throws<InvalidDataException>(() => store.GetRun("run-1"));
        ExecuteSql(databasePath, $"UPDATE runs SET state = 'Ready' WHERE run_id = 'run-1'; UPDATE jobs SET state = '999' WHERE job_id = '{jobId}';");
        Assert.Throws<InvalidDataException>(() => store.GetJob(jobId));
    }

    internal static void RunCreationIsAtomicAndPersistsMetadataOnly()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "runs.db");
        var store = new SqliteRunStore(databasePath);
        store.Initialize();
        var first = CreateRun("run-1", "segment-shared");
        var retention = new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true);
        var created = store.CreateRun(first, "create-key-1", retention);
        var restored = store.GetPreparedRun(first.RunId);

        Assert.Equal(RunState.Ready, created.Run.State);
        Assert.Equal(FixedTime.AddDays(30), created.Run.Retention.ExpiresAtUtc);
        Assert.Equal(1, created.Jobs.Length);
        Assert.True(restored is not null);
        Assert.Equal(first.RunId, restored!.RunId);
        Assert.Equal(first.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(first.SnapshotFingerprint, restored.SnapshotFingerprint);
        Assert.Equal(first.Items[0].ItemId, restored.Items[0].ItemId);
        Assert.Equal(first.Items[0].Source, restored.Items[0].Source);
        Assert.Equal(first.Items[0].Render.RendererId, restored.Items[0].Render.RendererId);
        Assert.Equal(first.Items[0].Render.RendererVersion, restored.Items[0].Render.RendererVersion);
        Assert.Equal(first.Items[0].Render.ArtifactSha256, restored.Items[0].Render.ArtifactSha256);
        Assert.Equal(first.Items[0].Render.Capabilities.ContractVersion, restored.Items[0].Render.Capabilities.ContractVersion);
        Assert.True(first.Items[0].Render.Capabilities.SupportedMediaTypes.SequenceEqual(restored.Items[0].Render.Capabilities.SupportedMediaTypes));
        Assert.True(first.Items[0].PageMap.SequenceEqual(restored.Items[0].PageMap));
        Assert.Equal(first.Items[0].PrintSettings, restored.Items[0].PrintSettings);
        Assert.Equal(first.Segments[0].SegmentId, restored.Segments[0].SegmentId);
        Assert.Equal(first.Segments[0].PrintSettings, restored.Segments[0].PrintSettings);
        Assert.True(first.Segments[0].ItemIds.SequenceEqual(restored.Segments[0].ItemIds));
        Assert.True(!created.Run.PreparedRunJson.Contains("document-content-sentinel", StringComparison.Ordinal));
        Assert.True(!created.Run.PreparedRunJson.Contains("password-sentinel", StringComparison.Ordinal));
        var futureSchema = created.Run.PreparedRunJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":99", StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => PreparedRunJson.Parse(Encoding.UTF8.GetBytes(futureSchema)));

        var interrupted = CreateRunWithTwoSegments("run-2", "segment-first", "segment-second");
        ExecuteSql(databasePath, "CREATE TRIGGER reject_second_job BEFORE INSERT ON jobs WHEN NEW.run_id = 'run-2' AND NEW.segment_index = 1 BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        Assert.Throws<SqliteException>(() => store.CreateRun(interrupted, "create-key-2", retention));
        Assert.True(store.GetRun("run-2") is null);
        Assert.Equal(1, ReadCount(Path.Combine(temporary.Path, "runs.db"), "runs"));
        Assert.Equal(1, ReadCount(Path.Combine(temporary.Path, "runs.db"), "jobs"));
        ExecuteSql(databasePath, "DROP TRIGGER reject_second_job;");
    }

    internal static void PreparedRunSerializationMatchesGoldenFixture()
    {
        var preparedRun = CreateRun("golden-run", "golden-segment");
        var json = PreparedRunJson.Serialize(preparedRun);
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prepared-run-v1.json")).Trim();

        Assert.Equal(golden, json);
        Assert.Equal(golden, PreparedRunJson.Serialize(PreparedRunJson.Parse(Encoding.UTF8.GetBytes(golden))));
    }

    internal static void DistinctRunsCanReuseSegmentIds()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRunStore(Path.Combine(temporary.Path, "runs.db"));
        store.Initialize();
        var retention = new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true);
        var first = store.CreateRun(CreateRun("run-1", "segment-shared"), "create-key-1", retention);
        var second = store.CreateRun(CreateRun("run-2", "segment-shared"), "create-key-2", retention);

        Assert.True(first.Jobs[0].JobId != second.Jobs[0].JobId);
        Assert.Equal("run-1", store.GetJob(first.Jobs[0].JobId)!.RunId);
        Assert.Equal("run-2", store.GetJob(second.Jobs[0].JobId)!.RunId);
        store.BeginSubmission(first.Jobs[0].JobId, "submit-key-1", FixedTime.AddMinutes(1));
        Assert.Equal(JobState.Ready, store.GetJob(second.Jobs[0].JobId)!.State);
    }

    internal static void RunCreationIdempotencyReplaysMatchingRequests()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRunStore(Path.Combine(temporary.Path, "runs.db"));
        store.Initialize();
        var retention = new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true);
        var run = CreateRun("run-1", "segment-1");

        var first = store.CreateRun(run, "create-key", retention);
        var replay = store.CreateRun(run, "create-key", retention);

        Assert.True(!first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Run.RunId, replay.Run.RunId);
        Assert.Throws<IdempotencyConflictException>(() => store.CreateRun(
            CreateRun("different-run", "different-segment"), "create-key", retention));
    }

    internal static void SubmissionIntentIsDurableAndIdempotent()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRunStore(Path.Combine(temporary.Path, "runs.db"));
        store.Initialize();
        var created = store.CreateRun(
            CreateRun("run-1", "segment-1"),
            "create-key",
            new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true));
        var jobId = created.Jobs[0].JobId;

        var first = store.BeginSubmission(jobId, "submit-key", FixedTime.AddMinutes(1));
        var replay = store.BeginSubmission(jobId, "submit-key", FixedTime.AddMinutes(2));

        Assert.Equal(JobState.Submitting, first.Job.State);
        Assert.True(replay.Replayed);
        Assert.Equal(2, store.GetTransitions(jobId).Length);
        Assert.Equal(JobState.Submitting, store.GetJobs("run-1")[0].State);
        Assert.Equal(RunState.Submitting, store.GetRun("run-1")!.State);
        Assert.Throws<IdempotencyConflictException>(() => store.BeginSubmission(jobId, "other-key", FixedTime.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => store.BeginSubmission("missing-job", "submit-key", FixedTime));
    }

    internal static void IntentAndOsReceiptWritesRollBackAtomically()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "runs.db");
        var store = new SqliteRunStore(databasePath);
        store.Initialize();
        var created = store.CreateRun(
            CreateRun("run-1", "segment-1"),
            "create-key",
            new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true));
        var jobId = created.Jobs[0].JobId;

        ExecuteSql(databasePath, "CREATE TRIGGER reject_intent BEFORE INSERT ON job_transitions WHEN NEW.to_state = 'Submitting' BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        Assert.Throws<SqliteException>(() => store.BeginSubmission(jobId, "submit-key", FixedTime.AddMinutes(1)));
        var stillReady = store.GetJob(jobId)!;
        Assert.Equal(JobState.Ready, stillReady.State);
        Assert.True(stillReady.IdempotencyKey is null);
        Assert.Equal(1, store.GetTransitions(jobId).Length);
        ExecuteSql(databasePath, "DROP TRIGGER reject_intent;");

        store.BeginSubmission(jobId, "submit-key", FixedTime.AddMinutes(1));
        var receipt = new OsJobReceipt("queue-opaque-id", "job-17", "run-nonce", FixedTime.AddMinutes(2));
        ExecuteSql(databasePath, "CREATE TRIGGER reject_receipt BEFORE INSERT ON job_transitions WHEN NEW.to_state = 'AcceptedByOs' BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        Assert.Throws<SqliteException>(() => store.RecordOsReceipt(jobId, "submit-key", receipt, FixedTime.AddMinutes(2)));
        var stillSubmitting = store.GetJob(jobId)!;
        Assert.Equal(JobState.Submitting, stillSubmitting.State);
        Assert.True(stillSubmitting.OsReceipt is null);
        Assert.Equal(2, store.GetTransitions(jobId).Length);
        ExecuteSql(databasePath, "DROP TRIGGER reject_receipt;");
    }

    internal static void OsReceiptIsIdempotentAndNotPhysicalCompletion()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "runs.db");
        var store = new SqliteRunStore(databasePath);
        store.Initialize();
        var created = store.CreateRun(
            CreateRun("run-1", "segment-1"),
            "create-key",
            new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true));
        var jobId = created.Jobs[0].JobId;
        Assert.Throws<InvalidOperationException>(() => store.TransitionJob(jobId, JobState.Submitting, "bypass intent", FixedTime));
        store.BeginSubmission(jobId, "submit-key", FixedTime.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => store.TransitionJob(jobId, JobState.AcceptedByOs, "bypass receipt", FixedTime.AddMinutes(2)));
        var receipt = new OsJobReceipt("queue-opaque-id", "job-17", "run-nonce", FixedTime.AddMinutes(2));

        var accepted = store.RecordOsReceipt(jobId, "submit-key", receipt, FixedTime.AddMinutes(2));
        var replay = store.RecordOsReceipt(jobId, "submit-key", receipt, FixedTime.AddMinutes(3));

        Assert.Equal(JobState.AcceptedByOs, accepted.Job.State);
        Assert.True(replay.Replayed);
        Assert.Throws<IdempotencyConflictException>(() => store.RecordOsReceipt(
            jobId, "submit-key", receipt with { OsJobId = "job-other" }, FixedTime.AddMinutes(3)));
        Assert.Equal(RunState.AcceptedByOs, store.GetRun("run-1")!.State);
        Assert.True(accepted.Job.State != JobState.OsReportsComplete);
        Assert.True(accepted.Job.State != JobState.UserConfirmedPaperOutput);

        store.TransitionJob(jobId, JobState.OsProcessing, "status observed", FixedTime.AddMinutes(4));
        store.TransitionJob(jobId, JobState.OsReportsComplete, "OS reports complete", FixedTime.AddMinutes(5));
        Assert.Equal(RunState.OsReportsComplete, store.GetRun("run-1")!.State);
        Assert.True(store.RecordOsReceipt(jobId, "submit-key", receipt, FixedTime.AddMinutes(5)).Replayed);
        Assert.Throws<InvalidOperationException>(() => store.TransitionJob(jobId, JobState.Submitting, "retry", FixedTime.AddMinutes(6)));
        store.TransitionJob(jobId, JobState.UserConfirmedPaperOutput, "operator confirmed sheet", FixedTime.AddMinutes(7));
        Assert.Equal(RunState.UserConfirmedPaperOutput, store.GetRun("run-1")!.State);

        var uncertain = store.CreateRun(
            CreateRun("run-2", "segment-2"),
            "create-key-2",
            new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true));
        var uncertainJobId = uncertain.Jobs[0].JobId;
        store.BeginSubmission(uncertainJobId, "submit-key-2", FixedTime.AddMinutes(8));
        store.TransitionJob(uncertainJobId, JobState.NeedsReviewUnknown, "receipt missing after restart", FixedTime.AddMinutes(9));
        var recoveredReceipt = new OsJobReceipt("queue-opaque-id", "job-recovered", "run-nonce-2", FixedTime.AddMinutes(8));
        ExecuteSql(databasePath, "CREATE TRIGGER reject_reconciled_receipt BEFORE INSERT ON job_transitions WHEN NEW.to_state = 'AcceptedByOs' AND NEW.reason LIKE 'OS receipt recovered%' BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        Assert.Throws<SqliteException>(() => store.RecordOsReceipt(uncertainJobId, "submit-key-2", recoveredReceipt, FixedTime.AddMinutes(10)));
        var stillUnknown = store.GetJob(uncertainJobId)!;
        Assert.Equal(JobState.NeedsReviewUnknown, stillUnknown.State);
        Assert.True(stillUnknown.OsReceipt is null);
        Assert.Equal(3, store.GetTransitions(uncertainJobId).Length);
        ExecuteSql(databasePath, "DROP TRIGGER reject_reconciled_receipt;");

        var reconciled = store.RecordOsReceipt(uncertainJobId, "submit-key-2", recoveredReceipt, FixedTime.AddMinutes(10));
        Assert.Equal(JobState.AcceptedByOs, reconciled.Job.State);
        Assert.Equal(recoveredReceipt, reconciled.Job.OsReceipt);
        Assert.Equal(JobState.NeedsReviewUnknown, store.GetTransitions(uncertainJobId)[^1].FromState);
        Assert.True(store.RecordOsReceipt(uncertainJobId, "submit-key-2", recoveredReceipt, FixedTime.AddMinutes(11)).Replayed);
        Assert.Equal(RunState.AcceptedByOs, store.GetRun("run-2")!.State);
    }

    internal static void UnknownOutcomeCanStillRecordUserConfirmedPaperOutput()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRunStore(Path.Combine(temporary.Path, "runs.db"));
        store.Initialize();
        var created = store.CreateRun(
            CreateRun("run-1", "segment-1"),
            "create-key",
            new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true));
        var jobId = created.Jobs[0].JobId;
        store.BeginSubmission(jobId, "submit-key", FixedTime.AddMinutes(1));
        store.TransitionJob(jobId, JobState.NeedsReviewUnknown, "OS outcome unavailable", FixedTime.AddMinutes(2));

        var confirmed = store.TransitionJob(jobId, JobState.UserConfirmedPaperOutput, "operator verified printed pages", FixedTime.AddMinutes(3));

        Assert.Equal(JobState.UserConfirmedPaperOutput, confirmed.State);
        Assert.Equal(RunState.UserConfirmedPaperOutput, store.GetRun("run-1")!.State);
        var transition = store.GetTransitions(jobId)[^1];
        Assert.Equal(JobState.NeedsReviewUnknown, transition.FromState);
        Assert.Equal(JobState.UserConfirmedPaperOutput, transition.ToState);
    }

    internal static void RecipeParserRejectsInvalidVersionsAndUnknownFields()
    {
        var futureVersion = Encoding.UTF8.GetBytes(ValidRecipeJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        Assert.Throws<UnsupportedSchemaVersionException>(() => JobRecipeJson.Parse(futureVersion));
        Assert.Throws<UnsupportedSchemaVersionException>(() => JobRecipeJson.Parse(Encoding.UTF8.GetBytes("{\"recipeId\":\"no-version\"}")));
        Assert.Throws<JsonException>(() => JobRecipeJson.Parse(Encoding.UTF8.GetBytes(ValidRecipeJson().Replace("\"name\": \"Office packet\"", "\"name\": \"Office packet\", \"script\": \"run\"", StringComparison.Ordinal))));
        Assert.Throws<JsonException>(() => JobRecipeJson.Parse(Encoding.UTF8.GetBytes(ValidRecipeJson().Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal))));
        Assert.Throws<JsonException>(() => JobRecipeJson.Parse(Encoding.UTF8.GetBytes("{ invalid }")));
        Assert.Throws<InvalidDataException>(() => JobRecipeJson.Parse(new byte[JobRecipeJson.MaximumRecipeBytes + 1]));
        foreach (var requiredSetting in new[] { "colorMode", "duplexMode", "orientation", "scalingMode" })
        {
            var incomplete = JsonNode.Parse(ValidRecipeJson())!.AsObject();
            incomplete["settings"]!.AsObject().Remove(requiredSetting);
            Assert.Throws<JsonException>(() => JobRecipeJson.Parse(Encoding.UTF8.GetBytes(incomplete.ToJsonString())));
        }

        var oversized = CreateRecipe() with
        {
            Sources = Enumerable.Range(0, 500).Select(index => new RecipeSource($"source-{index}", new string('x', 2048))).ToImmutableArray()
        };
        Assert.Throws<InvalidDataException>(() => JobRecipeJson.Serialize(oversized));
    }

    internal static void RecipeSerializationMatchesGoldenFixture()
    {
        var json = JobRecipeJson.Serialize(CreateRecipe());
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recipe-v1.json")).Trim();

        Assert.Equal(golden, json);
        var parsed = JobRecipeJson.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("office-packet", parsed.RecipeId);
        Assert.Equal(2, parsed.Sources.Length);
        Assert.Equal(JobRecipeJson.CurrentSchemaVersion, parsed.SchemaVersion);
    }

    internal static void IpcEnvelopesPreserveCorrelationAndNegotiateCapabilities()
    {
        using var payloadDocument = JsonDocument.Parse("{\"runId\":\"run-1\"}");
        var correlation = Guid.Parse("12345678-1234-5678-1234-567812345678");
        var request = new IpcRequestEnvelope(1, correlation, "run.prepare", "request-key", payloadDocument.RootElement.Clone());

        var bytes = IpcJson.SerializeRequest(request);
        var parsed = IpcJson.ParseRequest(bytes);
        var requestGolden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ipc-request-v1.json")).Trim();
        Assert.Equal(requestGolden, Encoding.UTF8.GetString(bytes));
        var negotiated = IpcCapabilities.Negotiate([1, 2], [1], 4096, 1024);

        Assert.Equal(correlation, parsed.CorrelationId);
        Assert.Equal("request-key", parsed.IdempotencyKey!);
        Assert.Equal(1, negotiated.ProtocolVersion);
        Assert.Equal(1024, negotiated.MaximumMessageBytes);
        Assert.Equal("run-1", parsed.Payload.GetProperty("runId").GetString()!);
        Assert.Equal(IpcJson.MaximumMessageBytes, IpcCapabilities.Negotiate(
            [1], [1], IpcJson.MaximumMessageBytes + 100, IpcJson.MaximumMessageBytes + 200).MaximumMessageBytes);
        Assert.Throws<NotSupportedException>(() => IpcCapabilities.Negotiate([2], [1], 4096, 1024));
        Assert.Throws<NotSupportedException>(() => IpcJson.ParseRequest(Encoding.UTF8.GetBytes(
            "{\"protocolVersion\":2,\"correlationId\":\"12345678-1234-5678-1234-567812345678\",\"operation\":\"ping\",\"idempotencyKey\":null,\"payload\":{}}")));
    }

    internal static void IpcRejectsMalformedAndOversizedMessages()
    {
        Assert.Throws<JsonException>(() => IpcJson.ParseRequest(Encoding.UTF8.GetBytes("{ broken }")));
        Assert.Throws<JsonException>(() => IpcJson.ParseRequest(Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"correlationId\":\"12345678-1234-5678-1234-567812345678\",\"operation\":\"ping\",\"idempotencyKey\":null,\"payload\":{},\"extra\":true}")));
        Assert.Throws<JsonException>(() => IpcJson.ParseRequest(Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"protocolVersion\":1,\"correlationId\":\"12345678-1234-5678-1234-567812345678\",\"operation\":\"ping\",\"idempotencyKey\":null,\"payload\":{}}")));
        Assert.Throws<InvalidDataException>(() => IpcJson.ParseRequest(new byte[IpcJson.MaximumMessageBytes + 1]));

        var correlationId = Guid.Parse("12345678-1234-5678-1234-567812345678");
        using var duplicatePayloadDocument = JsonDocument.Parse("{\"runId\":\"run-1\",\"runId\":\"run-2\"}");
        var duplicateRequest = new IpcRequestEnvelope(1, correlationId, "run.prepare", "duplicate-payload-key", duplicatePayloadDocument.RootElement.Clone());
        var duplicateResponse = new IpcResponseEnvelope(1, correlationId, duplicatePayloadDocument.RootElement.Clone(), null);
        Assert.Throws<JsonException>(() => IpcJson.SerializeRequest(duplicateRequest));
        Assert.Throws<JsonException>(() => IpcJson.SerializeResponse(duplicateResponse));

        var goldenError = IpcJson.SerializeResponse(IpcJson.CreateErrorResponse(correlationId, "invalid-request", "Malformed envelope."));
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ipc-error-v1.json")).Trim();
        Assert.Equal(golden, Encoding.UTF8.GetString(goldenError));

        var error = IpcJson.CreateErrorResponse(correlationId, "invalid-request", new string('x', 10000));
        var bytes = IpcJson.SerializeResponse(error);
        Assert.True(bytes.Length <= IpcJson.MaximumMessageBytes);
        Assert.True(error.Error!.Message.Length <= IpcJson.MaximumErrorMessageLength);
    }

    private static PreparedItem CreateItem() => new(
        "item-1",
        new SourceSnapshot("snapshot-1", Hash, 512, FixedTime),
        new RendererResult("renderer-neutral", "1.0", "artifact-1", Hash, 1024, 2, new RendererCapabilities(1, ["application/pdf"])),
        [new PageMapping(1, 1), new PageMapping(2, 2)],
        new PrintSettings(1, ColorMode.Color, DuplexMode.LongEdge, "default", Orientation.Auto, ScalingMode.ShrinkToFit));

    private static CompatibleSegment CreateSegment(string itemId) => new(
        "segment-1", 0, [itemId], "compat-v1", new PrintSettings(1, ColorMode.Color, DuplexMode.LongEdge, "default", Orientation.Auto, ScalingMode.ShrinkToFit));

    private static PreparedRun CreateRun(string runId, string segmentId) => PreparedRun.Create(
        runId,
        FixedTime,
        Hash,
        [CreateItem() with { ItemId = "item-1" }],
        [CreateSegment("item-1") with { SegmentId = segmentId }]);

    private static PreparedRun CreateRunWithTwoSegments(string runId, string firstSegmentId, string secondSegmentId)
    {
        var firstItem = CreateItem() with { ItemId = "item-1" };
        var secondItem = CreateItem() with { ItemId = "item-2" };
        var firstSegment = CreateSegment(firstItem.ItemId) with { SegmentId = firstSegmentId, Index = 0 };
        var secondSegment = CreateSegment(secondItem.ItemId) with { SegmentId = secondSegmentId, Index = 1 };
        return PreparedRun.Create(runId, FixedTime, Hash, [firstItem, secondItem], [firstSegment, secondSegment]);
    }

    private static JobRecipe CreateRecipe() => new(
        JobRecipeJson.CurrentSchemaVersion,
        "office-packet",
        "Office packet",
        [new RecipeSource("cover", "./cover.pdf"), new RecipeSource("body", "./report.docx")],
        new RecipePrintSettings(1, ColorMode.Color, DuplexMode.LongEdge, Orientation.Auto, ScalingMode.ShrinkToFit, "default"));

    private static string ValidRecipeJson() => JobRecipeJson.Serialize(CreateRecipe());

    private static void CreateLegacyV1Database(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE runs (
                run_id TEXT PRIMARY KEY,
                request_key TEXT NOT NULL UNIQUE,
                created_utc TEXT NOT NULL,
                state TEXT NOT NULL,
                prepared_run_json TEXT NOT NULL,
                retention_expires_utc TEXT NOT NULL
            );
            CREATE TABLE jobs (
                job_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL REFERENCES runs(run_id),
                segment_index INTEGER NOT NULL,
                state TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                os_receipt_json TEXT NULL
            );
            CREATE TABLE job_transitions (
                transition_id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL REFERENCES jobs(job_id),
                from_state TEXT NULL,
                to_state TEXT NOT NULL,
                at_utc TEXT NOT NULL,
                idempotency_key TEXT NULL,
                reason TEXT NULL
            );
            INSERT INTO runs (run_id, request_key, created_utc, state, prepared_run_json, retention_expires_utc)
            VALUES ('legacy-run', 'legacy-key', '2026-10-17T12:00:00.0000000+00:00', 'Ready', '{}', '2026-11-17T12:00:00.0000000+00:00');
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    }

    private static int ReadUserVersion(string path) => Convert.ToInt32(ReadScalar(path, "PRAGMA user_version;"));

    private static string ReadScalar(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static void ExecuteSql(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int ReadCount(string path, string table) => int.Parse(ReadScalar(path, $"SELECT COUNT(*) FROM {table};"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "paperbatch-u1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
