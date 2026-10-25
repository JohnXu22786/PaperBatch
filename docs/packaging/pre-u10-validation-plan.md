# Pre-U10 validation plan (PLAN2AUTO)

## Scope and boundaries

This plan verifies PaperBatch's document intake and processing by adding representative files through the application's production queue-import path. A separate pinned LibreOffice corpus records document-engine execution on synthetic files; it may help produce fixtures, but standalone LibreOffice conversion does not establish PaperBatch format support. Hosted results are synthetic CI evidence, not human fidelity review or proof of printed output.

U10 real-user testing remains deferred. This plan does not authorize a release, physical print, paid purchase, new license, or new infrastructure. A CUPS/IPP exchange, protocol result, compatibility probe, queue state, or reported job completion is not evidence that pages were physically produced.

## Ordered PLAN2AUTO units

Dispatch the remaining units in order. Unit 2 is already satisfied by evidence on `main`; it is included as a completed dependency and does not require another implementation unit.

### 1. `pre_u10_cross_platform_managed_ci` — current unit

**Work:** Run the existing locked restore, .NET core/probe/Avalonia builds, headless desktop and managed tests, Python compilation/unit suite, and applicable fixtures on hosted Ubuntu, Windows, and macOS runners. Each OS imports bounded synthetic documents through PaperBatch's production queue-import path and records queue status, renderer result, and per-format outcome. Retain OS-specific conditions: fresh pinned LibreOffice installation and bounded synthetic corpus on all three hosted OS lanes as separate engine-execution evidence; Windows package/PDFium/spooler evidence; and cross-platform managed fixtures.

**Acceptance:** The exact PR-head workflow runs the same application queue-import path on all three operating systems and passes; workflow YAML parses; platform-only steps run only on their intended OS. The application report shows PDF and PNG accepted with persisted PaperBatch artifacts, DOCX/XLSX/PPTX unsupported by the current production renderer composition, and malformed OOXML rejected before rendering. Each OS lane verifies the pinned LibreOffice installer before installation, runs only the bounded synthetic engine corpus, and publishes both exact-run reports. Report runner availability and OS-specific limits precisely. LibreOffice corpus success remains separate from PaperBatch import evidence. No physical output or human fidelity claim is made.

### 2. `pre_u10_exact_commit_evidence_access` — satisfied on main

**Basis:** PR #42 is merged to `main` as `9e7fc231c8341a8ba45dbfd7fde177f5c7a5a105` (`Expose exact-commit CI evidence in run summaries`). That commit adds bounded exact-run summaries and workflow/run/attempt identity for CI evidence.

**Disposition:** Satisfied by the existing main commit. Do not create duplicate work. Unit 1 continues to publish its app-import and LibreOffice reports and summaries with the same exact-run context.

### 3. `pre_u10_readiness_and_go_no_go`

**Depends on:** Unit 1 and the satisfied Unit 2 evidence.

**Work:** Reconcile exact-commit evidence against package provenance/contents and notices, document behavior, queue safety and ambiguous completion/recovery, privacy/redaction, offline behavior, and known migration/device gaps. Produce a final pre-U10 go/no-go report with pass, partial, blocked, and not-run states, evidence links, owners or prerequisites, and a bounded decision rationale. Keep U10 deferred for separate authorization.

**Acceptance:** Every claim maps to inspectable exact-run evidence and states what it cannot prove. Package, document, queue-safety, privacy, and recovery gates are individually dispositioned. The report makes a clear pre-U10 go/no-go recommendation and does not treat CUPS/IPP/protocol/probe/queue completion as proof of physical output. No release/tag, paid commitment, physical print, or U10 trial is initiated.

## Execution record

- Unit 1 is implemented in `.github/workflows/ci.yml` with production queue-import tests and the bounded LibreOffice engine corpus on the Ubuntu, Windows, and macOS matrix. Its exact-head hosted CI remains pending for this candidate. Windows-only packaging, PDFium, diagnostics, and spooler evidence remain scoped to Windows.
- The application-import report records the production queue row and renderer outcome for each synthetic fixture on every hosted OS. Current production rendering accepts PDF pass-through and built-in JPEG/PNG; DOCX, XLSX, and PPTX pass intake preflight but have no registered production renderer and are reported unsupported. Malformed OOXML is rejected before renderer selection.
- The separate LibreOffice evidence uses fresh 26.2.2.2 installers from official Document Foundation archives, with each package signature checked before installation. The bounded runner accepts only manifest-bound synthetic OOXML fixtures and reports OS, runner image, engine version, installer digest/signature provenance, outcomes, and evidence limits. This engine run does not prove that PaperBatch imports or renders those formats.
- Unit 2 is satisfied on `main` by PR #42 / commit `9e7fc231c8341a8ba45dbfd7fde177f5c7a5a105`; no additional exact-evidence-access unit is queued.
- Unit 3 remains planned and depends on Unit 1 evidence. U10 real-user testing remains deferred.
- Physical printing, human fidelity review, OEM support, and U10 real-user testing remain out of scope.
