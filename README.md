# Gloam

Fix washed-out SDR content in Windows HDR mode. Per-monitor gamma correction and brightness, with a second profile for media, from a small panel by the clock.

[![Latest release](https://img.shields.io/github/v/release/dkozlov-ua/gloam?sort=semver)](https://github.com/dkozlov-ua/gloam/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.txt)

![Detail of a candlelit painting shown twice. Left, decoded by Windows with the sRGB curve: the dark background washes out to a flat brown-grey haze. Right, with Gloam's Gamma 2.2 correction: the background returns to a deep black the figure sits against, while the candle flames stay exactly as bright.](docs/media/hero-comparison.png)

<sub>Rendered, not photographed: the source image was pushed through the same signal path the app implements (`TransferFunctions.cs`, `LutGenerator.cs`, and the Windows SDR-in-HDR wire model), once without Gloam and once with its Gamma 2.2 LUT, then re-encoded for an ordinary SDR screen. A screenshot cannot show this: the whole problem is that what the compositor sends to the panel differs from what gets captured.</sub>

Turn HDR on in Windows and SDR content goes flat and milky, because Windows presents it through a curve your display was never built to decode. Gloam puts the right curve back, per monitor.

## Quick start

1. Download `Gloam-X.Y.Z-setup.exe` from the [latest release](https://github.com/dkozlov-ua/gloam/releases/latest) and run it. Per-user install, no administrator prompt.
2. Click the Gloam icon in the system tray. Its panel opens by the clock.
3. Under **Display**, pick **2.2** for the monitor you want to fix.

That is the whole fix. Everything below is optional.

## What it does

- Fixes the Windows SDR-in-HDR gamma mismatch per monitor, with gamma 2.2 (everyday) or 2.4 (dark room).
- Dims each monitor in software, with a curve that keeps shadow detail, and fades HDR highlights with it.
- Keeps two profiles. **Normal** is each monitor's own settings. **Media** layers a set of overrides on top: gamma, brightness, and dark-area visibility, on every display or one.
- Lets scripts switch profiles: `Gloam.exe --profile-set media` and `--profile-set normal`.
- Restores the correction on startup, display changes, resume, and when a game or driver resets the gamma ramp.
- Updates on request: **Check for updates** installs a newer release in place.
- Runs locally. No accounts, no telemetry.

## Use

Left-click the tray icon to open Gloam's panel; click it again, press Esc, or click elsewhere to close it. Every setting is in the panel and takes effect as you change it. Right-click the icon for a menu with the two profiles and Exit.

- **Profile.** Normal or Media. The choice is kept across restarts.
- **Display.** With several monitors, pick one from the list first.
  - **Gamma.** 2.2 for general PC use, 2.4 for dark-room video or BT.1886-style viewing.
  - **Brightness.** 10–100%. **Keep shadow detail when dimming** dims with a curve that keeps near-black detail visible; turn it off for a straight dim.
- **Media profile.** What Media changes while it is on. Tick **Gamma** or **Brightness** to override the monitor's own value. **Dark-area visibility** lifts the darkest tones and leaves everything above them unchanged. **Applies to** limits Media to one display.
- **Start with Windows**, **Check for updates**, export diagnostics (the bug button), and Exit are at the bottom.

Starting Gloam again from the Start menu opens the panel of the Gloam that is already running.

Something not behaving? See [Troubleshooting](docs/troubleshooting.md).

### Switching profiles from scripts

```powershell
Gloam.exe --profile-set media    # switch to Media
Gloam.exe --profile-set normal   # back to Normal
```

The choice is saved and survives restarts. `auto`, from scripts written for the old automatic switching, means `normal`. When Gloam is already running, the command is forwarded to it. Otherwise the command starts Gloam in the tray with that profile and exits once it confirms, within 20 seconds. Gloam is a windowed app, so a shell doesn't wait for it. To read the exit code, use `Start-Process Gloam.exe -ArgumentList '--profile-set','media' -Wait -PassThru`. Exit codes: `0` applied, `1` could not be saved, `2` invalid arguments, `3` the running instance did not answer (or a newly started one did not come up in time).

## Install

Each release carries one asset, `Gloam-X.Y.Z-setup.exe`: the setup for a self-contained build. Download it from the [latest release](https://github.com/dkozlov-ua/gloam/releases/latest) and run it.

Gloam installs into `%LOCALAPPDATA%\Programs\Gloam`, a fixed folder that every update replaces in place, and adds **Gloam** to the Start menu and to Settings > Apps > Installed apps. No administrator prompt.

**Check for updates** asks GitHub for the latest release. When that release is newer, Gloam shows its notes and the button becomes **Update to X.Y.Z**. Updating downloads the setup, checks it against the SHA-256 that GitHub publishes for it, closes Gloam, runs the setup, and starts Gloam again. Nothing is checked unless you ask.

To uninstall, use Settings > Apps > Installed apps. Settings in `%LOCALAPPDATA%\Gloam` are kept.

Release notes, checksums and older versions: https://github.com/dkozlov-ua/gloam/releases

Releases are not code-signed, so SmartScreen may stop a setup downloaded through a browser: choose **More info**, then **Run anyway**. Updates installed from Gloam are not affected.

## Requirements

- Windows 10 or 11. The panel follows the Windows light or dark setting and accent colour.
- HDR display for the main SDR-in-HDR gamma fix.
- No .NET runtime install required for official builds.

## Build from source

```powershell
git clone https://github.com/halideworks/gloam.git
cd gloam
dotnet run --project src/Gloam
```

Build the setup locally (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php): `winget install JRSoftware.InnoSetup`):

```powershell
.\package.ps1 -Version X.Y.Z
```

Headless LUT checks:

```powershell
dotnet run --project src/Gloam.Cli -- check-lut 2.2 200 --brightness 50 --json -
dotnet run --project src/Gloam.Cli -- lut 2.4 240 --shadow 40 --sdr
```

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Files

- Settings and logs: `%LOCALAPPDATA%\Gloam`
- Installation: `%LOCALAPPDATA%\Programs\Gloam`

Export diagnostics (the bug button in the panel) when reporting issues.

## License

Gloam is MIT licensed. See `THIRD_PARTY_NOTICES.txt` for the components it ships.

## Credits

Gloam began as a fork of Dylan Raga's [win11hdr-srgb-to-gamma2.2-icm](https://github.com/dylanraga/win11hdr-srgb-to-gamma2.2-icm) and retains his identification of the Windows SDR-in-HDR sRGB-to-gamma correction, the insight the whole application is built around. Gloam has since grown into a different program, but it started there, and that diagnosis was the hard part.

The panel's layout follows [G-Helper](https://github.com/seerge/g-helper), by seerge.

Formerly HDR Gamma Controller.
