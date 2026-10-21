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
        Assert.Throws<ArgumentException>(() => PreparedRun.Create(
            "run-null-segment", FixedTime, Hash, [item], [null!]));
    }

    internal static void PreparedRunValidatesRendererMediaTypeSyntax()
    {
        var item = CreateItem();
        var validMediaTypes = new[] { "application/pdf", "text/plain", "application/vnd.example+json" };
        foreach (var mediaType in validMediaTypes)
        {
            var validItem = item with
            {
                Render = item.Render with
                {
                    Capabilities = new RendererCapabilities(1, [mediaType])
                }
            };
            PreparedRun.Create("run-valid-media-type", FixedTime, Hash, [validItem], [CreateSegment(item.ItemId)]);
        }

        var invalidMediaTypes = new[]
        {
            "/",
            "/pdf",
            "application/",
            "application//pdf",
            "application/pdf; charset=utf-8",
            "application/pd f",
            "application/pdf\ninvalid",
            "application/pdf\rinvalid"
        };
        foreach (var mediaType in invalidMediaTypes)
        {
            var invalidItem = item with
            {
                Render = item.Render with
                {
                    Capabilities = new RendererCapabilities(1, [mediaType])
                }
            };
            Assert.Throws<ArgumentException>(() => PreparedRun.Create(
                "run-invalid-media-type", FixedTime, Hash, [invalidItem], [CreateSegment(item.ItemId)]));
        }
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
        Assert.Throws<ArgumentOutOfRangeException>(() => RunStateAggregator.Aggregate([JobState.Ready, (JobState)999]));
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

    internal static void SqliteHistoryAndRecipesPersist()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "workspace.db");
        var store = new SqliteRunStore(databasePath);
        store.Initialize();
        var retention = new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true);
        store.CreateRun(CreateRun("a-history-older", "segment-older"), "history-key-1", retention);
        var newerRunParts = CreateRun("z-history-newer", "segment-newer");
        store.CreateRun(PreparedRun.Create("z-history-newer", FixedTime.AddMinutes(1), Hash,
            newerRunParts.Items, newerRunParts.Segments), "history-key-2", retention);
        Assert.Equal("z-history-newer", store.GetRecentRuns().First().RunId);
        Assert.Equal(1, store.GetRecentRuns(1).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.GetRecentRuns(0));

        var recipe = new JobRecipe(1, "recipe-1", "Office packet", [new RecipeSource("source-1", "/docs/a.pdf")],
            new RecipePrintSettings(2, ColorMode.Color, DuplexMode.Simplex, Orientation.Auto, ScalingMode.ShrinkToFit, "Letter"));
        store.SaveRecipe(recipe);
        var reopened = new SqliteRunStore(databasePath);
        reopened.Initialize();
        var restoredRecipe = reopened.GetRecipes().Single().Recipe;
        Assert.Equal(recipe.Name, restoredRecipe.Name);
        Assert.Equal(recipe.Sources[0], restoredRecipe.Sources[0]);
        Assert.Equal(recipe.Settings, restoredRecipe.Settings);
        Assert.Throws<UnsupportedSchemaVersionException>(() => reopened.SaveRecipe(recipe with { SchemaVersion = 3 }));
        var oversized = recipe with { RecipeId = "large", Name = "Large", Sources =
            Enumerable.Range(0, 500).Select(index => new RecipeSource($"source-{index}", "/" + new string('a', 2047))).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => reopened.SaveRecipe(oversized));
        ExecuteSql(databasePath, "UPDATE recipes SET recipe_json = '{\"schemaVersion\":999}' WHERE recipe_id = 'recipe-1';");
        Assert.Throws<UnsupportedSchemaVersionException>(() => reopened.GetRecipes());
        reopened.SaveRecipe(recipe);
        Assert.True(reopened.DeleteRecipe(recipe.RecipeId));
        Assert.True(reopened.GetRecipes().IsEmpty);
        Assert.Equal("ok", ReadScalar(databasePath, "PRAGMA quick_check;"));

        using var boundedDirectory = new TemporaryDirectory();
        var boundedPath = Path.Combine(boundedDirectory.Path, "bounded.db");
        var boundedStore = new SqliteRunStore(boundedPath);
        boundedStore.Initialize();
        ExecuteSql(boundedPath, """
            WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < 1000)
            INSERT INTO runs (run_id, request_key, created_utc, state, prepared_run_json, retention_expires_utc, retain_while_unresolved)
            SELECT 'seed-' || value, 'request-' || value, '2026-10-18T12:30:00.0000000+00:00', 'Ready', '{}',
                   '2030-01-01T00:00:00.0000000+00:00', 0 FROM n;
            """);
        Assert.Throws<InvalidOperationException>(() => boundedStore.CreateRun(CreateRun("over-limit", "over-limit-segment"),
            "over-limit-request", new RetentionMetadata(FixedTime.AddDays(30), true)));
    }

    internal static void SqliteRejectsFutureSchemaVersionsWithoutDowngrading()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "future.db");
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
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
        // This persistence boundary accepts metadata only; guard its stored JSON schema, not renderer or password handling.
        var storedRun = store.GetRun(first.RunId);
        Assert.True(storedRun is not null);
        using var persistedMetadata = JsonDocument.Parse(storedRun!.PreparedRunJson);
        var persistedRoot = persistedMetadata.RootElement;
        AssertJsonObjectProperties(persistedRoot, "schemaVersion", "run");

        var persistedRun = persistedRoot.GetProperty("run");
        AssertJsonObjectProperties(persistedRun, "runId", "createdAtUtc", "snapshotFingerprint", "items", "segments");
        foreach (var item in persistedRun.GetProperty("items").EnumerateArray())
        {
            AssertJsonObjectProperties(item, "itemId", "source", "render", "pageMap", "printSettings");
            AssertJsonObjectProperties(item.GetProperty("source"), "snapshotId", "sha256", "lengthBytes", "capturedAtUtc");

            var render = item.GetProperty("render");
            AssertJsonObjectProperties(render, "rendererId", "rendererVersion", "artifactId", "artifactSha256", "artifactLengthBytes", "pageCount", "capabilities");
            AssertJsonObjectProperties(render.GetProperty("capabilities"), "contractVersion", "supportedMediaTypes");

            foreach (var pageMapping in item.GetProperty("pageMap").EnumerateArray())
            {
                AssertJsonObjectProperties(pageMapping, "sourcePageNumber", "renderedPageNumber");
            }

            AssertJsonObjectProperties(item.GetProperty("printSettings"), "copies", "colorMode", "duplexMode", "media", "orientation", "scalingMode", "printerId", "trayId");
        }

        foreach (var segment in persistedRun.GetProperty("segments").EnumerateArray())
        {
            AssertJsonObjectProperties(segment, "segmentId", "index", "itemIds", "compatibilityKey", "printSettings");
            AssertJsonObjectProperties(segment.GetProperty("printSettings"), "copies", "colorMode", "duplexMode", "media", "orientation", "scalingMode", "printerId", "trayId");
        }

        var futureSchema = storedRun.PreparedRunJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":99", StringComparison.Ordinal);
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

    internal static void PreparedRunV2PersistsFinalArtifactAndReadsLegacyV1()
    {
        var legacy = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prepared-run-v1.json")).Trim();
        var legacyRun = PreparedRunJson.Parse(Encoding.UTF8.GetBytes(legacy));
        Assert.True(legacyRun.FinalArtifact is null);
        Assert.Equal(legacy, PreparedRunJson.Serialize(legacyRun));

        var artifact = new FinalPdfArtifact("artifact_" + Hash, Hash, 2048, 4);
        var run = PreparedRun.Create("run-with-final", FixedTime, Hash,
            [CreateItem() with { ItemId = "item-1" }], [CreateSegment("item-1")], artifact);
        var json = PreparedRunJson.Serialize(run);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(2, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
        var restored = PreparedRunJson.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(artifact, restored.FinalArtifact);
        Assert.Equal(json, PreparedRunJson.Serialize(restored));

        Assert.Throws<ArgumentException>(() => PreparedRun.Create("run-invalid-final", FixedTime, Hash,
            [CreateItem() with { ItemId = "item-1" }], [CreateSegment("item-1")],
            artifact with { Sha256 = "not-a-hash" }));
    }

    internal static void PreparedRunJsonRejectsNullArrayEntries()
    {
        var json = PreparedRunJson.Serialize(CreateRun("null-entry-run", "null-entry-segment"));
        var withNullItem = JsonNode.Parse(json)!.AsObject();
        withNullItem["run"]!["items"]!.AsArray().Insert(0, null);
        Assert.Throws<JsonException>(() => PreparedRunJson.Parse(Encoding.UTF8.GetBytes(withNullItem.ToJsonString())));

        var withNullSegment = JsonNode.Parse(json)!.AsObject();
        withNullSegment["run"]!["segments"]!.AsArray().Insert(0, null);
        Assert.Throws<JsonException>(() => PreparedRunJson.Parse(Encoding.UTF8.GetBytes(withNullSegment.ToJsonString())));
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

    internal static void ExpiredRunsArePrunedOnlyWhenRetentionAllowsIt()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "runs.db");
        var store = new SqliteRunStore(databasePath);
        store.Initialize();
        var expiredRetention = new RetentionMetadata(FixedTime.AddDays(1), RetainWhileUnresolved: true);
        var unresolved = store.CreateRun(CreateRun("unresolved", "segment-unresolved"), "create-unresolved", expiredRetention);
        store.BeginSubmission(unresolved.Jobs[0].JobId, "submit-unresolved", FixedTime.AddMinutes(1));
        store.TransitionJob(unresolved.Jobs[0].JobId, JobState.NeedsReviewUnknown, "unknown", FixedTime.AddMinutes(2));

        var resolved = store.CreateRun(CreateRun("resolved", "segment-resolved"), "create-resolved", expiredRetention);
        store.BeginSubmission(resolved.Jobs[0].JobId, "submit-resolved", FixedTime.AddMinutes(1));
        store.TransitionJob(resolved.Jobs[0].JobId, JobState.FailedBeforeSubmit, "known pre-submit failure", FixedTime.AddMinutes(2));

        var explicitlyUnretained = store.CreateRun(
            CreateRun("unretained", "segment-unretained"),
            "create-unretained",
            expiredRetention with { RetainWhileUnresolved = false });
        store.BeginSubmission(explicitlyUnretained.Jobs[0].JobId, "submit-unretained", FixedTime.AddMinutes(1));
        store.TransitionJob(explicitlyUnretained.Jobs[0].JobId, JobState.NeedsReviewUnknown, "unknown", FixedTime.AddMinutes(2));

        store.CreateRun(
            CreateRun("future", "segment-future"),
            "create-future",
            new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true));

        ExecuteSql(databasePath, "CREATE TRIGGER reject_retention_delete BEFORE DELETE ON runs WHEN OLD.run_id = 'unretained' BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        Assert.Throws<SqliteException>(() => store.DeleteExpiredRuns(FixedTime.AddDays(2)));
        Assert.True(store.GetRun("resolved") is not null);
        Assert.True(store.GetRun("unretained") is not null);
        ExecuteSql(databasePath, "DROP TRIGGER reject_retention_delete;");

        var firstDeleted = store.DeleteExpiredRuns(FixedTime.AddDays(2));

        Assert.True(firstDeleted.ToHashSet(StringComparer.Ordinal).SetEquals(["resolved", "unretained"]));
        Assert.True(store.GetRun("resolved") is null);
        Assert.True(store.GetRun("unretained") is null);
        Assert.True(store.GetRun("unresolved") is not null);
        Assert.True(store.GetRun("future") is not null);

        store.TransitionJob(unresolved.Jobs[0].JobId, JobState.UserConfirmedPaperOutput, "operator verified output", FixedTime.AddDays(3));
        Assert.True(store.DeleteExpiredRuns(FixedTime.AddDays(3)).SequenceEqual(["unresolved"]));
        Assert.True(store.GetRun("unresolved") is null);
        Assert.True(store.GetRun("future") is not null);
        Assert.Equal(JobState.Ready, store.GetJobs("future")[0].State);
    }

    internal static void StoreInitializationPrunesExpiredRuns()
    {
        using var temporary = new TemporaryDirectory();
        var databasePath = Path.Combine(temporary.Path, "runs.db");
        var initialStore = new SqliteRunStore(databasePath);
        initialStore.Initialize();
        var now = DateTimeOffset.UtcNow;
        var preparedRun = PreparedRun.Create(
            "expired-on-restart",
            now.AddDays(-2),
            Hash,
            [CreateItem() with { ItemId = "item-1" }],
            [CreateSegment("item-1") with { SegmentId = "segment-expired" }]);
        initialStore.CreateRun(
            preparedRun,
            "create-expired",
            new RetentionMetadata(now.AddDays(-1), RetainWhileUnresolved: true));

        var reopenedStore = new SqliteRunStore(databasePath);
        reopenedStore.Initialize();

        Assert.True(reopenedStore.GetRun("expired-on-restart") is null);
    }

    internal static void RunCreationIdempotencyReplaysMatchingRequests()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SqliteRunStore(Path.Combine(temporary.Path, "runs.db"));
        store.Initialize();
        var retention = new RetentionMetadata(FixedTime.AddDays(30), RetainWhileUnresolved: true);
        var run = CreateRun("run-1", "segment-1");
        var equivalentOffsetRun = PreparedRun.Create(
            run.RunId,
            run.CreatedAtUtc,
            run.SnapshotFingerprint,
            [run.Items[0] with
            {
                Source = run.Items[0].Source with
                {
                    CapturedAtUtc = run.Items[0].Source.CapturedAtUtc.ToOffset(TimeSpan.FromHours(2))
                }
            }],
            run.Segments);
        var equivalentHashCaseRun = PreparedRun.Create(
            run.RunId,
            run.CreatedAtUtc,
            run.SnapshotFingerprint,
            [run.Items[0] with
            {
                Source = run.Items[0].Source with { Sha256 = run.Items[0].Source.Sha256.ToUpperInvariant() },
                Render = run.Items[0].Render with { ArtifactSha256 = run.Items[0].Render.ArtifactSha256.ToUpperInvariant() }
            }],
            run.Segments);

        var first = store.CreateRun(run, "create-key", retention);
        var replay = store.CreateRun(equivalentOffsetRun, "create-key", retention);
        var hashCaseReplay = store.CreateRun(equivalentHashCaseRun, "create-key", retention);
        var exactReplay = store.CreateRun(run, "create-key", retention);

        Assert.True(!first.Replayed);
        Assert.True(replay.Replayed);
        Assert.True(hashCaseReplay.Replayed);
        Assert.True(exactReplay.Replayed);
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
        var equivalentOffsetReplay = store.RecordOsReceipt(
            jobId,
            "submit-key",
            receipt with { SubmittedAtUtc = receipt.SubmittedAtUtc.ToOffset(TimeSpan.FromHours(2)) },
            FixedTime.AddMinutes(3));

        Assert.Equal(JobState.AcceptedByOs, accepted.Job.State);
        Assert.True(replay.Replayed);
        Assert.True(equivalentOffsetReplay.Replayed);
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
        var futureVersion = Encoding.UTF8.GetBytes(ValidRecipeJson().Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3", StringComparison.Ordinal));
        Assert.Throws<UnsupportedSchemaVersionException>(() => JobRecipeJson.Parse(futureVersion));
        Assert.Throws<UnsupportedSchemaVersionException>(() => JobRecipeJson.Parse(Encoding.UTF8.GetBytes("{\"recipeId\":\"no-version\"}")));
        Assert.Throws<JsonException>(() => JobRecipeJson.Parse(Encoding.UTF8.GetBytes(ValidRecipeJson().Replace("\"name\": \"Office packet\"", "\"name\": \"Office packet\", \"script\": \"run\"", StringComparison.Ordinal))));
        Assert.Throws<JsonException>(() => JobRecipeJson.Parse(Encoding.UTF8.GetBytes(ValidRecipeJson().Replace("\"schemaVersion\": 2,", "\"schemaVersion\": 2, \"schemaVersion\": 2,", StringComparison.Ordinal))));
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
        var json = JobRecipeJson.Serialize(CreateRecipe() with { SchemaVersion = 1 });
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recipe-v1.json")).Trim();

        Assert.Equal(golden, json);
        var parsed = JobRecipeJson.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("office-packet", parsed.RecipeId);
        Assert.Equal(2, parsed.Sources.Length);
        Assert.Equal(1, parsed.SchemaVersion);
    }

    internal static void RecipeRulesUseFirstMatchThenPerFileOverrideAndRoundTrip()
    {
        var source = new RecipeSource("source-1", "/docs/annual.pdf")
        {
            Override = new RecipeSettingsOverride(Copies: 4, PageRange: "2-3")
        };
        var recipe = CreateRecipe() with
        {
            SchemaVersion = JobRecipeJson.CurrentSchemaVersion,
            Sources = [source],
            Rules = [
                new RecipeSettingRule("first", "*.pdf", "pdf", new RecipeSettingsOverride(Copies: 2, NUp: 2)),
                new RecipeSettingRule("second", "annual.*", null, new RecipeSettingsOverride(Copies: 9))
            ]
        };
        var json = JobRecipeJson.Serialize(recipe);
        var parsed = JobRecipeJson.Parse(Encoding.UTF8.GetBytes(json));
        var resolved = RecipeSettingsResolver.Resolve(parsed, parsed.Sources[0], "pdf", parsed.Settings);
        Assert.Equal("first", resolved.MatchingRuleId!);
        Assert.Equal(4, resolved.Values.Copies);
        Assert.Equal(2, resolved.NUp!.Value);
        Assert.Equal("2-3", resolved.PageRange!);
        Assert.Equal("file override", resolved.Provenance["copies"]);
        Assert.Equal("rule first", resolved.Provenance["nUp"]);
        Assert.True(RecipeSettingsResolver.IsMatch("Annual.PDF", "*.pdf"));
        Assert.False(RecipeSettingsResolver.IsMatch("dir/Annual.PDF", "*.pdf"));
        Assert.Throws<JsonException>(() => JobRecipeJson.Serialize(recipe with
        {
            Rules = [new RecipeSettingRule("bad", "../*.pdf", null, new RecipeSettingsOverride(Copies: 2))]
        }));
    }

    internal static void AutoStartingTransportLaunchesAndExitsCoordinatorProcess()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "PaperBatch-process-host-" + Guid.NewGuid().ToString("N"));
        var previousEnabled = Environment.GetEnvironmentVariable("PAPERBATCH_TEST_COORDINATOR_HOST");
        var previousRoot = Environment.GetEnvironmentVariable("PAPERBATCH_TEST_COORDINATOR_ROOT");
        Environment.SetEnvironmentVariable("PAPERBATCH_TEST_COORDINATOR_HOST", "1");
        Environment.SetEnvironmentVariable("PAPERBATCH_TEST_COORDINATOR_ROOT", dataRoot);
        try
        {
            var transport = new AutoStartingCoordinatorTransport(new NamedPipeLocalCoordinatorTransport(
                NamedPipeLocalCoordinatorTransport.CreateCurrentUserPipeName(), connectTimeout: TimeSpan.FromMilliseconds(200)));
            var request = new IpcRequestEnvelope(1, Guid.NewGuid(), "runtime.status", null, JsonDocument.Parse("{}").RootElement.Clone());
            var response = transport.SendAsync(request, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.True(response.Error is null);
            Assert.Equal(true, response.Payload!.Value.GetProperty("dryRun").GetBoolean());
            var unavailable = new NamedPipeLocalCoordinatorTransport(NamedPipeLocalCoordinatorTransport.CreateCurrentUserPipeName(),
                connectTimeout: TimeSpan.FromMilliseconds(150));
            var exited = false;
            for (var attempt = 0; attempt < 80 && !exited; attempt++)
            {
                Thread.Sleep(100);
                try { _ = unavailable.SendAsync(request).AsTask().GetAwaiter().GetResult(); }
                catch (Exception exception) when (exception is NotSupportedException or IOException) { exited = true; }
            }
            Assert.True(exited);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAPERBATCH_TEST_COORDINATOR_HOST", previousEnabled);
            Environment.SetEnvironmentVariable("PAPERBATCH_TEST_COORDINATOR_ROOT", previousRoot);
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    internal static void CoordinatorHostServesProductionStatusAndUnavailableOperations()
    {
        var appName = "PaperBatch-integration-" + Guid.NewGuid().ToString("N");
        var dataRoot = Path.Combine(Path.GetTempPath(), appName);
        Directory.CreateDirectory(dataRoot);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new ProductionCoordinatorRequestHandler(dataRoot);
        var host = new CoordinatorRuntimeHost(appName, handler);
        Assert.True(host.IsOwner);
        var duplicate = new CoordinatorRuntimeHost(appName, new UnavailableCoordinatorRequestHandler());
        Assert.True(!duplicate.IsOwner);
        duplicate.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var run = host.RunAsync(stop.Token);
        Thread.Sleep(50);
        var transport = new NamedPipeLocalCoordinatorTransport(NamedPipeLocalCoordinatorTransport.CreateCurrentUserPipeName(appName));
        var statusRequest = new IpcRequestEnvelope(1, Guid.NewGuid(), "runtime.status", null, JsonDocument.Parse("{}").RootElement.Clone());
        var status = transport.SendAsync(statusRequest, stop.Token).AsTask().GetAwaiter().GetResult();
        Assert.True(status.Error is null);
        Assert.Equal(false, status.Payload!.Value.GetProperty("prepare").GetBoolean());
        Assert.Equal(false, status.Payload.Value.GetProperty("submit").GetBoolean());
        var recipeDirectory = Path.Combine(dataRoot, "recipes");
        Directory.CreateDirectory(recipeDirectory);
        File.WriteAllBytes(Path.Combine(recipeDirectory, "source.pdf"), [0x25, 0x50, 0x44, 0x46]);
        var recipePath = Path.Combine(recipeDirectory, "relative-source.json");
        File.WriteAllText(recipePath, """{"schemaVersion":1,"recipeId":"relative-source","name":"Relative source","sources":[{"sourceId":"source","location":"./source.pdf"}],"settings":{"copies":1,"colorMode":"color","duplexMode":"longEdge","orientation":"auto","scalingMode":"shrinkToFit","media":"default","printerId":null,"trayId":null}}""");
        var dryRunRequest = new IpcRequestEnvelope(1, Guid.NewGuid(), "cli.dry-run", null,
            JsonSerializer.SerializeToElement(new { arguments = new[] { recipePath } }));
        var dryRun = transport.SendAsync(dryRunRequest, stop.Token).AsTask().GetAwaiter().GetResult();
        Assert.True(dryRun.Error is null);
        Assert.Equal("enumerated", dryRun.Payload!.Value.GetProperty("status").GetString()!);
        Assert.Equal(1, dryRun.Payload.Value.GetProperty("sourceCount").GetInt32());
        var watchRoot = Path.Combine(dataRoot, "watch");
        Directory.CreateDirectory(watchRoot);
        File.WriteAllText(Path.Combine(watchRoot, "office.docx"), "review only");
        var watchRequest = new IpcRequestEnvelope(1, Guid.NewGuid(), "cli.watch", "watch-key",
            JsonSerializer.SerializeToElement(new { arguments = new[] { watchRoot } }));
        var watch = transport.SendAsync(watchRequest, stop.Token).AsTask().GetAwaiter().GetResult();
        Assert.True(watch.Error is null);
        Assert.Equal(false, watch.Payload!.Value.GetProperty("submissionPerformed").GetBoolean());
        Assert.True(watch.Payload.Value.GetProperty("results").GetArrayLength() > 0);
        Assert.Equal("requires_review", watch.Payload.Value.GetProperty("results")[0].GetProperty("state").GetString()!);
        var cli = new NamedPipeCliCoordinator(transport);
        Assert.Throws<NotSupportedException>(() => cli.ExecuteAsync("status", ["missing-job"], stop.Token).AsTask().GetAwaiter().GetResult());
        var desktopRequest = new IpcRequestEnvelope(1, Guid.NewGuid(), "desktop.prepare", "desktop-prepare-key",
            JsonDocument.Parse("{\"inputs\":[],\"requestedSettings\":{},\"layoutOptions\":{}}").RootElement.Clone());
        var desktopResponse = transport.SendAsync(desktopRequest, stop.Token).AsTask().GetAwaiter().GetResult();
        Assert.Equal("unavailable", desktopResponse.Error!.Code);
        Assert.Contains("PDFium", desktopResponse.Error.Message);
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => run.GetAwaiter().GetResult());
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var restarted = new CoordinatorRuntimeHost(appName, new UnavailableCoordinatorRequestHandler());
        Assert.True(restarted.IsOwner);
        restarted.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Directory.Delete(dataRoot, recursive: true);
    }

    internal static void CoordinatorCapabilitiesRequireValidatedPdfiumRuntime()
    {
        var originalPath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_PATH");
        var originalHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_SHA256");
        var dataRoot = Path.Combine(Path.GetTempPath(), "PaperBatch-invalid-pdfium-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_PATH", Path.Combine(dataRoot, "missing-runtime.so"));
            Environment.SetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_SHA256", new string('0', 64));
            var handler = new ProductionCoordinatorRequestHandler(dataRoot);
            var request = new IpcRequestEnvelope(1, Guid.NewGuid(), "runtime.status", null, JsonDocument.Parse("{}").RootElement.Clone());
            var response = handler.HandleAsync(request).AsTask().GetAwaiter().GetResult();
            Assert.Equal(false, response.Payload!.Value.GetProperty("prepare").GetBoolean());
            Assert.Equal(false, response.Payload.Value.GetProperty("submit").GetBoolean());
            Assert.Equal(false, response.Payload.Value.GetProperty("cancel").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_PATH", originalPath);
            Environment.SetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_SHA256", originalHash);
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    internal static void CoordinatorInstanceLeaseAllowsOnlyOneOwner()
    {
        var application = "PaperBatch-test-" + Guid.NewGuid().ToString("N");
        using (var first = CoordinatorInstanceLease.TryAcquire(application) ?? throw new InvalidOperationException("First lease was not acquired."))
        {
            Assert.True(CoordinatorInstanceLease.TryAcquire(application) is null);
        }
        using var afterExit = CoordinatorInstanceLease.TryAcquire(application);
        Assert.True(afterExit is not null);
    }

    internal static void NamedPipeTransportNegotiatesAndPreservesRequestIdentity()
    {
        var pipeName = "paperbatch-test-" + Guid.NewGuid().ToString("N");
        var seen = new System.Collections.Concurrent.ConcurrentBag<(Guid CorrelationId, string? IdempotencyKey)>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new NamedPipeCoordinatorServer(pipeName, (request, _) =>
        {
            seen.Add((request.CorrelationId, request.IdempotencyKey));
            using var payload = JsonDocument.Parse("{\"accepted\":true}");
            return ValueTask.FromResult(new IpcResponseEnvelope(1, request.CorrelationId, payload.RootElement.Clone(), null));
        });
        var serverTask = server.RunAsync(stop.Token);
        Thread.Sleep(50);
        var transport = new NamedPipeLocalCoordinatorTransport(pipeName);
        using var requestPayload = JsonDocument.Parse("{}");
        var requests = Enumerable.Range(0, 4).Select(index =>
        {
            var key = index < 2 ? "same-retry-key" : "request-" + index;
            return transport.SendAsync(new IpcRequestEnvelope(1, Guid.NewGuid(), "run.prepare", key, requestPayload.RootElement.Clone()), stop.Token).AsTask();
        }).ToArray();
        Task.WhenAll(requests).GetAwaiter().GetResult();
        Assert.Equal(4, seen.Count);
        Assert.Equal(2, seen.Count(item => item.IdempotencyKey == "same-retry-key"));
        foreach (var response in requests.Select(task => task.Result))
        {
            Assert.True(response.Error is null);
            Assert.Equal(true, response.Payload!.Value.GetProperty("accepted").GetBoolean());
        }
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => serverTask.GetAwaiter().GetResult());
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    internal static void NamedPipeRequestDeadlineReturnsTimeoutAndPreservesCancellation()
    {
        var pipeName = "paperbatch-timeout-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = new NamedPipeCoordinatorServer(pipeName, async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        var serverTask = server.RunAsync(stop.Token);
        Thread.Sleep(50);
        var transport = new NamedPipeLocalCoordinatorTransport(pipeName, requestTimeout: TimeSpan.FromMilliseconds(150));
        using var payload = JsonDocument.Parse("{}");
        var request = new IpcRequestEnvelope(1, Guid.NewGuid(), "run.prepare", "timeout-key", payload.RootElement.Clone());
        Assert.Throws<TimeoutException>(() => transport.SendAsync(request).AsTask().GetAwaiter().GetResult());
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => transport.SendAsync(request, callerCancellation.Token).AsTask().GetAwaiter().GetResult());
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => serverTask.GetAwaiter().GetResult());
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    internal static void NamedPipeTransportEnforcesNegotiatedMessageLimit()
    {
        var pipeName = "paperbatch-limit-" + Guid.NewGuid().ToString("N");
        var dispatched = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = new NamedPipeCoordinatorServer(pipeName, (request, _) =>
        {
            Interlocked.Increment(ref dispatched);
            using var payload = JsonDocument.Parse("{}");
            return ValueTask.FromResult(new IpcResponseEnvelope(1, request.CorrelationId, payload.RootElement.Clone(), null));
        });
        var serverTask = server.RunAsync(stop.Token);
        Thread.Sleep(50);
        var transport = new NamedPipeLocalCoordinatorTransport(pipeName, maximumMessageBytes: 1024);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { value = new string('x', 2048) }));
        var request = new IpcRequestEnvelope(1, Guid.NewGuid(), "run.prepare", null, payload.RootElement.Clone());
        Assert.Throws<InvalidDataException>(() => transport.SendAsync(request, stop.Token).AsTask().GetAwaiter().GetResult());
        Assert.Equal(0, Volatile.Read(ref dispatched));
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => serverTask.GetAwaiter().GetResult());
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    internal static void NamedPipeServerClosesClientsThatWithholdFrameHeaders()
    {
        var pipeName = "paperbatch-stalled-" + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = new NamedPipeCoordinatorServer(pipeName, (request, _) =>
        {
            using var payload = JsonDocument.Parse("{\"accepted\":true}");
            return ValueTask.FromResult(new IpcResponseEnvelope(1, request.CorrelationId, payload.RootElement.Clone(), null));
        }, clientFrameTimeout: TimeSpan.FromMilliseconds(100));
        var serverTask = server.RunAsync(stop.Token);
        using var stalled = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        stalled.Connect(1000);
        Thread.Sleep(250);
        using var requestPayload = JsonDocument.Parse("{}");
        var response = new NamedPipeLocalCoordinatorTransport(pipeName).SendAsync(
            new IpcRequestEnvelope(1, Guid.NewGuid(), "run.status", null, requestPayload.RootElement.Clone()), stop.Token)
            .AsTask().GetAwaiter().GetResult();
        Assert.True(response.Error is null);
        Assert.Equal(true, response.Payload!.Value.GetProperty("accepted").GetBoolean());
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => serverTask.GetAwaiter().GetResult());
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    private static void AssertJsonObjectProperties(JsonElement value, params string[] expected)
    {
        Assert.True(value.ValueKind == JsonValueKind.Object);
        var actual = value.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(propertyName => propertyName, StringComparer.Ordinal);
        var allowlist = expected.OrderBy(propertyName => propertyName, StringComparer.Ordinal);
        Assert.True(actual.SequenceEqual(allowlist));
    }

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
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
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
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static void ExecuteSql(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
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
