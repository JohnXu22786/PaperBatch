# Desktop UI package inventory

Checked against NuGet package metadata and embedded license files on 2026-10-18. The exact direct and transitive versions and package hashes are recorded in the two `packages.lock.json` files.

## Direct dependencies

| Package | Version | License |
| --- | ---: | --- |
| `Avalonia` | `12.1.4` | MIT |
| `Avalonia.Desktop` | `12.1.4` | MIT |
| `Avalonia.Themes.Fluent` | `12.1.4` | MIT |
| `Avalonia.Headless` (presentation test runner) | `12.1.4` | MIT |

These references use Avalonia's open-source desktop and headless packages. No paid PDF control or proprietary UI package was added.

## Resolved transitive packages

The lockfiles resolve Avalonia runtime packages at `12.1.4`, `Avalonia.BuildServices` at `11.3.2`, `HarfBuzzSharp` and its native-asset packages at `8.3.1.3`, `SkiaSharp` and its native-asset packages at `3.119.4`, `MicroCom.Runtime` at `0.11.6`, and `Tmds.DBus.Protocol` at `0.94.1`. The platform-specific HarfBuzzSharp and SkiaSharp assets include Linux, Windows, macOS, and WebAssembly packages.

NuGet metadata lists MIT for the Avalonia packages, BuildServices, MicroCom.Runtime, HarfBuzzSharp, SkiaSharp, and Tmds.DBus.Protocol. `Avalonia.Angle.Windows.Natives` `2.1.27548.20260419` carries a BSD 3-Clause license file. HarfBuzzSharp and SkiaSharp native packages include their MIT license files and third-party notices; those notices include HarfBuzz's MIT terms and ANGLE's BSD terms. Preserve the relevant notices if these native assets are redistributed in a future installer.

License evidence:

- [Avalonia package source at the published commit](https://github.com/AvaloniaUI/Avalonia/tree/2d79ba55fae1b1e71c69f93a668080c9029e6a31) and [MIT license](https://licenses.nuget.org/MIT)
- [Avalonia BuildServices source](https://github.com/AvaloniaUI/Avalonia.BuildServices)
- [MicroCom source](https://github.com/kekekeks/MicroCom)
- [HarfBuzz source](https://github.com/harfbuzz/harfbuzz)
- [SkiaSharp source](https://github.com/mono/SkiaSharp)
- [ANGLE BSD 3-Clause license](https://github.com/google/angle/blob/main/LICENSE)

This inventory covers the UI and headless-test dependency graph only. It is not a shipping-binary inventory; future packaging must recheck the exact included assets and preserve their notices. It does not close the separate PDFium, office-suite, driver, or printer compatibility gates.

## U6 prepared artifact boundary

The production shell registers PDF pass-through and supported JPEG/PNG snapshot renderers. With an explicitly configured PDFium path and matching SHA-256, the shared preparation coordinator prepares PDF/JPEG/PNG sources, uses the configured PDFium inspector to reject source pages containing forms or annotations, composes and verifies the final PDF, and persists it in the private content-addressed artifact store. The final artifact identity is linked to version-2 `PreparedRun` metadata. Version-1 saved runs remain readable but have no final artifact and are rejected for submission. Unsupported or uninspectable source content fails closed.

The preparation result enters the presentation through `DesktopShellViewModel.RegisterPreparedRun(queueItemId, run)`, which registers the immutable run with the preview source. Re-preparation or removal calls `InvalidatePreparedRun`, clearing the current preview and disposing its prior bitmap. When `PAPERBATCH_PDFIUM_RUNTIME_PATH` and its matching `PAPERBATCH_PDFIUM_RUNTIME_SHA256` are explicitly configured, the desktop preview opens and revalidates that run's final artifact, rasterizes one bounded page, and displays the resulting image. Without both the persisted final artifact and configured runtime, preview stays explicitly unavailable. The CI-pinned PDFium runtime is test evidence only and is not registered in the application by default. With PDFium explicitly configured and a supported queue explicitly selected, the Print action submits the exact prepared PDF through the Windows GDI/Winspool coordinator. Statuses report spooler facts only; cancellation is best-effort for the exact receipt and cannot recall pages already emitted. The desktop provides effective-settings provenance for batch/recipe defaults, the first matching recipe rule, and per-file overrides; these values do not imply printer capability or submission. Test backends receive the same verified final artifact stream and are simulation-only; OS/backend status never means physical pages were confirmed.
