# README media

The README shows one image, `hero-comparison.png`: Windows' sRGB decode next to Gloam's Gamma
2.2 correction. This file records how it was made.

The screenshots of the old tray menu, dashboard, night mode, Game Lab and calibration report
were removed with those features. A screenshot of the panel can come from
`FlyoutWindowTests`, which renders the real window to `%TEMP%\GloamFlyout.png`; screen
capture of WPF windows on an HDR desktop comes out blank.

## The hero image is a render

`hero-comparison.png` was made by pushing a public-domain painting (Georges de La Tour,
"The Penitent Magdalen", ca. 1640, The Metropolitan Museum of Art, object 436839, CC0) through the actual signal path (the PQ and sRGB
transfer functions, the gamma-2.2 LUT, and the Windows SDR-in-HDR wire model, all ported
1:1 from the app's own source), then re-encoding for an ordinary SDR viewer. The script and
source images lived with the website, which has been removed; the image is kept as it is.

This is deliberate, and the README says so beneath the image. A screenshot genuinely
cannot show this problem: what the compositor puts on the cable is not what a screen
capture records, so a capture of a washed-out HDR desktop looks identical to a capture of
a corrected one. A camera photograph of the panel would also work and would be more
visceral; if you shoot one, replace the file and update the note under the image in the
README.

## Privacy note

Check any capture for machine names, account names, display serials, and paths containing a
username.

## Conventions

- PNG, captured at 100% display scaling. Do not upscale a small capture.
- Window only. No desktop, no taskbar, no other application.
- Dark theme (it reads better against GitHub's page).
- Real data. A panel listing "Generic PnP Monitor" undersells the app more than no
  screenshot would.
