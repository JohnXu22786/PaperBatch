using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Serialization;

namespace PaperBatch.Core.Persistence;

public sealed record RetentionMetadata(DateTimeOffset ExpiresAtUtc, bool RetainWhileUnresolved);

public sealed record OsJobReceipt(
    string QueueIdentity,
    string OsJobId,
    string RunNonce,
    DateTimeOffset SubmittedAtUtc);

public sealed record StoredRun(
    string RunId,
    DateTimeOffset CreatedAtUtc,
    RunState State,
    string PreparedRunJson,
    RetentionMetadata Retention);

public sealed record StoredJob(
    string JobId,
    string RunId,
    int SegmentIndex,
    JobState State,
    string? IdempotencyKey,
    OsJobReceipt? OsReceipt,
    DateTimeOffset UpdatedAtUtc);

public sealed record JobTransition(
    long Sequence,
    string JobId,
    JobState? FromState,
    JobState ToState,
    DateTimeOffset AtUtc,
    string? IdempotencyKey,
    string? Reason);

public sealed record RunCreationResult(StoredRun Run, ImmutableArray<StoredJob> Jobs, bool Replayed);

public sealed record JobOperationResult(StoredJob Job, bool Replayed);

public sealed class IdempotencyConflictException(string message) : InvalidOperationException(message);

/// <summary>Transactional local metadata store. It persists prepared metadata and OS receipts, never document bytes or passwords.</summary>
public sealed class SqliteRunStore
{
    public const int CurrentSchemaVersion = 2;
    private const int MaximumIdempotencyKeyLength = 128;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _databasePath;
    private bool _initialized;

    public SqliteRunStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        Execute(connection, null, "PRAGMA journal_mode = WAL;");
        Execute(connection, null, "PRAGMA synchronous = FULL;");

        using (var transaction = connection.BeginTransaction())
        {
            var version = ReadUserVersion(connection, transaction);
            if (version > CurrentSchemaVersion)
            {
                throw new NotSupportedException($"Database schema version {version} is newer than supported version {CurrentSchemaVersion}.");
            }

            while (version < CurrentSchemaVersion)
            {
                switch (version)
                {
                    case 0:
                        CreateVersionOneSchema(connection, transaction);
                        version = 1;
                        break;
                    case 1:
                        UpgradeVersionOneToTwo(connection, transaction);
                        version = 2;
                        break;
                    default:
                        throw new NotSupportedException($"No forward migration exists from database schema version {version}.");
                }

                Execute(connection, transaction, $"PRAGMA user_version = {version};");
            }

            transaction.Commit();
        }

        var integrity = Convert.ToString(Scalar(connection, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"SQLite quick_check reported: {integrity}");
        }

        _initialized = true;
    }

    public RunCreationResult CreateRun(PreparedRun preparedRun, string idempotencyKey, RetentionMetadata retention)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(preparedRun);
        ArgumentNullException.ThrowIfNull(retention);
        ValidateIdempotencyKey(idempotencyKey);
        if (retention.ExpiresAtUtc <= preparedRun.CreatedAtUtc)
        {
            throw new ArgumentException("Retention expiry must be later than the run creation time.", nameof(retention));
        }

        var preparedRunJson = PreparedRunJson.Serialize(preparedRun);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var existing = ReadRunByRequestKey(connection, transaction, idempotencyKey);
        if (existing is not null)
        {
            if (!string.Equals(existing.RunId, preparedRun.RunId, StringComparison.Ordinal) ||
                !string.Equals(existing.PreparedRunJson, preparedRunJson, StringComparison.Ordinal) ||
                existing.Retention != retention)
            {
                throw new IdempotencyConflictException("The run creation idempotency key was already used for a different request.");
            }

            var existingJobs = ReadJobsForRun(connection, transaction, existing.RunId);
            transaction.Commit();
            return new RunCreationResult(existing, existingJobs, Replayed: true);
        }

        Execute(connection, transaction, """
            INSERT INTO runs (run_id, request_key, created_utc, state, prepared_run_json, retention_expires_utc, retain_while_unresolved)
            VALUES ($runId, $requestKey, $createdUtc, $state, $preparedRunJson, $retentionExpiresUtc, $retainWhileUnresolved);
            """,
            ("$runId", preparedRun.RunId),
            ("$requestKey", idempotencyKey),
            ("$createdUtc", FormatTimestamp(preparedRun.CreatedAtUtc)),
            ("$state", RunState.Ready.ToString()),
            ("$preparedRunJson", preparedRunJson),
            ("$retentionExpiresUtc", FormatTimestamp(retention.ExpiresAtUtc)),
            ("$retainWhileUnresolved", retention.RetainWhileUnresolved ? 1 : 0));

        foreach (var segment in preparedRun.Segments)
        {
            Execute(connection, transaction, """
                INSERT INTO jobs (job_id, run_id, segment_index, state, created_utc, updated_utc)
                VALUES ($jobId, $runId, $segmentIndex, $state, $createdUtc, $updatedUtc);
                """,
                ("$jobId", segment.SegmentId),
                ("$runId", preparedRun.RunId),
                ("$segmentIndex", segment.Index),
                ("$state", JobState.Ready.ToString()),
                ("$createdUtc", FormatTimestamp(preparedRun.CreatedAtUtc)),
                ("$updatedUtc", FormatTimestamp(preparedRun.CreatedAtUtc)));
            InsertTransition(connection, transaction, segment.SegmentId, from: null, JobState.Ready, preparedRun.CreatedAtUtc, idempotencyKey: null, reason: "prepared run created");
        }

        var run = new StoredRun(preparedRun.RunId, preparedRun.CreatedAtUtc, RunState.Ready, preparedRunJson, retention);
        var jobs = ReadJobsForRun(connection, transaction, preparedRun.RunId);
        transaction.Commit();
        return new RunCreationResult(run, jobs, Replayed: false);
    }

    public StoredRun? GetRun(string runId)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = OpenConnection();
        return ReadRun(connection, null, runId);
    }

    public PreparedRun? GetPreparedRun(string runId)
    {
        var storedRun = GetRun(runId);
        return storedRun is null
            ? null
            : PreparedRunJson.Parse(Encoding.UTF8.GetBytes(storedRun.PreparedRunJson));
    }

    public StoredJob? GetJob(string jobId)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        using var connection = OpenConnection();
        return ReadJob(connection, null, jobId);
    }

    public ImmutableArray<StoredJob> GetJobs(string runId)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var jobs = ReadJobsForRun(connection, transaction, runId);
        transaction.Commit();
        return jobs;
    }

    public ImmutableArray<JobTransition> GetTransitions(string jobId)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transition_id, job_id, from_state, to_state, at_utc, idempotency_key, reason
            FROM job_transitions WHERE job_id = $jobId ORDER BY transition_id;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        using var reader = command.ExecuteReader();
        var result = ImmutableArray.CreateBuilder<JobTransition>();
        while (reader.Read())
        {
            result.Add(new JobTransition(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : ParseEnum<JobState>(reader.GetString(2)),
                ParseEnum<JobState>(reader.GetString(3)),
                ParseTimestamp(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result.ToImmutable();
    }

    /// <summary>Persists intent before the caller performs any operating-system submission side effect.</summary>
    public JobOperationResult BeginSubmission(string jobId, string idempotencyKey, DateTimeOffset atUtc)
    {
        EnsureInitialized();
        ValidateIdempotencyKey(idempotencyKey);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var job = ReadJob(connection, transaction, jobId)
            ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");

        if (job.IdempotencyKey is { } existingKey)
        {
            if (!string.Equals(existingKey, idempotencyKey, StringComparison.Ordinal))
            {
                throw new IdempotencyConflictException("A different idempotency key already owns this submission attempt.");
            }

            transaction.Commit();
            return new JobOperationResult(job, Replayed: true);
        }

        if (job.State != JobState.Ready)
        {
            throw new InvalidOperationException($"A job in state {job.State} cannot begin submission.");
        }

        var changed = Execute(connection, transaction, """
            UPDATE jobs SET state = $nextState, submission_key = $idempotencyKey, updated_utc = $updatedUtc
            WHERE job_id = $jobId AND state = $currentState AND submission_key IS NULL;
            """,
            ("$nextState", JobState.Submitting.ToString()),
            ("$idempotencyKey", idempotencyKey),
            ("$updatedUtc", FormatTimestamp(atUtc)),
            ("$jobId", jobId),
            ("$currentState", JobState.Ready.ToString()));
        if (changed != 1)
        {
            throw new InvalidOperationException("Submission intent lost a concurrent state transition.");
        }

        InsertTransition(connection, transaction, jobId, JobState.Ready, JobState.Submitting, atUtc, idempotencyKey, "submission intent persisted before OS submission");
        var updated = ReadJob(connection, transaction, jobId)!;
        RefreshRunState(connection, transaction, job.RunId);
        transaction.Commit();
        return new JobOperationResult(updated, Replayed: false);
    }

    /// <summary>Persists the operating-system receipt atomically with the accepted-by-OS transition.</summary>
    public JobOperationResult RecordOsReceipt(string jobId, string idempotencyKey, OsJobReceipt receipt, DateTimeOffset atUtc)
    {
        EnsureInitialized();
        ValidateIdempotencyKey(idempotencyKey);
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateReceipt(receipt);
        var receiptJson = JsonSerializer.Serialize(receipt, JsonOptions);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var job = ReadJob(connection, transaction, jobId)
            ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");

        if (job.OsReceipt is not null && string.Equals(job.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
        {
            if (!string.Equals(JsonSerializer.Serialize(job.OsReceipt, JsonOptions), receiptJson, StringComparison.Ordinal))
            {
                throw new IdempotencyConflictException("The submission idempotency key already has a different OS receipt.");
            }

            transaction.Commit();
            return new JobOperationResult(job, Replayed: true);
        }

        if (job.State is not (JobState.Submitting or JobState.NeedsReviewUnknown)
            || !string.Equals(job.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An OS receipt requires the matching durable submission intent.");
        }

        var previousState = job.State;
        var transitionReason = previousState == JobState.NeedsReviewUnknown
            ? "OS receipt recovered while reconciling an unresolved submission"
            : "OS accepted the job";
        var changed = Execute(connection, transaction, """
            UPDATE jobs SET state = $nextState, os_receipt_json = $receiptJson, updated_utc = $updatedUtc
            WHERE job_id = $jobId AND state = $currentState AND submission_key = $idempotencyKey;
            """,
            ("$nextState", JobState.AcceptedByOs.ToString()),
            ("$receiptJson", receiptJson),
            ("$updatedUtc", FormatTimestamp(atUtc)),
            ("$jobId", jobId),
            ("$currentState", previousState.ToString()),
            ("$idempotencyKey", idempotencyKey));
        if (changed != 1)
        {
            throw new InvalidOperationException("OS receipt lost a concurrent state transition.");
        }

        InsertTransition(connection, transaction, jobId, previousState, JobState.AcceptedByOs, atUtc, idempotencyKey, transitionReason);
        var updated = ReadJob(connection, transaction, jobId)!;
        RefreshRunState(connection, transaction, job.RunId);
        transaction.Commit();
        return new JobOperationResult(updated, Replayed: false);
    }

    public StoredJob TransitionJob(string jobId, JobState nextState, string reason, DateTimeOffset atUtc)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 512 || reason.Any(char.IsControl))
        {
            throw new ArgumentException("Transition reasons must be bounded and contain no control characters.", nameof(reason));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var job = ReadJob(connection, transaction, jobId)
            ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
        if (nextState is JobState.Submitting or JobState.AcceptedByOs)
        {
            throw new InvalidOperationException($"State {nextState} can only be entered through its idempotent repository operation.");
        }

        if (!JobStateTransitions.IsAllowed(job.State, nextState))
        {
            throw new InvalidOperationException($"A job in state {job.State} cannot transition to {nextState}.");
        }

        var changed = Execute(connection, transaction, """
            UPDATE jobs SET state = $nextState, updated_utc = $updatedUtc
            WHERE job_id = $jobId AND state = $currentState;
            """,
            ("$nextState", nextState.ToString()),
            ("$updatedUtc", FormatTimestamp(atUtc)),
            ("$jobId", jobId),
            ("$currentState", job.State.ToString()));
        if (changed != 1)
        {
            throw new InvalidOperationException("Job transition lost a concurrent state update.");
        }

        InsertTransition(connection, transaction, jobId, job.State, nextState, atUtc, job.IdempotencyKey, reason);
        var updated = ReadJob(connection, transaction, jobId)!;
        RefreshRunState(connection, transaction, job.RunId);
        transaction.Commit();
        return updated;
    }

    private static void CreateVersionOneSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
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
                run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
                segment_index INTEGER NOT NULL,
                state TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                os_receipt_json TEXT NULL,
                UNIQUE (run_id, segment_index)
            );
            CREATE TABLE job_transitions (
                transition_id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
                from_state TEXT NULL,
                to_state TEXT NOT NULL,
                at_utc TEXT NOT NULL,
                idempotency_key TEXT NULL,
                reason TEXT NULL
            );
            CREATE INDEX ix_jobs_run_id ON jobs(run_id);
            CREATE INDEX ix_job_transitions_job_id ON job_transitions(job_id, transition_id);
            """);
    }

    private static void UpgradeVersionOneToTwo(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            ALTER TABLE runs ADD COLUMN retain_while_unresolved INTEGER NOT NULL DEFAULT 1 CHECK (retain_while_unresolved IN (0, 1));
            ALTER TABLE jobs ADD COLUMN submission_key TEXT NULL;
            CREATE UNIQUE INDEX ux_jobs_submission_key ON jobs(submission_key) WHERE submission_key IS NOT NULL;
            """);
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys = ON;");
        Execute(connection, null, "PRAGMA busy_timeout = 5000;");
        Execute(connection, null, "PRAGMA synchronous = FULL;");
        return connection;
    }

    private static StoredRun? ReadRun(SqliteConnection connection, SqliteTransaction? transaction, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, created_utc, state, prepared_run_json, retention_expires_utc, retain_while_unresolved
            FROM runs WHERE run_id = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new StoredRun(
            reader.GetString(0),
            ParseTimestamp(reader.GetString(1)),
            ParseEnum<RunState>(reader.GetString(2)),
            reader.GetString(3),
            new RetentionMetadata(ParseTimestamp(reader.GetString(4)), reader.GetInt64(5) != 0));
    }

    private static StoredRun? ReadRunByRequestKey(SqliteConnection connection, SqliteTransaction transaction, string requestKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT run_id FROM runs WHERE request_key = $requestKey;";
        command.Parameters.AddWithValue("$requestKey", requestKey);
        var runId = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return runId is null ? null : ReadRun(connection, transaction, runId);
    }

    private static StoredJob? ReadJob(SqliteConnection connection, SqliteTransaction? transaction, string jobId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT job_id, run_id, segment_index, state, submission_key, os_receipt_json, updated_utc
            FROM jobs WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var receiptJson = reader.IsDBNull(5) ? null : reader.GetString(5);
        return new StoredJob(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            ParseEnum<JobState>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            receiptJson is null ? null : JsonSerializer.Deserialize<OsJobReceipt>(receiptJson, JsonOptions),
            ParseTimestamp(reader.GetString(6)));
    }

    private static ImmutableArray<StoredJob> ReadJobsForRun(SqliteConnection connection, SqliteTransaction transaction, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT job_id FROM jobs WHERE run_id = $runId ORDER BY segment_index;";
        command.Parameters.AddWithValue("$runId", runId);
        using var reader = command.ExecuteReader();
        var jobIds = new List<string>();
        while (reader.Read()) jobIds.Add(reader.GetString(0));
        reader.Close();
        return jobIds.Select(jobId => ReadJob(connection, transaction, jobId)!).ToImmutableArray();
    }

    private static void RefreshRunState(SqliteConnection connection, SqliteTransaction transaction, string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state FROM jobs WHERE run_id = $runId ORDER BY segment_index;";
        command.Parameters.AddWithValue("$runId", runId);
        using var reader = command.ExecuteReader();
        var states = new List<JobState>();
        while (reader.Read()) states.Add(ParseEnum<JobState>(reader.GetString(0)));
        reader.Close();

        var runState = RunStateAggregator.Aggregate(states);
        Execute(connection, transaction, "UPDATE runs SET state = $state WHERE run_id = $runId;",
            ("$state", runState.ToString()), ("$runId", runId));
    }

    private static void InsertTransition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        JobState? from,
        JobState to,
        DateTimeOffset atUtc,
        string? idempotencyKey,
        string? reason) => Execute(connection, transaction, """
            INSERT INTO job_transitions (job_id, from_state, to_state, at_utc, idempotency_key, reason)
            VALUES ($jobId, $fromState, $toState, $atUtc, $idempotencyKey, $reason);
            """,
            ("$jobId", jobId),
            ("$fromState", from?.ToString()),
            ("$toState", to.ToString()),
            ("$atUtc", FormatTimestamp(atUtc)),
            ("$idempotencyKey", idempotencyKey),
            ("$reason", reason));

    private static void ValidateReceipt(OsJobReceipt receipt)
    {
        ValidateOpaqueToken(receipt.QueueIdentity, nameof(receipt.QueueIdentity));
        ValidateOpaqueToken(receipt.OsJobId, nameof(receipt.OsJobId));
        ValidateOpaqueToken(receipt.RunNonce, nameof(receipt.RunNonce));
    }

    private static void ValidateOpaqueToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("Receipt identifiers must be nonempty, bounded, and contain no control characters.", parameterName);
        }
    }

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumIdempotencyKeyLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("Idempotency keys must be nonempty, bounded, and contain no control characters.", nameof(value));
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Call Initialize before using the run store.");
        }
    }

    private static int ReadUserVersion(SqliteConnection connection, SqliteTransaction transaction) =>
        Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string timestamp) => DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var result) &&
        Enum.IsDefined(result) &&
        string.Equals(Enum.GetName(result), value, StringComparison.Ordinal)
            ? result
            : throw new InvalidDataException($"Unknown persisted {typeof(TEnum).Name} value '{value}'.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

}
