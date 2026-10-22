# Beta candidate release checklist

- [ ] Start from the reviewed commit and clean worktree; record source revision.
- [ ] Build the Windows x64 lite package from locked dependency files in hosted Windows CI.
- [ ] Verify manifest, lock digests, per-file hashes/sizes, ZIP path safety, exact extraction list, and archive sidecar with `tools/verify_windows_package.py`.
- [ ] Confirm package carries AGPL-3.0-only and UI/core/.NET runtime notices.
- [ ] Confirm excluded engines, Office/WPS, PDFium, drivers and fonts remain absent.
- [ ] Run applicable managed tests, Linux CUPS/IPP simulation, pinned renderer checks and Windows package CI on the exact candidate commit.
- [ ] Publish evidence with the explicit simulation boundaries in `docs/packaging/u9-simulation-qualification.md`.
- [ ] Label any retained artifact **unsigned development/beta**. Do not imply publisher trust, installer support, clean-PC validation, Office/WPS compatibility, driver compatibility, or physical output.
- [ ] Keep public signed release and production download claims blocked until a reviewed signing/provenance plan and remaining compatibility evidence exist.
- [ ] Keep U10 real-user testing deferred until separately authorized and planned.

- [ ] Do not call U9 qualification complete while Q2 or Q3 is marked partial or Q5 remains partial; either complete those simulations on the exact candidate or carry the remaining gaps into an explicitly limited beta decision.
