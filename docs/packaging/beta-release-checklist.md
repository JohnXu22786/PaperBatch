# Beta candidate release checklist

## U9 decision recorded 2026-10-24

**No-go for end-user beta distribution or public release.** Keep the Windows x64 ZIP and CI reports as an unsigned development candidate. Q2 session/offline, Q3 package-version migration, and Q5 printer/IPP evidence remain partial; native Office/WPS testing is blocked; physical printing was not run; U10 real-user trials remain deferred. The reasons and exact-run references are in [U9 final evidence](u9-final-evidence.md) and [its machine-readable report](u9-final-evidence.json).

This checklist separates verified simulation evidence from open release requirements. A green CI workflow establishes only the checks configured in that workflow. The current U8 PR must still pass two independent full cumulative reviews and all exact-head CI before Batch considers its merge; that delivery gate is recorded on the PR and its attached exact-candidate CI report.

## Evidence checked on U1-U7 exact commits

- [x] Windows x64 lite package built in hosted Windows CI from locked dependencies; package manifest, hashes, ZIP safety, extraction, and required notice checks passed on the recorded exact commits.
- [x] Package declares `AGPL-3.0-only`, is marked unsigned, and the package workflow requires the source, UI/core, and .NET runtime notices.
- [x] Office, WPS, LibreOffice, PDFium, printer drivers, and proprietary fonts are documented as excluded from the lite package.
- [x] Applicable synthetic document, renderer, Linux CUPS/IPP, Windows fake-spooler, package session, and bounded fault/security jobs have successful exact-commit workflow records listed in [U9 final evidence](u9-final-evidence.md).
- [x] U9 evidence lists scenario boundaries and unresolved gaps. The archived report artifacts were listed by the Actions API but could not be downloaded in this environment; do not claim that their internal runner/version records were independently inspected.
- [x] Public signed release and production-download claims remain blocked; U10 real-user testing remains deferred.

## Still required before reconsidering end-user beta distribution

- [ ] Review the exact-run package and report artifact contents, including actual runner, client, endpoint, and software versions.
- [ ] Complete the agreed clean-client/session evidence or retain the Q2 restriction.
- [ ] Exercise a distinct historical package through a product-level upgrade/rollback lifecycle, or retain the Q3 restriction.
- [ ] Complete applicable Windows endpoint evidence and any separately authorized physical/user scope, or retain the Q5 restriction.
- [ ] Obtain separate authorization and a plan for U10 real-user trials before conducting them.

## Still required before public signed release

- [ ] Review and approve a signing/provenance plan and verify package redistribution rights for every included binary.
- [ ] Resolve remaining compatibility, license/redistribution, and beta-boundary decisions without presenting simulations as native support.
- [ ] Create no release or tag until a separate authorized release workflow passes.

Do not call U9 product qualification complete while Q2, Q3, or Q5 remains partial. This report records the limitations and makes a no-go decision for end-user distribution; it does not convert those partials into passes.
