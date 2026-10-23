# Gloam

Fix washed-out SDR content in Windows HDR mode. Per-monitor gamma correction, a perceptual night mode, and verified colorimeter calibration.

[![Latest release](https://img.shields.io/github/v/release/dkozlov-ua/gloam?sort=semver)](https://github.com/dkozlov-ua/gloam/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.txt)

![Detail of a candlelit painting shown twice. Left, decoded by Windows with the sRGB curve: the dark background washes out to a flat brown-grey haze. Right, with Gloam's Gamma 2.2 correction: the background returns to a deep black the figure sits against, while the candle flames stay exactly as bright.](docs/media/hero-comparison.png)

<sub>Rendered, not photographed: the source image is pushed through the same signal path the app implements (`TransferFunctions.cs`, `LutGenerator.cs`, and the Windows SDR-in-HDR wire model), once without Gloam and once with its Gamma 2.2 LUT, then re-encoded for an ordinary SDR screen. See [`scripts/render-comparison-images.py`](scripts/render-comparison-images.py). A screenshot cannot show this: the whole problem is that what the compositor sends to the panel differs from what gets captured.</sub>

Turn HDR on in Windows and SDR content goes flat and milky, because Windows presents it through a curve your display was never built to decode. Gloam puts the right curve back, per monitor.

## Quick start

1. Download `Gloam-X.Y.Z.zip` from the [latest release](https://github.com/dkozlov-ua/gloam/releases/latest), unzip it, and run `powershell -ExecutionPolicy Bypass -File install.ps1` in the unzipped folder. Per-user install, no administrator prompt.
2. Right-click the Gloam icon in the system tray.
3. Pick **Gamma 2.2** for the monitor you want to fix.

That is the whole fix. Everything below is optional.

## What It Does

- Fixes the Windows SDR-in-HDR gamma mismatch with per-monitor Gamma 2.2, Gamma 2.4, or Windows Default modes.
- Restores corrections automatically on startup, display changes, resume, and driver or game ramp resets.
- Provides a perceptual night mode built on CAT16 chromatic adaptation, with fixed-time or sun-position schedules, a manual override, perceptually uniform mired-space fades, and an Ultra Night profile for maximum blue suppression.
- Adds Game Lab, which switches between two display profiles: **Normal** (each monitor's own settings) and **Media** (one shared set with calibrated shadow visibility, gamma and brightness overrides, multi-display window targeting, and a Gameplay Lock that holds schedule-derived output steady during play). Media turns on while a game from your library is active and switches back to Normal afterwards.
- Lets scripts pick the profile: `Gloam.exe --profile-set normal`, `--profile-set media`, or `--profile-set auto`. A manual choice is sticky, survives restarts, and holds until `auto`.
- Offers an opt-in "Stay on Media while a library game is running, even without focus" setting under the Media profile's Advanced controls. Choose "One specific display" for apps such as MPV on a second monitor. A focused game takes priority; otherwise the first running game in library order is used. Background app starts and exits are detected within about three seconds.
- Checks the Windows-visible game signal chain at launch, including SDR/HDR expectation, DXGI color space, output bit depth, HDR headroom, and measured-calibration status. The dashboard keeps a live receipt of the active intent and every finding.
- Auto-disables night mode for chosen applications while they are in the foreground, either on every monitor or only on the displays the application's window covers. Gamma correction and installed calibration profiles stay active.
- Supports SDR and HDR colorimeter calibration through ArgyllCMS, including native Windows MHC2 profile installation and verification.
- Remembers calibration setup per monitor, including target, preset, meter correction, display type, and window position.
- Updates on request: **Check for Updates** in the tray installs a newer release in place.
- Runs locally. No accounts, no telemetry.

## Install

Each release carries one asset, `Gloam-X.Y.Z.zip`: a self-contained build with ArgyllCMS bundled.

1. Download it from the [latest release](https://github.com/dkozlov-ua/gloam/releases/latest) and unzip it.
2. Run `powershell -ExecutionPolicy Bypass -File install.ps1` in the unzipped folder.

Gloam installs into `%LOCALAPPDATA%\Programs\Gloam`, a fixed folder that every update replaces in place, and adds **Gloam** to the Start menu. No administrator prompt.

**Check for Updates** in the tray menu asks GitHub for the latest release. When that release is newer, Gloam shows its notes and offers **Install**. Install downloads the zip, checks it against the SHA-256 that GitHub publishes for it, closes Gloam, puts the new files into the same folder, and starts Gloam again. Nothing is checked unless you ask.

To uninstall, run `powershell -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\Programs\Gloam\install.ps1" -Uninstall`. Settings and calibration data in `%LOCALAPPDATA%\Gloam` are kept.

Release notes, checksums and older versions: https://github.com/dkozlov-ua/gloam/releases

Releases are not code-signed. `install.ps1` removes the downloaded-file mark from everything it installs, so SmartScreen does not stop `Gloam.exe` or the bundled ArgyllCMS tools.

## Use

![Gloam's tray menu, showing per-monitor gamma modes](docs/media/tray-menu.png)

1. Launch Gloam. It appears in the system tray.
2. Right-click the tray icon.
3. Pick a gamma mode per monitor:
   - Gamma 2.2 for general PC use.
   - Gamma 2.4 for dark-room video or BT.1886-style viewing.
   - Windows Default to bypass correction.
4. Enable **Start with Windows** if you want corrections restored at login.
5. Open the dashboard for monitor status, night mode, calibration, and diagnostics.

Something not behaving? See [Troubleshooting](docs/troubleshooting.md).

![The Gloam dashboard: per-monitor status, night mode, and calibration state](docs/media/dashboard.png)

Gloam has no global hotkeys. Left-click the tray icon to open the Dashboard.

## Night mode

![Gloam's night mode schedule editor](docs/media/night-mode.png)

Night mode warms the display on a schedule you set by fixed times or by sun position for your location. The shift is computed as a chromatic adaptation rather than a blue-channel cut, so neutrals stay neutral instead of going muddy, and transitions are paced in mired space so the change is perceptually even rather than fast at one end and slow at the other.

Ultra Night pushes further for maximum blue suppression, and can use a spectral sample of your own panel when you supply one.

## Game Lab

![Game Lab: the game library, the Media profile, and the live session receipt](docs/media/game-lab.png)

Game Lab has two profiles. **Normal** is each monitor's own settings. **Media** is one settings set shared by every game in your library.

1. Left-click the tray icon to open the Dashboard; Game Lab is the section below Displays.
2. The foreground game's executable is prefilled. Running-game choices refresh while the window is open.
3. Add games to the library. The checkbox on each row decides whether that game switches to Media.
4. Tune the Media profile on the right: dark-area visibility, brightness, and under Advanced the gamma, expected signal, target displays, and running policy. Edits save and apply automatically.
5. The **Profile** selector in the status strip is Automatic by default. Choose Normal or Media to hold one profile regardless of what is running.
6. The active-session receipt lists the gamma, shadow treatment, target display, and signal findings that Gloam can verify.

### Command line

```powershell
Gloam.exe --profile-set media    # hold Media
Gloam.exe --profile-set normal   # hold Normal
Gloam.exe --profile-set auto     # switch automatically again
```

A manual choice is saved and survives restarts until `auto` is set. When Gloam is already running, the command is forwarded to that instance. Otherwise Gloam starts with that profile. Gloam is a windowed app, so a shell doesn't wait for it. To read the exit code, use `Start-Process Gloam.exe -ArgumentList '--profile-set','media' -Wait -PassThru`. Exit codes: `0` applied, `1` could not be saved, `2` invalid arguments, `3` the running instance did not answer.

The implementation and measurement boundaries are documented in [Game Lab design](docs/gamer-mode-design.md).

## Calibration

![A Gloam calibration report: measured before/after, per-patch error, and the uncertainty budget](docs/media/calibration-report.png)

Connect a supported colorimeter and choose **Calibrate Display** from the tray.

Recommended flow:

1. Warm up the display for at least 30 minutes.
2. Pick the monitor, display type, target, and calibration preset.
3. Use a matching `.ccss` or `.ccmx` correction for QD-OLED, wide-gamut LCD, and other non-standard panels. The setup dialog can search the DisplayCAL community database and download one directly.
4. Run calibration. Gloam bypasses existing corrections while measuring.
5. Review the report. Gloam applies the profile and re-measures through it automatically.

For HDR desktop calibration, use **HDR Desktop PQ (sRGB gamut)**. When a panel already measures close to target, prefer white-point-only correction over full gamut correction: correcting a panel that is already accurate reliably verifies worse, not better.

Every number in the report is measured through the installed profile, not predicted. The method, and what it does not claim, is written up in the [whitepaper](https://getgloam.org/whitepaper).

### Hardware validation

Gloam's calibration model is pinned by golden fixtures: real recorded measurement sets replayed through the live pipeline on every test run, so a modeling change that would alter what the pipeline computes from real panel data fails the build.

- **Golden-fixture validated:** Gigabyte M27Q-P, MSI MAG271QPX28.
- **Expected to work, seeking validation data:** OLED, QD-OLED, wide-gamut LCD, mini-LED.

The second list is honest rather than modest. The code carries panel-specific handling for those classes, but no recorded fixture pins it. If you have one of those displays and a supported colorimeter, [contributing a golden fixture](docs/golden-fixture-contribution.md) is the single most valuable thing you can send us.

## Requirements

- Windows 10 or 11.
- HDR display for the main SDR-in-HDR gamma fix.
- No .NET runtime install required for official builds.
- Colorimeter optional. Calibration uses ArgyllCMS, which Gloam can download automatically.

## Build From Source

```powershell
git clone https://github.com/halideworks/gloam.git
cd gloam
dotnet run --project src/Gloam
```

Package locally:

```powershell
.\package.ps1 -Version X.Y.Z
```

Headless checks:

```powershell
dotnet run --project src/Gloam.Cli -- check-lut 2.2 200 --algorithm perceptual --temp-k 2700 --json artifacts\cli-lut-check.json
dotnet run --project src/Gloam.Cli -- night 2200 --algorithm ultra --basis rec2020 --json -
```

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Files

- Settings, logs, reports, corrections: `%LOCALAPPDATA%\Gloam`
- Installation: `%LOCALAPPDATA%\Programs\Gloam`

Use **Export Diagnostics** from the tray when reporting issues.

## License

Gloam is MIT licensed.

ArgyllCMS is AGPL v3. Gloam invokes ArgyllCMS tools as separate processes and does not link against AGPL code. See `THIRD_PARTY_NOTICES.txt`.

## Credits

Gloam began as a fork of Dylan Raga's [win11hdr-srgb-to-gamma2.2-icm](https://github.com/dylanraga/win11hdr-srgb-to-gamma2.2-icm) and retains his identification of the Windows SDR-in-HDR sRGB-to-gamma correction, the insight the whole application is built around. Gloam has since grown into a different program, but it started there, and that diagnosis was the hard part.

- [ArgyllCMS](https://www.argyllcms.com/), by Graeme Gill: the measurement and profiling tools Gloam drives for every calibration.
- [MHC2Gen](https://github.com/dantmnf/MHC2), by dantmnf: the reference for Windows MHC2 profile generation.

Formerly HDR Gamma Controller.
