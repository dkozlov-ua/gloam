# Contributing to Gloam

Thanks for looking. Bug reports with a diagnostics bundle and small focused pull requests are
both genuinely useful.

## Build

```powershell
git clone https://github.com/halideworks/gloam.git
cd gloam
dotnet run --project src/Gloam
```

You need the .NET 10 SDK. `global.json` pins it, so a different major SDK will refuse rather
than silently building against something else.

Build a local setup (needs Inno Setup 6: `winget install JRSoftware.InnoSetup`):

```powershell
.\package.ps1 -Version X.Y.Z
```

## Run the tests

```powershell
dotnet test src/Gloam.sln -c Release
```

This must pass before you open a pull request. It runs two projects:

- `tests/Gloam.Tests`: the Core logic, plus the window's view models against a fake display,
  updater and Start with Windows switch. A few tests drive real WPF on an STA thread.
- `tests/Gloam.UiTests`: starts the real `Gloam.exe` with simulated displays and drives its
  window with UI Automation. Each run is its own app profile (`GLOAM_PROFILE`), so it never
  meets your running Gloam, your settings or your Start with Windows entry, and the
  simulator (`GLOAM_SIMULATOR`) records gamma ramps in a file instead of writing them to
  your GPU.

Both need a desktop session. They will not run headless.

Before submitting, also check the build is warning-free under the strict configuration.
There is no CI, so this check is yours to run:

```powershell
dotnet build src/Gloam.sln -c Release -p:GloamStrictBuild=true
```

## Pull requests

- **Changes under `src/Gloam.Core` need tests**, and so do view-model changes. That is where
  the LUT math, settings persistence and apply policy live, and it is all testable without a
  display. A change to what the window does belongs in a UI test as well.
- **Presentation-only changes are exempt.** XAML layout and styling under `src/Gloam` do not
  need tests unless they carry real logic. If you find yourself writing a loop or a
  conditional in a code-behind, that is logic, and it belongs somewhere testable.
- **Keep it focused.** One change per pull request. Drive-by refactors in a bug-fix PR make
  the fix harder to review and harder to revert.
- **Match the surrounding comment voice.** Comments in this codebase explain *why* and
  record invariants, not what the next line does. Please do not strip existing ones.

### LUT math

Changes to `LutGenerator` and `ToneAdjustments` change what every display shows, so:

1. **Add pinning tests first.** Tests that capture the current behavior, committed before
   the behavioral change, so the diff shows exactly what moved. `LutPinningTests` holds the
   current output.
2. **Say what looks different**, on which displays and modes, in the pull request and the
   release notes.

Do not weaken, skip, or delete an existing test to make a change pass. If a test is wrong,
that is its own pull request with its own argument.

## Reporting bugs

Use the [bug report template](.github/ISSUE_TEMPLATE/bug_report.yml). It asks for a
diagnostics bundle (the bug button at the bottom of Gloam's window), which is not
bureaucracy. Display problems depend on your Windows version, GPU driver, panel, and HDR
state, and the bundle carries all four. A report without one usually turns into several
rounds of questions.

Check [docs/troubleshooting.md](docs/troubleshooting.md) first; several common reports have
a known answer there.

## Security

Do not open a public issue for a security problem. See [SECURITY.md](SECURITY.md).
