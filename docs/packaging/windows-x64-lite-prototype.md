# Windows x64 lite packaging prototype

This prototype publishes the current Avalonia desktop shell as a self-contained
.NET 10 `win-x64` directory and ZIP. On Windows with the .NET 10 SDK, run:

```powershell
./tools/package_windows_x64.ps1
```

The output is written to `artifacts/windows-x64-lite/`. It includes the
application files, the AGPL-3.0-only license, dependency license inventories,
.NET runtime third-party notices, package limitations, and
`package-manifest.json`. The manifest identifies the source revision and both
locked dependency files,
and records SHA-256 and size for each payload file before the ZIP is created.
The ZIP and manifest are excluded from that file list; the archive has a
separate `.zip.sha256` sidecar, which CI verifies against the ZIP bytes. CI builds
this package on a hosted Windows runner, verifies every manifest digest, and
keeps the unsigned development artifact for 14 days. Builds are from locked
NuGet dependencies with deterministic .NET compilation settings; the ZIP
timestamps are not guaranteed byte-identical across runs.

## Scope and limits

The package is a lite shell prototype, not a complete offline renderer. It does
not contain LibreOffice, Microsoft Office, WPS Office, PDFium, printer drivers,
or fonts. Those runtimes are intentionally absent: there is no reviewed,
license-cleared redistribution bundle for a full offline Windows runtime in
this prototype. The package omits PDFium, so preparation and preview can operate
only after the user supplies a separately trusted runtime using an explicit
`PAPERBATCH_PDFIUM_RUNTIME_PATH` and matching
`PAPERBATCH_PDFIUM_RUNTIME_SHA256`; the application verifies the digest before
loading it. The package does not provide or select that runtime. With PDFium
explicitly configured and a supported queue explicitly selected, the desktop
Print action submits the exact prepared PDF through the Windows GDI/Winspool
coordinator. Submission statuses report spooler facts only; cancellation is
best-effort for the exact receipt and cannot recall pages already emitted. The
package and its CI do not establish clean-PC installation, Office/WPS
compatibility, a verified printer route, or physical output.

The binary is unsigned. No paid signing certificate or signing service is
used. This is an unsigned development beta candidate, not a signed or production
release. No installer or automatic updater is provided. CI includes a
same-build package-directory swap simulation; it does not establish historical
version migration or a supported rollback contract. See
[u9-simulation-qualification.md](u9-simulation-qualification.md) for the full
qualification matrix and remaining gaps.

The UI and core dependency license inventories in `docs/dependencies/` are
metadata reviews for the locked NuGet graphs; the package also carries the
.NET runtime third-party notices supplied with the SDK. These are not a legal
review or complete binary-level software bill of materials. Recheck the exact
published files and their notices before any public binary release.
