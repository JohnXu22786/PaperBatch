# Pre-U10 validation plan (PLAN2AUTO)

## Scope and boundaries

This plan orders the remaining pre-U10 validation work. It preserves U10 real-user trials as deferred and does not authorize a release, physical print, paid purchase, new license, or new infrastructure. A CUPS/IPP exchange, protocol result, compatibility probe, queue state, or reported job completion is not evidence that pages were physically produced, nor does it establish general native application compatibility.

The current execution environment is a Linux container without a VM or hypervisor device. No Windows 11 VM, Office installation, or WPS installation is available here. Native Office/WPS work is blocked unless a qualifying, already available and legally licensed isolated Windows VM is attached. Do not claim that such a VM or software is available, and do not purchase one.

## Ordered PLAN2AUTO units

Dispatch these units in order. Each later unit depends on the earlier evidence and its acceptance criteria. Reassess the plan after each merge; record a gate as skipped only when existing exact-commit evidence demonstrably satisfies it.

### 1. `pre_u10_cross_platform_managed_ci` — this unit

**Work:** Run the existing locked restore, .NET core/probe/Avalonia builds, headless desktop and managed tests, Python compilation/unit suite, and applicable fixtures on hosted Ubuntu, Windows, and macOS runners. Retain OS-specific conditions: Linux renderer setup and corpus, Windows package/PDFium/spooler evidence, and cross-platform managed fixtures.

**Acceptance:** The exact PR-head workflow has the same managed/headless application path on all three operating systems and passes; workflow YAML parses; platform-only steps run only on the intended OS. Report any runner availability or OS-specific limitation precisely. This is CI portability evidence, not a claim that PaperBatch supports macOS or submits jobs to a printer there.

### 2. `pre_u10_exact_commit_evidence_access`

**Depends on:** Unit 1.

**Work:** Make exact-commit evidence inspectable from the workflow run even when artifact download/body access is unavailable. Add concise, bounded job summaries for the relevant package, managed test, runner, and report outcomes; bind summaries and machine-readable reports to the PR head commit and workflow run/attempt; preserve artifact links and bounded retention. Summaries must include material runner/tool provenance and explicit evidence limits without copying unbounded logs or sensitive data.

**Acceptance:** For a known exact run, a reviewer can inspect its commit, job conclusions, bounded scenario/package results, runner identity/version when exposed, and limitations directly in the run summary. A missing report or failed job cannot be presented as passed. Artifact-body access is not the sole review path; no artifact contents are claimed inspected unless they were actually retrieved and checked.

### 3. `pre_u10_office_wps_stubs_and_trusted_runner_contract`

**Depends on:** Unit 2.

**Work:** Keep Office/WPS behavior covered in ordinary CI with deterministic stubs. Specify and validate an opt-in native runner contract restricted to trusted protected-main or manual execution, exact protected checkout verification, an isolated disposable/snapshotted host, no repository secrets, minimum allowlisted network, and bounded/redacted evidence. Never run public pull-request or fork code on a self-hosted Office/WPS runner. Do not enable a runner that is absent.

**Acceptance:** Ordinary PR CI exercises only synthetic stubs and clearly reports native coverage as blocked/not run. Contract tests reject pull requests, forks, untrusted refs, incomplete isolation/software metadata, and unexpected secrets/network policy before launch. The trusted native job remains disabled until all prerequisites are verified.

### 4. `pre_u10_licensed_interactive_office_wps`

**Depends on:** Units 1–3 and a verified existing licensed isolated Windows VM.

**Work:** Only when such a VM is attached, run distinct interactive Office-only, WPS-only, and coexistence scenarios against synthetic allowlisted documents. Record exact Windows image, isolation/snapshot, application edition/build/channel/bitness and activation state, scenario outcome, and bounded evidence. Reset the isolated profile between scenarios. If the VM is unavailable or its licensing/isolation cannot be verified, leave the native lane blocked and record the concrete missing prerequisite; proceed with no purchase.

**Acceptance:** Each available profile has its own exact-run evidence and explicit limits; unavailable profiles are blocked, never passed by stubs or protocol tests. No unattended Office COM automation. No real-user trial or physical print is part of this unit.

### 5. `pre_u10_readiness_and_go_no_go`

**Depends on:** Units 1–4, with Unit 4 explicitly recorded as blocked if no qualifying VM is available.

**Work:** Reconcile exact-commit evidence against package provenance/contents and notices, document behavior, queue safety and ambiguous completion/recovery, privacy/redaction, offline behavior, and known migration/device/native gaps. Produce a final pre-U10 go/no-go report with pass, partial, blocked, and not-run states, evidence links, owners or prerequisites, and a bounded decision rationale. Keep U10 deferred for separate authorization.

**Acceptance:** Every claim maps to inspectable exact-run evidence and states what it cannot prove. Package, document, queue-safety, privacy, and recovery gates are individually dispositioned. The report makes a clear pre-U10 go/no-go recommendation and does not treat CUPS/IPP/protocol/probe/queue completion as proof of physical output or general native compatibility. No release/tag, paid commitment, physical print, or U10 trial is initiated.

## Execution record

- Unit 1 is implemented in `.github/workflows/ci.yml` by adding macOS to the existing `build-and-test` matrix. Linux-only renderer/corpus work and Windows-only packaging, PDFium, diagnostics, and spooler evidence remain conditionally scoped to their operating systems.
- Unit 2 implementation adds bounded exact-run summaries and workflow/run/attempt identity to CI evidence reports. Its remote exact-head CI and review gates remain pending until this unit is reviewed and merged.
- Units 3–5 remain ordered future gates; this plan does not enable a self-hosted runner or conduct native software or real-user trials.
- Native Office/WPS lane status in this environment: **blocked**. The environment is a Linux container and exposes no VM/hypervisor device or licensed isolated Windows VM.
