## What this changes

<!-- What it does and why. If it fixes an issue, link it. -->

## Checklist

- [ ] `dotnet test src/Gloam.sln -c Release` passes (it includes the UI tests, which need a desktop session).
- [ ] `dotnet build src/Gloam.sln -c Release -p:GloamStrictBuild=true` is warning-free.
- [ ] No existing test was weakened, skipped, or deleted to make this pass.
- [ ] Changes under `src/Gloam.Core` have tests. (Presentation-only changes are exempt.)

## LUT math

Only if this touches `LutGenerator` or `ToneAdjustments`. Delete this section otherwise.

- [ ] Pinning tests were added **before** the behavioral change, so the diff shows what moved
      (`LutPinningTests` holds the current output).
- [ ] The UI tests pass: `dotnet test tests/Gloam.UiTests -c Release`.

**What changes on screen?** Say which displays, modes and settings look different, and how.
