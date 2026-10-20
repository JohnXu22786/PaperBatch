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
