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

## Provenance and evidence gaps

Every fixture was authored in `tools/generate_document_corpus.py`. No customer or third-party documents, licensed fonts, vendor binaries, external renderer output, credentials or network resources are included. The manifest marks all entries `synthetic` and `simulation_only`; it currently records engine-generated, human-reviewed, Office/WPS, OEM-driver and physical-output evidence as absent. These fixtures do not close G1 or another compatibility gate. Add future engine outputs and human-reviewed references as separate evidence records, including source digest, engine/version and executable digest where available, operating system/build, exact command/settings, output digest and review/reference identifier.

ZIP hostile inputs must remain inert. Do not extract untrusted entries into shared paths or follow external relationships when testing them. These fixtures are bounded test data and are not a security certification or a substitute for a sandboxed archive parser.
