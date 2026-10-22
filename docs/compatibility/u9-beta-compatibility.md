# U9 beta compatibility and limitations

This is a simulation-qualified, unsigned development beta candidate. The lite Windows package contains the PaperBatch Avalonia desktop shell, its locked managed dependencies, the .NET self-contained runtime, and required notices. It does not include LibreOffice, Microsoft Office, WPS Office, PDFium, printer drivers, or proprietary fonts.

| Area | Supported or exercised | Not established |
|---|---|---|
| Package | Windows x64 self-contained ZIP; manifest/hash validation; fresh temporary extraction in CI | Signed publisher identity, installer, elevation, clean consumer PC, automatic updates |
| Documents | PDF pass-through and supported JPEG/PNG preparation when a separately trusted digest-pinned PDFium runtime is configured; synthetic OOXML export evidence in Linux CI | Native Office/WPS opening, office suite fidelity, all document features, production PDFium redistribution |
| Printers | Explicit queue selection and Windows GDI/Winspool API behavior; simulated Linux CUPS/IPP fault protocol | OEM-driver compatibility, default-printer mutation, verified route, physical pages or quality |
| Persistence | Versioned SQLite runs and recipe schema compatibility as described in core contracts | Product installer migration, rollback of arbitrary historical app-data versions |
| Operating systems | Hosted Windows and Linux CI runners for their listed tests | Independent VM matrix, macOS support, clean end-user machines |

Without an explicitly configured PDFium runtime path and matching SHA-256, preview and PDF preparation requiring PDFium are unavailable. The app does not download a runtime or silently substitute one. Do not use Office/WPS or claim physical print completion from a successful export, IPP response, or spooler status. See [U9 simulation qualification](../packaging/u9-simulation-qualification.md) and [core contracts](../architecture/core-contracts.md).

This simulated U9 gate does not close G1, G3 physical, or G4 real-use claims. Real-user trial U10 remains deferred.
