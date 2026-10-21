# U8 synthetic document corpus

`tests/fixtures/synthetic_documents/` contains deterministic, locally authored fixtures for structural parser and fault handling tests. The manifest includes provenance, byte length, SHA-256, scenario coverage tags, expected outcome and an explicit evidence class for each entry. Regenerate and run the focused checks with:

```sh
python3 tools/generate_document_corpus.py
python3 -m unittest tests/test_document_corpus.py
```

The generator uses the Python standard library only. OPC-like ZIP packages use stored entries with fixed timestamps and metadata; PDF bytes are assembled directly. The malformed archive fixtures are data only. Tests inspect archive names and declared sizes without extracting them, and no fixture script or document is launched.

## Coverage and expected outcomes

The current set covers synthetic DOCX sections/tables, multilingual text, field/form markers, comments and footnotes; XLSX hidden sheets, print area and mixed page setup; PPTX multilingual text and generic font family names; hand-authored PDF page syntax and a text annotation plus AcroForm widget; and fault cases for a truncated package, traversal name, duplicate ZIP entry, bounded 1 MiB member, opaque encrypted-container simulation, and a legacy OLE signature simulation. A separate external hyperlink relationship case represents a valid DOCX hyperlink/OPC relationship and has a no-network expected outcome; it is not classified as a malformed package. Outcomes marked `synthetic_only` describe structural intent without an engine claim; `rejected` marks hazardous or malformed packages; `unsupported` marks the encryption and legacy simulations. Those last entries are not valid encrypted or Office binary files. Feature structures inside the synthetic packages do not certify application interoperability.

The manifest count is a synthetic-input inventory only. Parameter changes do not count as compatibility evidence. The approved planning target is 150–300 public or sanitized real engine/device cases; the current corpus is 16 synthetic fixtures and remains below that target. The report does not represent real world compatibility coverage.

## Provenance and evidence gaps

Every fixture was authored in `tools/generate_document_corpus.py`. No customer or third-party documents, licensed fonts, vendor binaries, external renderer output, credentials or network resources are included. The manifest marks all entries `synthetic` and `simulation_only`; it currently records engine-generated, human-reviewed, Office/WPS, OEM-driver and physical-output evidence as absent. These fixtures do not close G1 or another compatibility gate. Add future engine outputs and human-reviewed references as separate evidence records, including source digest, engine/version and executable digest where available, operating system/build, exact command/settings, output digest and review/reference identifier.

ZIP hostile inputs must remain inert. Do not extract untrusted entries into shared paths or follow external relationships when testing them. These fixtures are bounded test data and are not a security certification or a substitute for a sandboxed archive parser.
