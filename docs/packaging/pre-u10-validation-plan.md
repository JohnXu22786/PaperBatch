# Pre-U10 validation plan (PLAN2AUTO)

## Scope and boundaries

This plan validates PaperBatch's print path and its configured document-conversion path. The existing Ubuntu, Windows, and macOS build/test matrix remains general cross-platform CI evidence; it does not claim PaperBatch print support on all three operating systems. Production document conversion is supported on Windows and Linux when a trusted LibreOffice executable path and SHA-256 pin are explicitly configured. PaperBatch conversion and print preparation remain unsupported on macOS. Print-specific hosted evidence runs in the Windows matrix lane because production printing uses Windows GDI and Winspool.

The Windows batch-print contract test imports `tests/fixtures/synthetic_documents/synthetic_two_page.pdf` followed by `tests/fixtures/print_batch/synthetic_batch_marker.png` through the production source intake and renderer paths, composes and persists the combined final PDF with pinned PDFium, and submits that artifact through `WindowsPrintCoordinator` and `WindowsGdiPrintBackend` to an injected fake GDI/Winspool boundary. The main case checks both source identities and order, renderer and per-source page mappings, the three-page persisted artifact's bytes/hash/length, one compatible segment and job for shared printer settings, explicit queue/options, GDI page/document calls, and the job-owned receipt. A second prepared batch uses per-document layout copy counts to exercise two ordered segments/jobs; the fake accepts the first and makes the second outcome ambiguous after `StartDoc`, then verifies replay does not submit again. The prior single-PDF production-path contract remains a separate regression. The fake creates no OS print job.

The exact-run report identifies fixture paths, formats, hashes and sizes; source order and batch counts; final artifact identity/page count; segment/job counts; simulated submission and partial-failure outcomes; workflow/run/attempt; Windows runner image; .NET and pinned PDFium identities; and evidence limits. The batch example uses shared printer settings and does not establish arbitrary per-item printer-option behavior. The mixed batch now includes PDF, image, and safely preflighted LibreOffice document inputs where the runner has the explicitly pinned engine configured.

On hosted Windows and Linux runners, install only the pinned LibreOffice build after verifying its Document Foundation signature, then set its explicit executable path and SHA-256 for the production renderer. Exercise synthetic DOCX, XLSX, PPTX, and only those ODT/ODS/ODP/RTF fixtures that pass PaperBatch preflight through production intake and rendering. On Windows, include document inputs in mixed-batch preparation and send only the persisted artifact to the injected fake GDI/Winspool backend. PDFium remains a separate explicit requirement for PDF page inspection, composition, and preparation. This evidence records the actual LibreOffice version, executable digest, runner, and scenario results. It does not establish Office/WPS equivalence, visual fidelity, driver support, or physical output. Do not install Microsoft Office/WPS, a printer driver, or a printer queue. macOS remains a protocol/unsupported-platform lane, not PaperBatch conversion or print support. No safe existing local queue is approved for real submission; native submission remains `not_run`, and physical output remains `unavailable`. A fake spooler, queue state, or reported job completion never proves paper output.

LibreOffice resource controls are deliberately described by their actual scope. Each source and accepted final PDF is limited to 512 MiB. An in-process semaphore plus owner-only file lock under the current user's PaperBatch data directory serializes conversions across processes sharing that data root; separate OS users or isolated data-root overrides have separate locks. Each conversion process has a two-minute timeout, and cancellation/timeout terminate and reap the owned process tree before operation-directory cleanup. Linux applies kernel `RLIMIT_FSIZE=512 MiB` per file; it does not cap total profile/cache/temp usage. Windows uses a periodic output-file monitor that terminates the owned process tree at the 512 MiB ceiling, but the file can temporarily overshoot between checks. The final PDF is rejected above 512 MiB. This Windows monitor is best-effort, not a hard disk cap; neither OS currently has an aggregate per-conversion quota for LibreOffice profile/cache/temp files. These limits are not a sandbox. The user accepted this Windows limitation for BDF1 with the scope stated here.

U10 real-user testing remains deferred. This plan does not authorize a release, physical print, paid purchase, new license, or new infrastructure.

## Ordered PLAN2AUTO units

### 1. `pre_u10_windows_print_path_ci` — current unit

**Work:** Keep the repository's general locked restore, .NET core/probe/Avalonia builds, desktop and managed tests, Python checks, and application import evidence on hosted Ubuntu, Windows, and macOS. On hosted Ubuntu and Windows only, install the pinned and signature-verified LibreOffice build and run synthetic supported document inputs through the production renderer configured by exact path and executable digest. On Windows, include converted document fixtures in mixed batch preparation, persistence, and print orchestration with an injected fake GDI boundary. Keep the single-PDF contract as regression coverage, and publish bounded exact-run renderer and batch reports. macOS checks retain protocol and unsupported-platform coverage only.

**Acceptance:** The general cross-platform matrix passes and remains described as build/test and application-import evidence. Ubuntu and Windows reports bind to the exact commit and workflow/run/attempt and include fixture identities/order, source formats, renderer outcomes, actual LibreOffice version and executable SHA-256, runner identity, and evidence limits. The Windows batch report also includes batch/page/segment/job counts, final artifact hash/length, PDFium identity, simulated outcomes, and evidence limits. The batch test proves source order and mapping, persisted combined artifact integrity, exact submitted bytes/options, job/segment grouping, receipt ownership, and stop/no-auto-resubmit behavior after an ambiguous later segment. It never claims real queue acceptance or paper output. Native queue inventory, native real-queue submission, and physical paper output remain separate statuses; this test records real submission as `not_run` and paper output as `unavailable`.

### 2. `pre_u10_exact_commit_evidence_access` — satisfied on main

**Basis:** PR #42 is merged to `main` as `9e7fc231c8341a8ba45dbfd7fde177f5c7a5a105` (`Expose exact-commit CI evidence in run summaries`). That commit adds bounded exact-run summaries and workflow/run/attempt identity for CI evidence.

**Disposition:** Satisfied by the existing main commit. Do not create duplicate work.

### 3. `pre_u10_readiness_and_go_no_go` — downstream

**Depends on:** Unit 1 and the satisfied Unit 2 evidence.

**Work:** Batch will refresh the readiness/go-no-go evidence after this unit merges. Reconcile exact-commit evidence against package provenance/contents and notices, print queue safety and ambiguous completion/recovery, privacy/redaction, offline behavior, and known migration/device gaps. Keep U10 deferred for separate authorization.

**Acceptance:** Every claim maps to inspectable exact-run evidence and states what it cannot prove. Package, print safety, privacy, and recovery gates are individually dispositioned. No queue completion or protocol result is treated as proof of physical output. No release/tag, paid commitment, physical print, or U10 trial is initiated.

## Execution record

- The existing general matrix keeps its hosted Ubuntu/Windows/macOS builds, tests, and application-import report. It is not PaperBatch print support evidence for non-Windows systems.
- The Windows batch-print report is produced by the managed test runner and records exact-run identity, the two checked-in fixture identities and order, batch/page/segment/job counts, persisted final artifact identity, Windows runner image, .NET and pinned PDFium identity, fake boundary path, partial ambiguous outcome, and evidence limits.
- The print contract uses an injected fake GDI boundary. Native queue inventory is a separate read-only observation; native real-queue submission is `not_run` without a safe verified queue; physical paper output is `unavailable` without a physical device and direct confirmation.
- Unit 2 remains satisfied on `main` by PR #42 / commit `9e7fc231c8341a8ba45dbfd7fde177f5c7a5a105`.
- Unit 3 remains downstream for Batch to refresh after this unit merges. U10 real-user testing remains deferred.
- Windows conversion output monitoring is best-effort and may temporarily overshoot; Linux `RLIMIT_FSIZE` applies per file, not aggregate profile/cache/temp storage. The final PDF ceiling, two-minute timeout, cancellation, process-tree reaping, and cleanup are checked separately. Do not report a strict total disk bound.
