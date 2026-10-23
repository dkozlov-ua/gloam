# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Gloam is a Windows tray app (WPF, .NET 8) that fixes washed-out SDR content in Windows HDR mode with per-monitor gamma LUTs. It also provides a CAT16-based night mode, "Game Lab" Normal/Media profile switching driven by a game library, and ArgyllCMS-driven colorimeter calibration that installs native Windows MHC2 ICC profiles.

## Commands

The .NET 8 SDK is pinned by `global.json` (`rollForward: latestFeature`). Builds are Windows-only.

```powershell
dotnet run --project src/Gloam                                        # run the tray app
dotnet test src/Gloam.sln -c Release                                  # full suite (must pass before a PR)
dotnet test src/Gloam.sln -c Release --filter "FullyQualifiedName~LutGeneratorTests"   # one class
dotnet test src/Gloam.sln -c Release --filter "FullyQualifiedName~LutGeneratorTests.MethodName"
dotnet build src/Gloam.sln -c Release -p:GloamStrictBuild=true       # warnings-as-errors; must be clean
.\package.ps1 -Version X.Y.Z                                          # local Velopack package
python scripts/build-whitepaper-md.py                                 # regenerate site/whitepaper.md from site/whitepaper.html
```

Headless CLI (`src/Gloam.Cli`). Subcommands: `lut`, `sweep`, `compare`, `check-lut`, `night`, `golden-ingest`:

```powershell
dotnet run --project src/Gloam.Cli -- check-lut 2.2 200 --algorithm perceptual --temp-k 2700 --json artifacts\cli-lut-check.json
dotnet run --project src/Gloam.Cli -- night 2200 --algorithm ultra --basis rec2020 --json -
```

`src/Gloam.DebugMonitors` dumps `MonitorManager.EnumerateMonitors()` as JSON, which is useful for display-topology issues. `Gloam.exe --smoke` runs a launch smoke test against isolated data directories. `Gloam.exe --profile-set normal|media|auto` sets the sticky Game Lab profile selection and forwards it to a running instance over a named pipe (`ProfileCommand.cs`).

### Test notes

- Tests run serially (`DisableTestParallelization` in `tests/Gloam.Tests/AssemblyInfo.cs`).
- Some tests drive real WPF on an STA thread through `WpfTestHost`. They need an interactive desktop session and won't run headless.
- A module initializer redirects `AppPaths` to a per-process temp directory, so tests never touch the real `%LOCALAPPDATA%\Gloam`. Any new code that owns a path on disk must go through `AppPaths`, or it bypasses that protection.
- `Gloam.Core` and `Gloam` both have `InternalsVisibleTo Gloam.Tests`, so tests use `internal` seams freely.

## Architecture

Projects (dependency order): `Gloam.Interop` (raw P/Invoke for DisplayConfig, DXGI, GDI32, User32, and WCS) → `Gloam.Core` (all logic, no UI) → `Gloam` (WPF app), plus `Gloam.Cli` and `Gloam.DebugMonitors`, which both sit on Core.

### Startup

`src/Gloam/Program.cs` is the custom entry point (`<StartupObject>`). Velopack hooks must run before WPF starts. It also holds a single-instance mutex, because two instances would fight over the GPU gamma ramp and `settings.json`. `App.xaml.cs` is the DI composition root, where all core services are singletons. `TrayViewModel` is the central coordinator. Its constructor's wiring and start order is load-bearing: `NightModeService` and `AppDetectionService` fire events synchronously from `Start()`.

### Gamma apply pipeline (the core loop)

1. `GammaApplyService` owns apply policy. It resolves the effective per-monitor state (saved profile + night mode + app exclusions + active Game Lab session), coalesces rapid requests per monitor through `LatestValueCoalescer`, and persists changes.
2. `LutGenerator` builds 1024-point per-channel LUTs: PQ decode → SDR normalization → target gamma → linear-space adjustments (`ColorAdjustments`, night-mode CAT) → PQ encode, preserving HDR headroom. **Cached LUT arrays are shared and read-only. Never mutate a returned array.**
3. `DispwinRunner` writes the LUT to the GPU gamma ramp. It first tries the native `SetDeviceGammaRamp` path (`NativeGammaRamp`) and falls back to spawning ArgyllCMS `dispwin`. It skips identical re-applies to avoid flicker. Its "ramp guard" periodically reads the ramp back and restores it when games or drivers overwrite it, but it leaves displays alone while they are mid-calibration (`CalibrationStateManager` bypass).
4. `NativeGammaRamp` documents the Windows per-entry ramp envelope (`|(ramp[i]>>8) − i| ≤ 128`). One out-of-range entry silently rejects the whole ramp.

### Two correction layers

Calibration (`Gloam.Core/Calibration`) produces an MHC2 ICC profile (gamut matrix + tone LUTs) through `CalibrationProfileInstaller` / `Mhc2ProfileBuilder` / `HdrMhc2LutBuilder`. Windows' DWM applies that profile natively. The GPU gamma ramp then layers gamma and night mode on top. Profile association goes through `AdvancedColorProfileAssociation`. Its doc comment explains why the *default* association is the source of truth, not list membership, which reads empty on Windows 11 25H2. The MHC2 templates (`srgb_to_gamma2p2_*.icm` at the repo root) are embedded in the app and also copied to the output directory.

The measurement flow is: `CalibrationOrchestrator` (patch sequencing, adaptive settle, multi-read) → `ColorimeterService` / `SpotreadSession` (ArgyllCMS `spotread` process) → fit / refine (`HdrColorMatrixRefiner`, `HdrJointRefinement`, `ClosedLoopCorrector`, `DriftCompensator`) → install → verify (`CalibrationVerifier`, `TrustCheck`, `UncertaintyBudget`). The WPF side splits into coordinators under `src/Gloam/Services` (`CalibrationSessionCoordinator`, `VerificationCoordinator`, `HdrRefinementCoordinator`).

### Settings and data

`SettingsManager` → `SettingsPersistence` (atomic writes, size limit, refuses newer schema versions) → `SettingsNormalization` (migration and sanitizing on load). All data lives under `AppPaths.DataDir` (`%LOCALAPPDATA%\Gloam`). The legacy `HDRGammaController` folder name exists only for a one-time migration and must not change. The Velopack install root is the separate `%LOCALAPPDATA%\GloamApp`. The pack id `GloamApp` must stay identical in `package.ps1` and `.github/workflows/build.yml`.

## Contribution rules (from CONTRIBUTING.md / PR template)

- Changes under `src/Gloam.Core` need tests. Presentation-only changes under `src/Gloam` are exempt, but loops or conditionals in code-behind count as logic and belong in a testable place.
- **Calibration math** (`LutGenerator`, `ColorMath`, `HdrMhc2LutBuilder`, `DriftCompensator`, the refiner and planner classes) has a higher bar:
  1. Add pinning tests that capture current behavior *before* the behavioral change.
  2. Update `site/whitepaper.html` if documented behavior changes, then regenerate the `.md`. Never hand-edit `site/whitepaper.md`.
  3. State what was measured on hardware, or say that nothing was.
- **Golden fixtures** (`tests/Gloam.Tests/Fixtures/Golden/<panel>/`, replayed by `GoldenReplayTests` against `GoldenSampleBaseline`) pin what the pipeline computes from real panel data. If a change shifts them intentionally, regenerate with `dotnet run --project src/Gloam.Cli -- golden-ingest <fixtureDir>` so the diff is reviewable. Never loosen tolerances to make them pass.
- Never weaken, skip, or delete an existing test to make a change pass.
- Match the existing comment voice. Comments explain *why* and record invariants, often with measured evidence. Don't strip them.
- Style follows `.editorconfig`: LF endings, 4-space C#, block-scoped namespaces, braces preferred. Analyzers run at `latest-recommended`. Known legacy diagnostics are baselined in `Directory.Build.props` (`KnownAnalyzerBacklog`). Don't suppress whole categories.
- NuGet versions are centrally managed in `Directory.Packages.props`.

## Releases

See `RELEASE.md`. Pushing a `vX.Y.Z` tag triggers CI to build, sign (Azure Trusted Signing over OIDC), and publish a Velopack release to GitHub. Before tagging, bump `<Version>` in `src/Gloam/Gloam.csproj` to match and write `docs/release-notes/vX.Y.Z.md`. Notes must say plainly when a change affects calibration math or measured output.
