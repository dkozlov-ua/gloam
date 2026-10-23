# Game Lab: calibrated display intents for games

Game Lab switches between two display profiles. **Normal** is each monitor's own settings, with no Game Lab layer. **Media** is a single settings set shared by every game in the library. Media applies when a library executable owns the foreground window. The feature stays in Gloam's existing output path: Windows display discovery, measured per-monitor calibration, linear-light color adjustment, MHC2 or gamma-ramp application, and the latest-value coalescer. It does not inject a DLL, hook a swap chain, install an in-process overlay, or inspect game memory.

That boundary matters. Anti-cheat systems see an ordinary desktop color-management tool, and every claim in the dashboard comes from a Windows output fact or Gloam's measured display model.

## Profiles and selection

Library entries hold identity only: executable name, verified path, title, an enable flag, detected HDR capability, and last-used time. Rendering and policy live in the Media profile. A session composes the two, so the apply pipeline still receives one complete rule carrying the game's identity and Media's settings.

The profile selection is one of:

- **Automatic** (default). Media applies while an enabled library game is active, and Normal applies otherwise. The Dashboard's pause stops this switching.
- **Normal**, chosen manually. No Game Lab session runs, even when a library game is focused.
- **Media**, chosen manually. Media applies without a game. With no game window to intersect, window targeting widens to every display; a specific-display target still applies. The foreground app plays no part, so focus changes never restart the session or move its Gameplay Lock. The Dashboard's pause doesn't override this explicit choice.

A manual choice is persisted and survives restarts until Automatic is selected again. The panic suspend still clears everything, and choosing a profile again re-arms output.

The same choice is available from the Dashboard's Profile selector and from the command line:

```text
Gloam.exe --profile-set normal|media|auto
```

A second launch can't run alongside the tray because of the single-instance mutex. It forwards the command over a named pipe scoped to the login session and restricted to the current user, then exits with `0` (applied), `1` (could not be saved), `2` (invalid arguments) or `3` (the running instance did not answer within 10 seconds). When no instance is running, the launch becomes the tray and persists the choice before the first evaluation.

Settings schema 5 introduced this model. Loading an older file seeds Media from the most recently used enabled per-game profile, or from the default preset when no game was ever activated, and then reduces every library entry to its identity.

## Session path

1. The foreground hook reports the executable and its window bounds.
2. The Media profile selects the affected displays: window intersection, every connected display, or one named display.
3. Gloam captures a session record for each selected display and runs the signal checks.
4. Media contributes gamma, linear-light shadow visibility, brightness, and its night policy to the normal calibration settings.
5. The existing coalescer applies only the newest state. The ramp guard continues to restore corrections after a driver or game clears them.
6. Leaving the game clears the session and restores Normal, unless Media uses "Stay on Media while a library game is running, even without focus".

The optional running policy checks enabled library games every two seconds and verifies their executable paths. It keeps Media on until the game exits, including while minimized when targeting one specific display or all displays. Window targeting uses the running process's main window bounds. A focused game takes priority; otherwise the first running game in library order owns the session. Repeated checks preserve the captured Gameplay Lock state. Pause bypasses running games too.

Game Lab is a section of the Dashboard. Field edits are coalesced for 180 ms, persisted, and sent through the foreground resolver. Running executable choices refresh every 2.5 seconds without resetting typed text or an open picker. Repeated focus events preserve the original session time and locked color temperature.

## Picture intents

| Intent | Gamma policy | Shadow visibility | Night policy | Signal expectation |
|---|---|---:|---|---|
| Reference | Monitor setting | Off | Daylight | Automatic |
| Competitive Clarity | Gamma 2.2 | 55% | Follow schedule | Automatic |
| Cinematic HDR | Windows Default | Off | Daylight | HDR required |
| Night Ops | Gamma 2.2 | 45% | Spectral Night Ops | Automatic |
| Custom | User controlled | User controlled | User controlled | User controlled |

Intents are presets for the Media profile. Changing the intent reapplies its preset. Custom preserves the current fields. Gamma override can be disabled when the monitor's ordinary gamma setting should remain in force.

## Shadow visibility curve

Competitive visibility is evaluated in linear light before brightness, chromatic adaptation, per-channel trim, and the installed display correction. `x` is the wire-basis relative luminance, using sRGB coefficients on SDR and Rec.2020 coefficients on HDR. For strength `s` and pivot `p`:

```text
y = x + 1.5 s x (1 - x/p)^2    when 0 < x < p
y = x                           otherwise
```

Black remains black. The curve meets the identity at the pivot with a matching first derivative, so the protected midtones do not acquire a seam. Its minimum slope is `1 - 0.5s`; even the maximum setting remains strictly increasing. RGB channels share one luminance-derived gain, capped by available channel headroom, which preserves hue and saturation. The generated SDR and HDR LUTs are tested for finite, bounded, monotonic output.

This curve lifts encoded shadow separation without raising the black floor or flattening the rest of the image. A measured calibration remains active underneath it, which keeps the adjustment tied to the connected panel instead of assuming that every display leaves black at the same code value.

## Night policies

- **Follow schedule** uses the current night-mode trajectory.
- **Force daylight** holds the game at 6500 K.
- **Night Ops** selects the warmer of the scheduled state and the profile target, uses perceptual spectral adaptation, and disables luminance preservation. Its melanopic-EDI ceiling is optional and disabled in the preset. Enabling it passes the chosen limit to the circadian governor, which may change brightness and white point to comply.

Night Ops therefore retains the existing CCSS-aware dose model when a spectrum is available. Its melanopic number inherits the same provenance and uncertainty rules as the main night dashboard.

Gameplay Lock captures the resolved kelvin when a session starts. Timer and sunrise changes continue to run internally, while their effective display state remains fixed and the LUT deduper prevents redundant hardware writes. A manual preview may temporarily bypass the game policy, and the user's global night-mode disable remains final authority.

## Launch checks and the active receipt

The launch checks currently cover:

- Media expecting HDR while Windows HDR is inactive;
- Media expecting SDR while Windows HDR is active;
- disagreement between Advanced Color state and the DXGI output color space;
- an HDR output below 10 bits per component;
- missing or implausibly small reported HDR headroom;
- an SDR gamma override inside an active HDR/PQ output path;
- absence of an installed measured calibration;
- a disabled Gameplay Lock.

The dashboard groups findings by display and grades them as information, warning, or check-required. These diagnostics describe the path from Windows to the connector. A game's private tone mapper, internal paper-white slider, dynamic metadata, frame limiter, VRR state, and monitor OSD remain opaque to a desktop process. Paper-white and peak fields are stored as game-menu guidance until a measured HDR characterization can be linked directly to the game profile.

## Failure and recovery behavior

Settings are schema-versioned, sanitized on load, and returned as deep snapshots. Invalid enum values fall back to safe presets; numeric fields are finite and clamped; duplicate executable rules resolve deterministically. A missing named display falls back to window targeting during sanitization.

Game Lab uses the same calibration bypass as the rest of Gloam. Calibration measurements bypass temporary game rendering. Resume and display-topology events re-enumerate monitors, refresh the foreground assignment, and rebuild sessions against the new signal facts.

## Research backlog

The next useful advances require measurements or platform access that the first implementation cannot invent:

- **HDR calibration passport.** Attach a display's measured knee, window peak, full-frame peak, and ABL curve to each game. Translate those measurements into the exact menu vocabulary used by that title, then verify a short patch sequence after setup.
- **Probe-assisted visibility threshold.** Measure the user's panel and room at several near-black levels, find the lowest reliable separation under motion-adapted viewing, and solve the smallest monotonic toe that reaches it.
- **Adaptation-aware session start.** Use ambient illuminance and the previous desktop state to choose a sub-JND transition into the game profile, avoiding a pupil shock when a dark title opens from a bright desktop.
- **VRR-aware write windows.** If Windows exposes reliable compositor timing, schedule the rare unavoidable ramp update outside sensitive frame intervals. Hardware capture has to prove that this reduces visible discontinuities.
- **Flash and fatigue budget.** Combine calibrated luminance, APL history, session duration, and user sensitivity settings into a local exposure receipt. Any automatic intervention would require conservative medical and accessibility review.
- **Signed community recipes.** Store title-specific HDR-menu semantics and known engine quirks as versioned data, with measurements and author provenance attached to each recommendation.

Each item preserves the same rule used by the shipped launch checks: facts from the platform are labeled as observed, values from an instrument are labeled as measured, and everything inferred from a model carries its assumptions into the UI.
