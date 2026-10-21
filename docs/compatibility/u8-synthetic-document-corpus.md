# U8 synthetic document corpus seed

`tests/fixtures/synthetic_documents/` contains five small, synthetic inputs for later document-package and PDF parser tests: a DOCX with a table and two page sections, an XLSX with a hidden sheet and print area, a two-slide PPTX, a hand-authored two-page PDF, and a deliberately truncated DOCX package. The JSON manifest records each fixture's scenario, expected structural facts, byte size, and SHA-256 digest.

## Regenerate and verify

From the repository root, run:

```sh
python3 tools/generate_document_corpus.py
python3 -m unittest discover -s tests -p 'test_*.py'
```

The generator uses only the Python standard library. It writes OPC ZIP entries without compression and assigns fixed ZIP timestamps and metadata so output bytes do not depend on the current time or operating system. The PDF is assembled directly from a small fixed set of PDF objects; no renderer is launched. Use `--output-dir PATH` to generate a temporary copy for inspection.

## Provenance and limits

All fixture contents were authored by the generator for PaperBatch. They contain no personal or customer documents, copied online content, proprietary fonts, credentials, or vendor output. The office packages and PDF are structural test inputs. Their presence does not show that Microsoft Office, WPS, LibreOffice, PDFium, the PaperBatch application, a printer driver, or physical printing can open or render them correctly. The malformed DOCX intentionally ends before its ZIP central directory and is expected to be rejected as a package. These files do not close G1 or any compatibility gate.

This is a seed for U8's continuing work, not the planned 150–300 public or sanitized real-engine/device cases. Later engine outputs and human-reviewed references should be added as distinct evidence records without replacing these inputs. Each record should identify the source fixture digest, renderer and version, executable digest where available, operating-system/build, exact command and relevant settings, output digest, and review/reference identifier. A person must review any image or physical-output reference and record what it verifies. Keep licenses and redistribution provenance with imported material; exclude anything that cannot be shared safely. Synthetic fixtures, renderer outputs, human-reviewed references, and physical printer evidence must remain distinguishable in manifests and reports.
