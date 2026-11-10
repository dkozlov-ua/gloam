# Release runbook

Gloam ships as an Inno Setup setup, `Gloam-X.Y.Z-setup.exe`, on this repository's GitHub
Releases. It installs into one fixed folder, and every later setup replaces that folder in
place. There is no CI release and no code signing: releases are built and published from a
local checkout, the way [ups-tools](https://github.com/dkozlov-ua/ups-tools) does it.

Building needs the .NET SDK and Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

## How it fits together

- **`package.ps1`** stages `Releases\Gloam-X.Y.Z\` (a self-contained, multi-file
  `dotnet publish` of `src\Gloam`, the bundled ArgyllCMS checked against a pinned SHA-256,
  README, LICENSE and THIRD_PARTY_NOTICES), checks it with `scripts\Test-PublishOutput.ps1`
  (including a launch smoke test), then compiles `installer\Gloam.iss` over it into
  `Releases\Gloam-X.Y.Z-setup.exe`.
- **`publish.ps1`** checks that the release can go out, runs `package.ps1`, creates the
  release with `gh release create` (which also creates the tag on GitHub), and confirms that
  GitHub's SHA-256 of the asset matches the setup it built.
- **`installer\Gloam.iss`** installs per user, with no administrator prompt, into
  `%LOCALAPPDATA%\Programs\Gloam`, and adds a *Gloam* Start menu shortcut and an entry in
  Settings > Apps > Installed apps. Before it replaces files it waits for a running Gloam to
  exit (on its single-instance mutex); run by hand, it asks you to exit Gloam from the tray.
  Uninstalling also removes the Start with Windows registration when it points into the
  installation. Its header has the details.
- **Tray → Check for Updates** reads the repository's latest release. It offers the release
  when the `vX.Y.Z` tag is newer than the running version and the release carries
  `Gloam-X.Y.Z-setup.exe` with a GitHub digest. *Install* downloads the setup into
  `%LOCALAPPDATA%\Gloam\Updates`, checks it against that SHA-256, and runs it with
  `/SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1`. Then Gloam exits. The setup replaces the
  files **in the same folder** and starts Gloam again. Its log is
  `%LOCALAPPDATA%\Gloam\Updates\Gloam-setup.log`. See `UpdateService`.
- **An installation** is a folder with the setup's `unins000.exe` beside `Gloam.exe`. Only an
  installation updates itself or takes over the Start with Windows registration; a dev run is
  told to install the release instead.

The repository the app checks is `UpdateService.UpdateRepo`. `publish.ps1` refuses to
publish anywhere else, because releases published elsewhere would never be found.

**The repository must be public for the check to work.** The check sends no token, and
GitHub answers a private repository's release request with 404. The app says exactly that.

## Cutting a release

1. Make sure `main` has everything to ship and these pass:
   - `dotnet test src/Gloam.sln -c Release`
   - `dotnet build src/Gloam.sln -c Release -p:GloamStrictBuild=true`
2. Pick `X.Y.Z` and set `<Version>` in `src\Gloam\Gloam.csproj` to it. The app compares
   releases by these three numbers only.
3. Write the notes at `docs\release-notes\vX.Y.Z.md` (see below).
4. Commit and push `main`.
5. Publish:
   ```powershell
   .\publish.ps1
   ```
   Before building anything, it refuses on a dirty tree (untracked files included), a HEAD
   that is not on `origin/main`, a `<Version>` that disagrees, missing notes, or a release or
   tag of that version that already exists. It needs `gh` signed in with push rights to the
   repository.
6. Check it from an installed copy: *Check for Updates* in the tray should offer it.

To try a setup without publishing it, run `.\package.ps1`, then run
`Releases\Gloam-X.Y.Z-setup.exe`. It installs over this PC's installation, like an update.
Inno Setup's standard switches work (`/SILENT`, `/VERYSILENT`, `/LOG=<file>`).

### Release notes

`publish.ps1` makes the notes the release body, and *Check for Updates* shows them as plain
text above *Install*. Write them for someone deciding whether to install now: what changed
for them, and what they need to do about it (usually nothing).

State plainly when a change affects calibration math or measured output. That is the one
category of change where a user may want to re-run a calibration after updating.

## Pre-release validation checklist

- [ ] Full test suite passing.
- [ ] Strict build clean: `dotnet build src/Gloam.sln -c Release -p:GloamStrictBuild=true`.
- [ ] `<Version>` in `Gloam.csproj` is the version being published.
- [ ] Release notes written at `docs/release-notes/vX.Y.Z.md`.
- [ ] **Colorimeter re-validation**, only when the release changes measured behavior
      (calibration math, LUT generation, drift handling, refinement). Verify the specific
      changed path on real hardware and record the before/after in the release notes.
      Skip it for releases that do not touch those paths, and say so rather than leaving
      the box ambiguously unticked.

## Installing for the first time

Download `Gloam-X.Y.Z-setup.exe` from the release and run it. The setup isn't signed, so
SmartScreen may ask first: *More info*, then *Run anyway*.

The folder is `%LOCALAPPDATA%\Programs\Gloam` and stays that way, so the Start with Windows
registration and the Start menu shortcut keep working across updates. Settings, calibration
reports and logs are in `%LOCALAPPDATA%\Gloam`, separate from the installation, so neither
an update nor an uninstall touches them. Uninstall from Settings > Apps > Installed apps.
