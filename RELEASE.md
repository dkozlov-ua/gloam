# Release runbook

Gloam ships as a zip on this repository's GitHub Releases. Its own `install.ps1` installs
it into one fixed folder, and every later update replaces that folder in place. There is no
CI release and no code signing: releases are built and published from a local checkout,
the way [game-utils](https://github.com/dkozlov-ua/game-utils) does it.

## How it fits together

- **`package.ps1`** builds `Releases\Gloam-X.Y.Z\` (the staged package) and
  `Releases\Gloam-X.Y.Z.zip`. The package holds a self-contained, multi-file `dotnet publish`
  of `src\Gloam`, the bundled ArgyllCMS (checked against a pinned SHA-256), README, LICENSE,
  THIRD_PARTY_NOTICES and `install.ps1`. It adds `VERSION` and `manifest.txt`, which lists
  every file in the package. `scripts\Test-PublishOutput.ps1` checks the staged folder,
  including a launch smoke test, before the zip is written.
- **`publish.ps1`** checks that the release can go out, runs `package.ps1`, creates the
  release with `gh release create` (which also creates the tag on GitHub), and confirms that
  GitHub's SHA-256 of the asset matches the zip it built.
- **`install.ps1`** ships inside the package. Run from an unzipped package, it installs into
  `%LOCALAPPDATA%\Programs\Gloam` (or `-InstallDir`) and adds a *Gloam* shortcut to the
  Start menu. `-Package <zip> -Sha256 <hex>` is the update path, and `-Uninstall` removes an
  installation. Its header has the details.
- **Tray → Check for Updates** reads the repository's latest release. It offers the release
  when the `vX.Y.Z` tag is newer than the running version and the release carries
  `Gloam-X.Y.Z.zip` with a GitHub digest. *Install* downloads the zip into
  `%LOCALAPPDATA%\Gloam\Updates`, checks it against that SHA-256, and hands it to the
  installation's own `install.ps1`. Then Gloam exits. The installer checks the hash again,
  replaces the files **in the same folder**, and starts Gloam again. See `UpdateService`.

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

To try a package without publishing it, run `.\package.ps1`, then
`Releases\Gloam-X.Y.Z\install.ps1 -InstallDir <scratch folder> -NoShortcut`.

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

1. Download `Gloam-X.Y.Z.zip` from the release and unzip it.
2. `powershell -ExecutionPolicy Bypass -File install.ps1`

The folder is `%LOCALAPPDATA%\Programs\Gloam` and stays that way, so the Start with Windows
registration and the Start menu shortcut keep working across updates. Settings, calibration
reports and logs are in `%LOCALAPPDATA%\Gloam`, separate from the installation, so neither
an update nor an uninstall touches them. The installer's own log is
`%TEMP%\Gloam-install.log`.
