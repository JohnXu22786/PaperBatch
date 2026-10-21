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

## Provenance and evidence gaps

Every fixture was authored in `tools/generate_document_corpus.py`. No customer or third-party documents, licensed fonts, vendor binaries, external renderer output, credentials or network resources are included. The manifest marks all entries `synthetic` and `simulation_only`; it currently records engine-generated, human-reviewed, Office/WPS, OEM-driver and physical-output evidence as absent. These fixtures do not close G1 or another compatibility gate. Add future engine outputs and human-reviewed references as separate evidence records, including source digest, engine/version and executable digest where available, operating system/build, exact command/settings, output digest and review/reference identifier.

ZIP hostile inputs must remain inert. Do not extract untrusted entries into shared paths or follow external relationships when testing them. These fixtures are bounded test data and are not a security certification or a substitute for a sandboxed archive parser.
