# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Gloam is a Windows tray app (WPF, .NET 10) that fixes washed-out SDR content in Windows HDR mode with per-monitor gamma LUTs and software brightness. It has three display profiles: Normal (each monitor's own settings) and Media (overrides layered on top), which both turn Windows HDR on, and SDR (each monitor's own settings with HDR off), switchable from its window, its tray menu, or `Gloam.exe --profile-set`. Its UI is one G-Helper-style flyout that opens by the tray, styled with WPF's Fluent theme (the same resources as [ups-tools](https://github.com/dkozlov-ua/ups-tools)).

## Commands

The .NET 10 SDK is pinned by `global.json` (`rollForward: latestFeature`). Builds are Windows-only.

```powershell
dotnet run --project src/Gloam                                        # run the tray app
dotnet test src/Gloam.sln -c Release                                  # full suite, UI tests included (must pass before a PR)
dotnet test tests/Gloam.Tests -c Release --filter "FullyQualifiedName~LutGeneratorTests"   # one class
dotnet test tests/Gloam.UiTests -c Release                            # the UI tests only
dotnet build src/Gloam.sln -c Release -p:GloamStrictBuild=true       # warnings-as-errors; must be clean
.\package.ps1                                                        # release setup: Releases\Gloam-X.Y.Z-setup.exe (needs Inno Setup 6)
.\publish.ps1                                                        # package this commit and publish it as a GitHub release
.\scripts\New-AppIcon.ps1 -PreviewPath $env:TEMP\icons.png          # redraw app.ico (Normal) and the tray-only app-media.ico, app-sdr.ico
```

Headless CLI (`src/Gloam.Cli`). Subcommands: `lut`, `sweep`, `compare`, `check-lut`:

```powershell
dotnet run --project src/Gloam.Cli -- check-lut 2.2 200 --brightness 50 --json -
dotnet run --project src/Gloam.Cli -- lut 2.4 240 --shadow 40 --sdr
```

`src/Gloam.DebugMonitors` dumps `MonitorManager.EnumerateMonitors()` as JSON, which is useful for display-topology issues. `Gloam.exe --smoke` runs a launch smoke test against isolated data directories. `Gloam.exe --profile-set normal|media|sdr` sets the sticky profile selection and forwards it to a running instance over a named pipe (`ProfileCommand.cs`); `auto` is accepted as `normal` for old scripts. `Gloam.exe --background` starts in the tray without opening the window (logon, an update's relaunch).

### Test notes

- `tests/Gloam.Tests` holds unit tests plus view-model tests against fakes (`Support/Fakes.cs`: a gamma ramp, monitors, an updater, Start with Windows). `tests/Gloam.UiTests` starts the real `Gloam.exe` and drives its window with UI Automation, in the manner of ups-tools.
- Tests run serially (`DisableTestParallelization`). The two test assemblies still run at the same time under `dotnet test src/Gloam.sln`, so every test that puts a window on the desktop (`FlyoutWindowTests`, each UI test's `AppUnderTest`) holds `DesktopLock` (`tests/Shared`), a named semaphore: otherwise a unit test's window steals the foreground and the UI test's flyout, which hides on deactivation, disappears.
- WPF tests (`FlyoutWindowTests`, which also saves the rendered flyout to `%TEMP%\GloamFlyout.png`) and all UI tests need an interactive desktop session and won't run headless.
- A module initializer redirects `AppPaths` to a per-process temp directory, so tests never touch the real `%LOCALAPPDATA%\Gloam`; `TempDataScope` gives a test its own. Any new code that owns a path on disk must go through `AppPaths`, or it bypasses that protection.
- Two environment variables make an app the UI tests can drive without touching the user's: `GLOAM_PROFILE=<name>` (`AppProfile`) gives it its own data folder (`%LOCALAPPDATA%\Gloam-<name>`), single-instance mutex, show signal, `--profile-set` pipe and Run value; `GLOAM_SIMULATOR=<monitors.json>` (`Simulator`) replaces the real displays and records every ramp in `<monitors.json>.ramps.json` instead of writing to a GPU.
- Screen capture of WPF windows on this HDR desktop comes out blank; look at the UI through `FlyoutWindowTests`' PNG instead.
- `Gloam.Core` and `Gloam` both have `InternalsVisibleTo Gloam.Tests`, so tests use `internal` seams freely.

## Architecture

Projects (dependency order): `Gloam.Interop` (raw P/Invoke for DisplayConfig, DXGI, GDI32, User32, DWM) → `Gloam.Core` (all logic, no UI) → `Gloam` (WPF app), plus `Gloam.Cli` and `Gloam.DebugMonitors`, which both sit on Core.

### Startup

`src/Gloam/Program.cs` is the custom entry point (`<StartupObject>`). The `--smoke` and `--profile-set` modes and the single-instance mutex all decide before WPF starts. The mutex exists because two instances would fight over the GPU gamma ramp and `settings.json`; a second plain launch sets the running instance's show signal, which opens its window. `App.xaml.cs` composes the services by hand (`App.CreateServices`), creates the flyout (`MainWindow`) and the tray icon (`TrayIcon`, Hardcodet), and hooks display changes and resume through `SystemEvents`. `MainViewModel` is the coordinator: it owns the monitor list and re-applies whenever settings change, from any thread, by posting to the UI thread.

### The window

`MainWindow` is a captionless flyout (`WindowChrome` with a zero-height caption, so it keeps the system frame and rounded corners). It opens in the tray corner of the primary taskbar (`FlyoutPlacement`), hides on deactivation or Esc, and is never closed while the app runs. Its sections: Profile (Normal/Media/SDR), Display (gamma and brightness per monitor), Media profile, and a footer (Start with Windows, updates, diagnostics, Exit). Every control saves at once; the settings event that follows applies it. Theme: `ThemeMode="System"` in `App.xaml` with the ups-tools styles; no custom palette.

### Gamma apply pipeline (the core loop)

1. `GammaApplyService` owns apply policy. `Resolve` takes the monitor's saved profile and, while Media is selected and covers the monitor, layers the `MediaProfile` on it. Requests are coalesced per monitor through `LatestValueCoalescer`, and a 10 s ramp guard restores ramps something else overwrote.
2. `LutGenerator` builds 1024-point LUTs: PQ decode → SDR normalization → target gamma → `ToneAdjustments` (dark-area visibility toe, then dimming) in linear light → PQ encode, preserving HDR headroom. Every adjustment is neutral, so the three channels are equal. The app always dims linearly with no toe: `GammaApplyService.Resolve` overrides `UseLinearBrightness` and `ShadowDetailStrength`, whatever settings.json holds, since the panel no longer offers either (the generator and the CLI still support both). **Cached LUT arrays are shared and read-only. Never mutate a returned array.**
3. `GammaRampWriter` writes the LUT through `IGammaRamp` (`NativeGammaRampDevice`, i.e. `SetDeviceGammaRamp`) and skips identical re-applies to avoid flicker.
4. `NativeGammaRamp` documents the Windows per-entry ramp envelope (`|(ramp[i]>>8) − i| ≤ 128`). One out-of-range entry silently rejects the whole ramp.

### HDR mode

`HdrModeSwitcher` puts every HDR-capable display in the selected profile's mode (HDR for Normal and Media, off for SDR) through `IHdrSwitch`: `NativeHdrSwitch` calls `DisplayConfig.SetHdrState` (Windows 11 24H2's `SET_HDR_STATE`, falling back to `SET_ADVANCED_COLOR_STATE`), and the `Simulator` rewrites its monitor file. `MainViewModel` runs it in the background at startup and on a profile switch only, never after a display change, so a mode set by hand holds until the next switch and Gloam's own mode change cannot loop. After a switch it lists and applies the displays again, since the LUT depends on the mode.

### Settings and data

`SettingsManager` → `SettingsPersistence` (atomic writes, size limit, refuses newer schema versions) → `SettingsNormalization`. Schema 6 holds monitor profiles (gamma, brightness, linear dimming), the Media profile and the profile selection; fields of removed features in older files are ignored, and unknown enum names read as the default (so a saved `Automatic` selection is Normal). All data lives under `AppPaths.DataDir` (`%LOCALAPPDATA%\Gloam`). The legacy `HDRGammaController` folder name exists only for a one-time migration and must not change. The installation is the separate `%LOCALAPPDATA%\Programs\Gloam` (see Releases), so updates and uninstalls never touch user data.

## Contribution rules (from CONTRIBUTING.md / PR template)

- Changes under `src/Gloam.Core` need tests, and so do view models; UI behaviour belongs in the UI tests. Presentation-only XAML is exempt, but loops or conditionals in code-behind count as logic and belong in a testable place.
- **LUT math** (`LutGenerator`, `ToneAdjustments`) has a higher bar: `LutPinningTests` pins its output. Add pinning tests before a behavioral change, and say in the release notes what looks different.
- Never weaken, skip, or delete an existing test to make a change pass.
- Match the existing comment voice. Comments explain *why* and record invariants, often with measured evidence. Don't strip them.
- Style follows `.editorconfig`: LF endings, 4-space C#, block-scoped namespaces, braces preferred. Analyzers run at `latest-recommended`. Known legacy diagnostics are baselined in `Directory.Build.props` (`KnownAnalyzerBacklog`). Don't suppress whole categories.
- NuGet versions are centrally managed in `Directory.Packages.props`.

## Releases

See `RELEASE.md`. There is no CI and no code signing: `publish.ps1` builds the release locally and publishes it with `gh`. Before publishing, bump `<Version>` in `src/Gloam/Gloam.csproj` (X.Y.Z only; it is the one version) and write `docs/release-notes/vX.Y.Z.md`. Notes must say plainly when a change affects what the displays show.

What the code relies on:

- **The release is an Inno Setup 6 setup**, `Gloam-X.Y.Z-setup.exe`, compiled by `package.ps1` from `installer\Gloam.iss` over the staged `dotnet publish`. It installs per user (no elevation) into one fixed folder, `%LOCALAPPDATA%\Programs\Gloam`, and every update lands in the *same* folder, so the HKCU Run value and the Start menu shortcut never need repointing. Never change the `.iss` `AppId`. Its `[InstallDelete]` removes what older releases shipped (ArgyllCMS, ICM templates).
- **`unins000.exe` beside `Gloam.exe` marks an installation.** `UpdateService.IsInstallation` checks it to tell an installation from a dev run. It gates self-update and taking over the Start with Windows registration (`"Gloam.exe" --background`, repaired at startup when stale).
- **Updates happen on a click, never on a timer.** *Check for updates* (`UpdateViewModel` over `UpdateService`) offers the latest release only when its tag is `vX.Y.Z` and newer, and only when it carries `Gloam-X.Y.Z-setup.exe` served from under `UpdateService.DownloadUrlPrefix` with a GitHub `sha256:` digest. The app checks the download, re-hashes it through a write-denying handle, runs it with `/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1 /LOG=...` and exits. The setup waits on the single-instance mutex for Gloam to exit and starts it again in the tray. `publish.ps1` refuses an `UpdateRepo` that is not the repository it publishes to. The check sends no token, so the repository must be public.
- `installer\Gloam.iss` repeats app identities in Pascal (mutex name, `/relaunch` switch, Run value). `InstallerScriptTests` pins them to the C# constants. Keep the `.iss` ASCII.
