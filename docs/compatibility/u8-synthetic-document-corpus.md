# U8 synthetic document corpus

`tests/fixtures/synthetic_documents/` contains deterministic, locally authored fixtures for structural parser and fault handling tests. The manifest includes provenance, byte length, SHA-256, scenario coverage tags, expected outcome and an explicit evidence class for each entry. Regenerate and run the focused checks with:

```sh
python3 tools/generate_document_corpus.py
python3 -m unittest tests/test_document_corpus.py
```

The generator uses the Python standard library only. OPC-like ZIP packages use stored entries with fixed timestamps and metadata; PDF bytes are assembled directly. The malformed archive fixtures are data only. Tests inspect archive names and declared sizes without extracting them, and no fixture script or document is launched.

## Coverage and expected outcomes

The inventory contains 166 deterministic files: 34 DOCX, 32 XLSX, 32 PPTX, 32 hand-authored PDF, 30 intentionally truncated ZIP specimens, and 6 additional base, inert, or unsupported examples. The 120 scaled cases vary authored text and document structure: DOCX paragraph counts and page geometry, XLSX row/value ranges, PPTX text-run counts, PDF page counts and page dimensions, and malformed ZIP truncation offsets. The malformed and unsupported specimens are inert data; the encrypted-container and legacy OLE entries are signatures/simulations, not valid encrypted or Office binary files. Feature structures inside synthetic packages do not certify application interoperability.

The manifest separately reports `syntheticFixtureCount` (166), `compatibilityEvidenceCount` (0), and `realEngineDeviceEvidenceCount` (0). Synthetic volume, parameter variation, and structural assertions do not count as compatibility evidence. The 150–300 target is a synthetic corpus size target only; no real engine/device case is included here. The report does not represent real world compatibility coverage.

## Resource stress profile

An opt-in bounded resource profile exercises the stable `SourceEnumerator` and `PreparedRun` APIs with 1,000 temporary synthetic file descriptors and 10,000 logical page mappings. It writes only tiny inert PDF-header fixtures to the system temporary directory, enumerates them, builds the engine-neutral prepared-run model, then removes the fixtures. It does not launch a renderer, create a print spool, or claim Office/WPS/driver throughput. Its `simulation-only` report records runtime, OS, architecture, CPU count, available-memory estimate, commit from CI or the current Git checkout (or `unreported` when neither is available), elapsed time, managed-memory delta and sampled peak, peak process working set, fixture bytes and logical counts. Managed memory is sampled every 100 fixtures/items; the report does not claim continuous allocation-profiler precision. `artifactBytes` is zero because no rendered artifact is produced.

Run it manually with:

```sh
dotnet run --project tests/PaperBatch.CompatibilityProbe.Tests/PaperBatch.CompatibilityProbe.Tests.csproj -- --resource-stress --report resource-stress-report.json
```

`--files` and `--pages` select workload size; `--max-files` and `--max-pages` set stricter per-run caps (the hard maxima are 1,000 files and 10,000 pages). The page count must divide evenly across files. `--budget-seconds` accepts 1–900 seconds (default 300). A budget expiry or Ctrl+C returns a nonzero exit and a JSON `cancelled` status after temporary fixtures are removed; fixture or enumeration failures also return nonzero with a failure status. Ordinary CI runs a small deterministic self-check only; it does not run the full stress profile or enforce machine-specific performance thresholds. This profile is a local synthetic model-construction measurement, not a final reference-machine baseline or compatibility evidence.

## Pinned LibreOffice synthetic corpus run

The standard Ubuntu, Windows, and macOS CI matrix runs the manifest-selected 30 scaled variants in each of DOCX, XLSX and PPTX with LibreOffice 26.2.2.2. Each lane verifies the exact downloaded installer with the Document Foundation detached signature before installing; reports also carry the installed engine binary SHA-256. Linux uses LibreOffice's launcher startup chain from an isolated program overlay and binds its `soffice.bin` entry to the sealed pinned engine image. Windows and macOS use their signed installer-provided launcher/runtime. A representative invocation is:

```sh
python3 tools/run_libreoffice_corpus.py \
  --engine-binary "$PAPERBATCH_LO_ENGINE_PATH" \
  --expected-engine-sha256 "$PAPERBATCH_LO_ENGINE_SHA256" \
  --timeout-seconds 60 --budget-seconds 900 \
  --output "$RUNNER_TEMP/paperbatch-lo-corpus-report.json"
```

Selection is by exact manifest ID pattern (`docx_variant_000` through `029`, likewise XLSX and PPTX), `accepted` outcome, synthetic/simulation-only metadata, path, byte length, and SHA-256. The runner also checks OPC member paths, duplicate names, CRCs, required format parts, XML well-formedness, macros, and external relationships before conversion. The hosted Linux lane uses a seccomp filter that denies socket and io_uring networking syscalls in the version probe and every conversion child and applies an OS file-size limit. Windows and macOS run only the same generated synthetic fixtures, reject external relationships and macro content before launch, and enforce per-file/batch timeouts, but do not claim Linux-equivalent per-process network or file-write sandboxing. The 90 selected files total less than the 64 MiB aggregate cap; each input is capped at 1 MiB, each package at 256 entries and 8 MiB uncompressed, each inspected PDF at 64 MiB, and each reported stdout/stderr field at 2,000 characters. Each input gets its own temporary profile, output directory, and intermediate directory, with TMPDIR, TMP, and TEMP set to that per-input directory for the LibreOffice child; its temporary working directory is removed after the output has been hashed and inspected. A per-file timeout is 60 seconds and the whole batch is capped at 900 seconds; one conversion failure is recorded and does not prevent later files from running. Reports include every input ID and manifest digest, engine version and binary digest, signed installer provenance, OS and runner-image identity, exit status, timeout, PDF size/digest and structural inspection result. When the trusted PDFium test library path and digest from the existing pinned CI setup are present, PDFium supplies the page count; otherwise the report records that PDFium inspection was unavailable while retaining the bounded structural PDF check. PDF output bytes may vary across hosts and are not compared with a golden digest. LibreOffice is engine-execution evidence only; it does not prove Microsoft Office/WPS compatibility, visual fidelity, OEM/device support, or physical output.

The manifest currently has 166 synthetic specimens. CI selects 90 variants (30 per format) and explicitly skips the other 76: 39 accepted non-variant or PDF records, 30 truncated ZIP variants, four other malformed/hazardous packages, two encrypted/legacy signature simulations, and one external-relationship DOCX. The report keeps these as skipped synthetic sources with per-record reasons. Counts for human-reviewed fidelity, Office/WPS, OEM/device, and physical output remain zero; compatibility evidence remains zero; G1, G3, G4, and U8 native/device/clean-PC evidence remain open. The JSON report is uploaded as a short-retention CI artifact and summarized in the workflow job summary. This is real LibreOffice execution over synthetic input only; it does not exercise PaperBatch's production queue-import path, prove that PaperBatch accepts or processes OOXML, provide fidelity review, show native Office/WPS compatibility, validate printers, or prove paper output. The hosted PaperBatch import report is separate and records that PDF/PNG are accepted with artifacts, OOXML is currently unsupported by the production renderer, and malformed OOXML is rejected.

## Provenance and evidence gaps

Every fixture was authored in `tools/generate_document_corpus.py`. No customer or third-party documents, licensed fonts, vendor binaries, external renderer output, credentials or network resources are included. The manifest marks all entries `synthetic` and `simulation_only`; it currently records engine-generated, human-reviewed, Office/WPS, OEM-driver and physical-output evidence as absent. These fixtures do not close G1 or another compatibility gate. Add future engine outputs and human-reviewed references as separate evidence records, including source digest, engine/version and executable digest where available, operating system/build, exact command/settings, output digest and review/reference identifier.

ZIP hostile inputs must remain inert. Do not extract untrusted entries into shared paths or follow external relationships when testing them. These fixtures are bounded test data and are not a security certification or a substitute for a sandboxed archive parser.
