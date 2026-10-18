# Core model, state, schema, and IPC contracts

This note defines the shared .NET 10 contracts introduced before UI, renderer workers, or a Windows print backend. Engine versions, document formats, PDFium ABI, and printer-driver settings remain behind later versioned adapters.

## Prepared runs and renderer boundary

A `PreparedRun` is immutable after construction. It contains opaque snapshot IDs and SHA-256 values, renderer identity/version and capability contract version, rendered artifact identity/hash/page count, ordered source-to-rendered page mappings, effective print settings, and consecutive compatible segments. Settings include copies, color, duplex, media, orientation, and scaling, with optional opaque printer and tray IDs. It does not contain a source path, document bytes, or password. Segment flattening must cover every prepared item exactly once in the original order, and all items in a segment must have identical effective settings. Renderer capability media types are generic MIME identifiers; no engine or suite name is assumed by the core.

Run state is an aggregate of its segment jobs. Each job records its own append-only transition history:

| Job state | Meaning |
|---|---|
| `Ready` | Prepared and durably recorded; no OS submission intent exists. |
| `InProgress` (run aggregate) | Segment jobs have different states; inspect per-job history for the exact outcome. |
| `Submitting` | Idempotent submission intent was committed before the OS side effect. A crash here is uncertain. |
| `AcceptedByOs` | The OS receipt/job identity was committed; acceptance does not mean processing or paper output. |
| `OsProcessing` | The OS reports the job is processing. |
| `OsReportsComplete` | The OS reports completion. This does not assert that physical pages came out correctly. |
| `FailedBeforeSubmit` | Failure is known to be before the external submission side effect. |
| `CancelRequested` / `CancelObserved` | Cancellation was requested / observed; neither means pages can be recalled. |
| `NeedsReviewUnknown` | The OS outcome cannot safely be inferred, such as the intent/receipt crash gap. No automatic retry follows. |
| `UserConfirmedPaperOutput` | A user explicitly confirmed the paper output. This is the only state that records physical-output confirmation. |

The state graph rejects backward transitions and allows `UserConfirmedPaperOutput` after OS completion, observed cancellation, or explicit operator confirmation while the OS outcome remains unknown. Direct confirmation from `NeedsReviewUnknown` preserves the prior uncertainty in transition history. Reconciliation out of `NeedsReviewUnknown` is explicit. The core makes no physical-success claim from a queue record or OS status.

## SQLite metadata store

`SqliteRunStore.Initialize` runs forward migrations in one SQLite transaction using `PRAGMA user_version`; it never downgrades a newer database. Version 1 created run, job, and transition tables. Version 2 adds retention-while-unresolved metadata and a unique submission idempotency key. Initialization runs `PRAGMA quick_check` after migration and may safely run again. It then removes expired runs through `DeleteExpiredRuns`; job and transition rows are deleted in the same transaction. `ExpiresAtUtc` is an absolute deadline supplied when creating the run; resolving the run does not change that deadline. Runs are retained past expiry while they contain a submitting, OS-accepted, processing, cancel-requested, or unknown-outcome job when `RetainWhileUnresolved` is set. Ready jobs have no OS-side effect and do not block cleanup. Passing `RetainWhileUnresolved: false` allows expired active or uncertain jobs to be removed by explicit policy.

Run creation stores prepared metadata in a schema-versioned JSON envelope, with a representative version-1 golden fixture at `tests/PaperBatch.CompatibilityProbe.Tests/Fixtures/prepared-run-v1.json`. `GetPreparedRun` validates the supported version and domain invariants before restoring the immutable model. Persisted job IDs are generated independently of segment IDs, so separate runs may reuse segment IDs. Run creation inserts the run, all segment jobs, and initial `Ready` transitions in one transaction. `BeginSubmission` atomically stores the idempotency key, `Submitting` state, and transition before the caller contacts the OS. `RecordOsReceipt` atomically stores the receipt and `AcceptedByOs` state. Replaying an identical key returns the original record; reusing it for a different request or receipt fails. A `Submitting` record without an OS receipt is intentionally ambiguous and must become `NeedsReviewUnknown` if reconciliation cannot find a trustworthy result.

The database stores hashes, opaque snapshot/artifact IDs, page/settings metadata, state, OS receipt identifiers, and retention timestamps. It has no document-content, credential, or password fields. Recipes may reference user-selected local locations, but the SQLite prepared-run record does not persist those locations.

## Recipe JSON v1

Recipes use UTF-8 JSON with a required integer `schemaVersion` equal to `1`. The parser rejects comments, trailing commas, unknown properties, integer enum values, duplicate source IDs, invalid setting values, and unsupported schema versions. The maximum encoded recipe is 256 KiB, with at most 500 sources. The schema contains locations and settings only; it has no scripts, regular expressions, or embedded documents. Property ordering, indentation, and enum tokens are stable; `tests/PaperBatch.CompatibilityProbe.Tests/Fixtures/recipe-v1.json` is the golden serialization fixture.

## Local coordinator IPC v1

Requests and responses carry protocol version `1` and a correlation ID. Requests also carry an operation, optional idempotency key, and JSON object payload. Unknown properties and protocol versions fail closed. Request and response messages are limited to 256 KiB; error codes and messages are bounded. Capability negotiation chooses the highest shared version and the smaller peer message limit.

`ILocalCoordinatorTransport` is only a transport seam. This unit does not create a listener or choose a wire transport. A future named-pipe implementation must restrict access to the current user; the product must never add a network listener for this coordinator protocol.

## Verification boundary

The Linux and Windows CI matrix builds this library and runs the unchanged U0 tests together with focused core regression tests. None of those tests closes G1. Native Office/WPS, PDFium behavior, Windows spooler/driver routing, and verified physical output remain pending until the native evidence is recorded in the compatibility matrix.
