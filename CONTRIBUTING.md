# Contributing

PaperBatch is an early AGPL-3.0-only project. Contributions should keep user data and source documents safe, make status and failure behavior explicit, and avoid claiming physical print proof from operating-system or protocol acknowledgements.

## Build and verify

Use the .NET 10 SDK and Python 3.12. Restore locked NuGet dependencies and run focused managed checks:

```sh
dotnet restore tests/PaperBatch.CompatibilityProbe.Tests/PaperBatch.CompatibilityProbe.Tests.csproj --locked-mode -p:NuGetAudit=false
dotnet run --project tests/PaperBatch.CompatibilityProbe.Tests/PaperBatch.CompatibilityProbe.Tests.csproj -p:NuGetAudit=false
python3 -m unittest tests.test_package_verifier tests.test_libreoffice_probe
```

For Linux print-protocol simulation, Docker is required:

```sh
tools/run_ipp_fault_lab.sh test
```

Windows package creation is CI-qualified and requires Windows plus the .NET 10 SDK:

```powershell
./tools/package_windows_x64.ps1
python -m unittest tests.test_package_verifier
python tools/verify_windows_package.py artifacts/windows-x64-lite artifacts/windows-x64-lite/PaperBatch-windows-x64-lite.zip
```

Do not commit generated artifacts, downloaded renderer binaries, real user documents, printer identifiers, or secrets. Synthetic fixtures belong under `tests/fixtures/` with reproducible generation instructions and a manifest. Keep external runtimes digest-pinned and test-only unless redistribution, licensing, and notices have been reviewed.

## Changes and evidence

Add a focused regression test for changed risky behavior. Describe commands, observed results, and evidence limits in the pull request. Simulation results must be labeled; do not represent CUPS/IPP, Windows spooler status, synthetic conversion, or hosted runners as physical output, Office/WPS compatibility, clean-PC proof, or user trials. U10 real-user testing is deferred.

Keep source files immutable unless an operation explicitly creates a user-approved output. Preserve idempotency and unknown/review states after ambiguous submission. Do not add automatic retries that could duplicate jobs. Include license and copyright changes for new dependencies and runtime assets; never bundle an engine, font, driver, or proprietary application without explicit scope and verified rights.
