# U9 final evidence and beta boundary

Reviewed 2026-10-24 at the U7 main commit `700adaffa5f4ab38a23eda27de8b3fea400290d2`. The machine-readable record is [u9-final-evidence.json](u9-final-evidence.json). On every CI run, the `u9-final-evidence-report` job adds the exact candidate commit, workflow run ID, attempt, and package/test/protocol job conclusions to a 14-day artifact. On pull requests, the report binds to the PR head SHA, not GitHub's synthetic merge ref.

## Exact-commit evidence

Each listed commit is the corresponding unit's merge commit on `main`. Both workflow runs completed successfully against that exact SHA. The U7 post-merge CI run ultimately passed on its third attempt: the Windows PDFium verification step failed on attempts 1 and 2 and passed on attempt 3 without a source change; the U9 simulation matrix passed on the first run.

| Unit | PR | Exact merge SHA | CI run | U9 simulation run |
|---|---:|---|---:|---:|
| U1 simulation lanes | 33 | `69e98641e833bbc1b118441d8ece8875daee18a2` | [39871418376](https://github.com/JohnXu22786/PaperBatch/actions/runs/39871418376) | [39871418429](https://github.com/JohnXu22786/PaperBatch/actions/runs/39871418429) |
| U2 package/session | 34 | `f9faecf9cd12af11f872ec1f8b1ab33727644708` | [39952651075](https://github.com/JohnXu22786/PaperBatch/actions/runs/39952651075) | [39952650984](https://github.com/JohnXu22786/PaperBatch/actions/runs/39952650984) |
| U3 migration | 35 | `405111589a56dab2730b8bf6698ee6d860c0c799` | [39954854949](https://github.com/JohnXu22786/PaperBatch/actions/runs/39954854949) | [39954855055](https://github.com/JohnXu22786/PaperBatch/actions/runs/39954855055) |
| U4 IPP matrix | 36 | `ca88b086b04eb00d7360b91c6d1c160dab24fe3c` | [39965358306](https://github.com/JohnXu22786/PaperBatch/actions/runs/39965358306) | [39965358414](https://github.com/JohnXu22786/PaperBatch/actions/runs/39965358414) |
| U5 Office/WPS harness | 37 | `a29a875baa6e380b210bc7f6a6aa9f26414409fe` | [39979366268](https://github.com/JohnXu22786/PaperBatch/actions/runs/39979366268) | [39979366356](https://github.com/JohnXu22786/PaperBatch/actions/runs/39979366356) |
| U6 document pipeline | 38 | `34404bf5a3a902adeb681c984cd8d09649f72efd` | [40050212215](https://github.com/JohnXu22786/PaperBatch/actions/runs/40050212215) | [40050212260](https://github.com/JohnXu22786/PaperBatch/actions/runs/40050212260) |
| U7 fault/security/stress | 39 | `700adaffa5f4ab38a23eda27de8b3fea400290d2` | [40052115422](https://github.com/JohnXu22786/PaperBatch/actions/runs/40052115422) | [40052115383](https://github.com/JohnXu22786/PaperBatch/actions/runs/40052115383) |

The Actions API listed retained, unexpired aggregate U9 reports for every simulation run. CI artifacts included the package, bounded Windows session report, synthetic LibreOffice report, PDFium proof, and Windows runner evidence as applicable; U7 also included Ubuntu/Windows fault-stress reports and PDFium diagnostics. Artifact IDs, names, byte sizes and expiration state are recorded by exact run in the JSON file. The archive downloads returned `Forbidden` from the Actions artifact host in this environment. Therefore this report records workflow conclusions and artifact metadata, but does not claim to have inspected archived report bodies or their runner/version fields.

## Results and evidence limits

| Domain | Result | Boundary |
|---|---|---|
| Q1 package/archive | Passed in CI | Unsigned development artifact; no signed publisher provenance or clean consumer PC proof. |
| Q2 session/offline | Partial | Hosted ephemeral runner, selected session roots and registry snapshots; only the two coordinator executables are firewall-blocked; GUI not launched. |
| Q3 upgrade/migration | Partial | SQLite schema migration, rollback and retry are exercised; both extracted package trees are the same current artifact, with no historical release or application-version rollback lifecycle. |
| Q4 document/rendering | Passed for bounded synthetic checks | Synthetic corpus and test-only PDFium/LibreOffice lanes; no human fidelity or Office/WPS compatibility result. |
| Q5 printer/IPP | Partial | Protocol and fake-spooler tests; no Windows real-queue submission, PaperBatch macOS support, or physical print evidence. |
| Q6 bounded fault/security | Passed for the fixed simulation matrix | Synthetic lifecycle/resource/security scenarios; no power-loss or fleet-scale evidence. |
| Q7 license/release | Partial | Tracked lockfiles and package notices are inventoried; this is not legal advice or a complete binary-level SBOM. |
| Office/WPS native | Blocked | No already available licensed isolated Windows 11 VM/snapshot; no native run occurred. |
| PaperBatch macOS | Unsupported | Hosted macOS protocol smoke is not PaperBatch macOS product evidence. |
| Physical pages | Not run | No physical printer job was submitted. |
| U10 real-user trials | Not run | Deferred and outside this unit. |

For scenario and lane details, see [U9 simulation qualification](u9-simulation-qualification.md), [U9 compatibility limits](../compatibility/u9-beta-compatibility.md), and the original [U9 plan](u9-comprehensive-simulation-plan.md). A green workflow means its configured jobs succeeded; it does not upgrade `partial`, `blocked`, `unsupported`, or `not_run` evidence to a product-support claim.

## License and redistribution inventory

- Project/source license: `AGPL-3.0-only` (`LICENSE`). The Windows package workflow verifies this declaration and checks for `LICENSE`, UI/core third-party notices, and the .NET `ThirdPartyNotices.txt`.
- Core lock graph: 6 unique external packages across 7 target entries in `src/PaperBatch.Core/packages.lock.json` (SHA-256 `83d43c94497ad0d0e81fef752db5080e92e5a42d4689da912f0ae45fe0487a1d`). The inventory identifies Microsoft.Data.Sqlite as MIT and SQLitePCLRaw as Apache-2.0.
- Desktop lock graph: 31 unique external packages across 41 target entries in `src/PaperBatch.Desktop/packages.lock.json` (SHA-256 `8cb8a61fcf84483be0ba188722fe5040ba5bb5e3b056339a30e9065d3388bb17`). The existing metadata inventory lists the Avalonia/runtime dependencies and their declared MIT terms, and ANGLE native assets with BSD-3-Clause terms.
- Desktop test lock graph: 33 unique external packages across 35 target entries (SHA-256 `fd0ae407673ff26695a18c59125204c62785fa9ded709255be5662633b71b99a`); it is test-only, not part of the package inventory.
- IPP lab build/runtime package locks and the Debian copyright inventory generator are `tools/ipp-lab/build-packages.lock`, `tools/ipp-lab/runtime-packages.lock`, and `tools/ipp-lab/create_license_inventory.py`; these describe the isolated CI lab, not Windows package contents.
- The lite package excludes Office, WPS, LibreOffice, PDFium, printer drivers, and proprietary fonts. Do not infer license or redistribution facts not stated in the locked metadata and notices. The CI package artifact was not downloaded for binary-level inspection.

The fuller package and dependency descriptions remain in [core runtime inventory](../dependencies/core-runtime.md) and [desktop UI inventory](../dependencies/desktop-ui.md). Neither inventory is a legal review or a complete binary SBOM.

## Beta decision

**No-go for end-user beta distribution or public release on the current evidence.** Keep the ZIP and reports as an unsigned development candidate. Q2, Q3 and Q5 remain partial; native Office/WPS is blocked; the artifact bodies could not be inspected here; and U10 real-user trials are deferred. The current evidence does not establish clean-PC behavior, a historical package upgrade lifecycle, physical output, Office/WPS compatibility, OEM driver support, or publisher trust.

Reconsider only after artifact contents and exact versions have been reviewed, the remaining Q2/Q3/Q5 boundary has either been completed or explicitly accepted by the project owner, and U10 has separate authorization. Do not create a release or tag, make a paid commitment, buy a certificate, print physically, or start U10 under this unit.
