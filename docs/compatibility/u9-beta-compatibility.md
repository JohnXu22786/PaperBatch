# U9 beta compatibility and limitations

This is a simulation-qualified, unsigned development beta candidate. The lite Windows package contains the PaperBatch Avalonia desktop shell, its locked managed dependencies, the .NET self-contained runtime, and required notices. It does not include LibreOffice, Microsoft Office, WPS Office, PDFium, printer drivers, or proprietary fonts.

| Area | Supported or exercised | Not established |
|---|---|---|
| Package | Windows x64 self-contained ZIP; manifest/hash validation; fresh temporary extraction in CI | Signed publisher identity, installer, elevation, clean consumer PC, automatic updates |
| Documents | PDF/image preparation with separately trusted digest-pinned PDFium; supported DOCX/XLSX/PPTX/ODF/RTF conversion through an explicitly pinned LibreOffice executable on Windows/Linux | Native Office/WPS opening or equivalence, office suite fidelity, all document features, production PDFium or LibreOffice redistribution |
| Printers | Explicit queue selection and Windows GDI/Winspool API behavior; simulated Linux CUPS/IPP fault protocol | OEM-driver compatibility, default-printer mutation, verified route, physical pages or quality |
| Persistence | Versioned SQLite runs and recipe schema compatibility as described in core contracts | Product installer migration, rollback of arbitrary historical app-data versions |
| Operating systems | Hosted Windows and Linux CI runners for their listed tests | Independent VM matrix, macOS support, clean end-user machines |

The lite package excludes LibreOffice and PDFium. Document conversion remains unavailable until a trusted LibreOffice engine path and matching SHA-256 are explicitly configured; PaperBatch does not discover or download an engine. Without an explicitly configured PDFium runtime path and matching SHA-256, page inspection, preview, composition, and PDF preparation are unavailable. LibreOffice conversion does not establish Microsoft Office/WPS equivalence or visual fidelity. Do not use Office/WPS or claim physical print completion from a successful export, IPP response, or spooler status. See [U9 simulation qualification](../packaging/u9-simulation-qualification.md) and [core contracts](../architecture/core-contracts.md).

This simulated U9 gate does not close G1, G3 physical, or G4 real-use claims. Real-user trial U10 remains deferred.
