# Troubleshooting

Every entry here ends in something to actually do. If none of them fit, jump to
[Getting help](#getting-help). The diagnostics bundle is what makes a bug report
actionable.

---

## "Windows protected your PC" when installing

**What you are seeing:** a blue SmartScreen dialog with a **Don't run** button and no
obvious way past it.

**Why:** Gloam's releases are not code-signed, and SmartScreen flags unsigned setups it has
not seen downloaded at volume.

**Do this:** click **More info**, then **Run anyway**, for a setup you downloaded from the
[releases page](https://github.com/dkozlov-ua/gloam/releases). Updates installed from
Gloam itself are not stopped. Official builds come only from that page; if you got the setup
anywhere else, do not run it.

---

## I can't find Gloam's window

Gloam lives in the notification area. Left-click its icon to open the panel; it opens by the
clock and closes again when you click elsewhere or press Esc. If the icon is hidden in the
overflow (the **^** arrow), drag it onto the taskbar to keep it visible. Starting Gloam from
the Start menu while it is running also opens the panel.

At logon Gloam starts in the tray without opening the panel, by design.

---

## A game reset my gamma

**What you are seeing:** you alt-tab out of a fullscreen game and the desktop is flat,
oversaturated, or washed out.

**Why:** exclusive-fullscreen games write the hardware gamma ramp directly, and many do not
put it back on exit. So do some driver control panels and overlay tools.

**Do this:** nothing, usually. Gloam runs a ramp guard that reads the hardware ramp back on
a ten-second cadence and restores what it applied when something else stomps it, so the
correction should return on its own within a few seconds. Wait for it before intervening.

If it does not come back, or you need it back *now*, open the panel and use the refresh
button in the Display section: it finds the displays again and re-applies every one.

---

## Colors look wrong after a driver update

**What you are seeing:** a GPU driver update, and now everything is off, often more
saturated, or with a visible color cast.

**Why:** driver installs commonly reset the hardware gamma ramp, and can also reset or
re-enumerate the display. Vendor control panels (NVIDIA, AMD, Intel) also apply their own
color settings that compose with Gloam's, and a driver install can re-enable those.

**Do this, in order:**

1. Check the vendor control panel first. If its own digital vibrance, color enhancement, or
   custom gamma setting got turned back on, turn it off. Two corrections stacked will
   never look right, and Gloam cannot see or undo that one.
2. Use the refresh button in the panel's Display section to re-apply.
3. If the display was re-enumerated, Gloam may see it as a new monitor with no saved
   settings. Pick its gamma and brightness again.

---

## Colors are still corrected after I set gamma to 2.2 (or something else stacks)

Older Gloam versions could calibrate a display with a colorimeter and install an MHC2 color
profile for it. Calibration has been removed, but a profile installed earlier stays in
Windows, which applies it whether or not Gloam runs. To remove it, open **Color
Management** (`colorcpl`), choose the display on the **Devices** tab, and remove the profile
whose name starts with `Gloam`. For a display in HDR mode, also check **Settings > System >
Display > HDR**, where Windows lists the display's color profile.

Windows Night Light, and Gloam versions with night mode, are separate: Gloam no longer warms
the display or turns Night Light off.

---

## Brightness at 10–20% looks less dim than I asked for on an SDR display

Windows rejects gamma ramps that stray too far from neutral, and very low brightness on an
SDR display goes past that limit. Gloam then applies the closest ramp Windows accepts, so the
darkest settings come out a little brighter than asked, mostly near white. HDR displays are
not affected.

**Optional, for the full range on SDR:** the limit can be lifted with the registry override
f.lux and ArgyllCMS document. As Administrator, add a DWORD value named `GdiIcmGammaRange`
set to `0x100` under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM`, then sign out
and back in. Gloam detects the override and stops limiting the ramp.

---

## Where the files are

| What | Where |
| --- | --- |
| Settings and logs | `%LOCALAPPDATA%\Gloam` |
| Log file | `%LOCALAPPDATA%\Gloam\app.log` |
| Exported diagnostics bundles | `%LOCALAPPDATA%\Gloam\Diagnostics` |
| Installation | `%LOCALAPPDATA%\Programs\Gloam` |
| Update setup log | `%LOCALAPPDATA%\Gloam\Updates\Gloam-setup.log` |

The two directories are separate on purpose: uninstalling Gloam removes the installed files
from `Programs\Gloam` and leaves `Gloam`, so an uninstall never destroys your settings.

---

## Getting help

Click the bug button at the bottom of Gloam's panel to export diagnostics. It writes a zip
to `%LOCALAPPDATA%\Gloam\Diagnostics` containing the log, sanitized settings, and monitor and
display-configuration state, and opens the folder. In the log, the lines to look for are
`NativeGammaRamp: SetDeviceGammaRamp rejected` (a driver refused the ramp) and
`Windows' gamma-range validation limits the requested correction` (the limiter engaged).

The bundle is text-only and is sanitized before it is written, but it does describe your
displays and your configuration. Look through it before attaching it to a public issue if
that concerns you.

Then [open an issue](https://github.com/halideworks/gloam/issues/new/choose) and attach it.
A report with a diagnostics bundle can usually be diagnosed; one without it usually turns
into several rounds of questions that the bundle would have answered.
