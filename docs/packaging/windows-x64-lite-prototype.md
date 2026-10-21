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
this prototype. PDFium-dependent preview/preparation remains unavailable until
the user configures a separately trusted runtime, and production print
submission is not implemented. The package does not establish clean-PC
installation, Office/WPS compatibility, a printer route, or physical output.

The binary is unsigned. No paid signing certificate or signing service is
used. This is a CI verification artifact, not a stable or beta release. No
installer, upgrade, migration, or rollback behavior is claimed.

The UI and core dependency license inventories in `docs/dependencies/` are
metadata reviews for the locked NuGet graphs; the package also carries the
.NET runtime third-party notices supplied with the SDK. These are not a legal
review or complete binary-level software bill of materials. Recheck the exact
published files and their notices before any public binary release.
