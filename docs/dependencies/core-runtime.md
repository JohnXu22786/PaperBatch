# Core runtime dependency inventory

Checked against the resolved NuGet package metadata and the Core lockfile on
2026-10-21. The Windows x64 self-contained desktop output contains the shared
core and its SQLite dependency graph. Package versions and content hashes are
recorded in `src/PaperBatch.Core/packages.lock.json`.

| Package | Version | NuGet declared license |
| --- | ---: | --- |
| `Microsoft.Data.Sqlite` | `10.0.0` | MIT |
| `Microsoft.Data.Sqlite.Core` | `10.0.0` | MIT |
| `SQLitePCLRaw.bundle_e_sqlite3` | `2.1.11` | Apache-2.0 |
| `SQLitePCLRaw.core` | `2.1.11` | Apache-2.0 |
| `SQLitePCLRaw.lib.e_sqlite3` | `2.1.11` | Apache-2.0 |
| `SQLitePCLRaw.provider.e_sqlite3` | `2.1.11` | Apache-2.0 |

The NuGet license expressions are available at [MIT](https://licenses.nuget.org/MIT)
and [Apache-2.0](https://licenses.nuget.org/Apache-2.0). The published package
also contains the .NET runtime's `ThirdPartyNotices.txt`; it is copied from the
same .NET SDK installation used to build the self-contained output. This is a
metadata inventory, not a legal review or complete binary-level SBOM.
