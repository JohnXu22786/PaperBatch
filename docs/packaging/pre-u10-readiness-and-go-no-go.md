# Pre-U10 readiness and go/no-go

Reviewed 2026-11-22 against `main` merge commit `5d7ecd2cd65c7420fad2fdab340073531e5f64b3` (PR #110). This is an evidence review of the approved pre-U10 plan, not a product certification or release authorization.

## Decision

**No-go for beta/public release and U10 real-user trials.** Q2 offline/session evidence, Q3 historical migration evidence, and Q5 device/print evidence remain partial. The package artifact bodies have not been inspected; Office/WPS native validation remains blocked; and PaperBatch macOS support, real printer output, and device/OEM coverage are not established. Continue internal synthetic development within the approved plan. Keep U10 deferred for separate authorization.

No physical print, purchase, paid runner/certificate/infrastructure, release/tag, Office/WPS validation, or U10 user trial was performed or initiated for this decision.

## Evidence basis and confidence

- Unit 2 exact-commit evidence remains satisfied on `main` by PR #42 / `9e7fc231c8341a8ba45dbfd7fde177f5c7a5a105`.
- Pre-U10 Unit 1 PR #110 merged as `5d7ecd2cd65c7420fad2fdab340073531e5f64b3`; its PR head was `2ab72068c43f39fcfaebdb1e2881fa42f4b2c5ae`. Exact-head CI [44981874302](https://github.com/JohnXu22786/PaperBatch/actions/runs/44981874302) and U9 [44981874294](https://github.com/JohnXu22786/PaperBatch/actions/runs/44981874294) passed. Post-merge U9 [44984254223](https://github.com/JohnXu22786/PaperBatch/actions/runs/44984254223) passed against the merge SHA. Post-merge CI [44984254303](https://github.com/JohnXu22786/PaperBatch/actions/runs/44984254303) is tracked in [U9 final evidence](u9-final-evidence.md) with its latest observed status. If still in progress, its conclusion remains unreported.
- CI workflow and test sources show what is configured and asserted. Workflow run/job metadata shows execution status. Archived Actions report and package artifact bodies were not read, so this report does not infer their omitted values, inspect package payload contents, or claim artifact-level provenance beyond the tracked workflow's checks.
- Unit 1's Windows mixed-batch contract is a synthetic, injected fake GDI/Winspool test. It validates the prepared/persisted artifact and the modeled handoff; it does not create an OS print job or prove physical output.

## Gate dispositions

| Gate | Disposition | Evidence and boundary |
|---|---|---|
| Package contents, provenance, notices | **Partial** | The Windows package workflow verifies manifest file hashes, locked dependency file hashes, required `LICENSE` and third-party notice files, archive checksum, archive paths/payload, extraction, and tamper rejection (`.github/workflows/ci.yml`, package verifier). The artifact is explicitly unsigned and is a development package. Artifact bodies were not inspected here, so actual archived files, publisher attestation, binary SBOM, and clean-consumer-PC behavior remain unverified. Tracked dependency and notice inventories are not legal review or a complete binary-level SBOM. |
| Print queue safety and ambiguous-result recovery | **Partial** | The Windows batch contract in `tests/PaperBatch.CompatibilityProbe.Tests/PrintingTests.cs` exercises the production preparation/composition path and fake GDI boundary, binds the receipt to its modeled job/queue/run nonce, marks a later ambiguous segment unknown, and verifies replay makes no second submission. The report source explicitly labels native queue inventory and native real-queue submission `not_run`, and paper output `unavailable`. Read-only hosted-runner queue inventory is a separate CI observation, not a user's queue. No native real queue was submitted to; fake acceptance, OS status, or protocol status would not prove pages. |
| Privacy and redaction | **Bounded checks passed; overall privacy assurance partial** | `tests/test_libreoffice_corpus.py` tests redaction of credential-like values, authorization values, and absolute paths in selected renderer reports and blocked/skipped records. `tests/PaperBatch.CompatibilityProbe.Tests/FaultSecurityStressProfile.cs` includes `privacy.diagnostic_redaction`; the managed test assertions check serialized output does not contain a synthetic secret. The Windows session source snapshots only three named current-user PaperBatch registry trees, computes before/after digests, and emits digest/count metadata rather than raw values in that report. These checks apply to the named test/report paths and fields only; they are not a host-wide privacy audit, and archived report bodies were not inspected. |
| Offline behavior | **Partial (Q2)** | `tools/simulate_windows_package_session.ps1` uses an ephemeral hosted Windows runner, dedicated temporary/profile/app-data roots, and outbound firewall blocks scoped to the two packaged coordinator executable paths. It checks startup, coordinator continuity, and package-tree switching with one package's bytes. The workflow does not launch the GUI; it does not trace or deny all host network traffic, inspect all user roots/registry, or establish a clean client PC or client-wide offline guarantee. |
| Migration and rollback | **Partial (Q3)** | Existing CI covers v1 SQLite schema migration/rollback/retry, and the package session switches between two extracted trees made from the same current package. No historical package, installer/updater, application-version rollback, or cross-version submitted-job lifecycle was tested. Package-tree continuity is not an upgrade/migration pass. |
| Device and platform gaps | **Partial / unsupported / blocked** | The Windows path uses fake GDI/Winspool only; no real Windows queue, printer driver, OEM configuration, physical device, or paper evidence exists. macOS is protocol/unsupported-platform coverage, not PaperBatch product support. Native Office/WPS compatibility remains blocked; LibreOffice conversion evidence does not establish Office/WPS equivalence or visual fidelity. U10 is deferred. |

## Conditions to reconsider

Reconsider beta/public release only after package payloads and exact versions are inspectably verified, Q2/Q3/Q5 limitations are completed or explicitly accepted by the project owner, and applicable publisher/release evidence is ready. Reconsider U10 only under separate authorization and its own safety/evidence plan. Do not promote synthetic, fake-spooler, queue-status, or protocol evidence into a physical-output claim.

The Windows LibreOffice output monitor remains best-effort and can overshoot between checks; it is not a hard cap. Aggregate profile/cache/temp usage is not bounded by a per-conversion total disk quota.

## Sources

- [Pre-U10 validation plan](pre-u10-validation-plan.md)
- [U9 final evidence and beta boundary](u9-final-evidence.md)
- `.github/workflows/ci.yml` and `.github/workflows/u9-simulation-matrix.yml`
- `tests/PaperBatch.CompatibilityProbe.Tests/PrintingTests.cs`
- `tests/PaperBatch.CompatibilityProbe.Tests/FaultSecurityStressProfile.cs`
- `tests/test_libreoffice_corpus.py`
- `tools/simulate_windows_package_session.ps1`
