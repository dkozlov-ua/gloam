using System;

namespace Gloam.Core
{
    /// <summary>
    /// Which display profile owns the output. Normal is each monitor's own settings. Media
    /// layers the single <see cref="MediaProfile"/> on top of them. The choice is sticky: it
    /// survives restarts until another profile is chosen.
    /// </summary>
    /// <remarks>
    /// Normal must stay the zero value: settings.json stores the name, and an unknown name
    /// (such as the retired "Automatic") reads back as the default.
    /// </remarks>
    public enum DisplayProfileSelection
    {
        Normal,
        Media
    }

    /// <summary>Which connected displays the Media profile applies to.</summary>
    public enum MediaDisplayScope
    {
        AllDisplays,
        SpecificDisplay
    }

    /// <summary>
    /// The Media profile: overrides layered on each monitor's own settings while Media is
    /// selected. Anything not overridden keeps the monitor's value. While Media is active it
    /// holds the displays it covers; Normal restores each monitor's settings untouched.
    /// </summary>
    public sealed class MediaProfile
    {
        public bool OverrideGamma { get; set; }
        public GammaMode GammaMode { get; set; } = GammaMode.Gamma22;

        /// <summary>Replace the monitor's brightness while Media is active.</summary>
        public bool OverrideBrightness { get; set; }

        /// <summary>Brightness percentage (10..100) used when <see cref="OverrideBrightness"/> is set.</summary>
        public double Brightness { get; set; } = 100.0;

        /// <summary>Strength of the black-anchored dark-area visibility toe, 0..1.</summary>
        public double ShadowDetailStrength { get; set; }

        public MediaDisplayScope DisplayScope { get; set; } = MediaDisplayScope.AllDisplays;

        /// <summary>The display <see cref="MediaDisplayScope.SpecificDisplay"/> targets.</summary>
        public string? MonitorDevicePath { get; set; }

        public MediaProfile Clone() => new()
        {
            OverrideGamma = OverrideGamma,
            GammaMode = GammaMode,
            OverrideBrightness = OverrideBrightness,
            Brightness = Brightness,
            ShadowDetailStrength = ShadowDetailStrength,
            DisplayScope = DisplayScope,
            MonitorDevicePath = MonitorDevicePath,
        };

        /// <summary>Returns a copy with every field in its valid range.</summary>
        public MediaProfile Sanitized()
        {
            var copy = Clone();
            copy.GammaMode = Enum.IsDefined(copy.GammaMode) ? copy.GammaMode : GammaMode.Gamma22;
            copy.Brightness = ClampFinite(copy.Brightness, 10.0, 100.0, 100.0);
            copy.ShadowDetailStrength = ClampFinite(copy.ShadowDetailStrength, 0.0, 1.0, 0.0);
            copy.DisplayScope = Enum.IsDefined(copy.DisplayScope) ? copy.DisplayScope : MediaDisplayScope.AllDisplays;
            copy.MonitorDevicePath = string.IsNullOrWhiteSpace(copy.MonitorDevicePath)
                ? null
                : copy.MonitorDevicePath.Trim();
            // A specific display with no display named covers nothing; fall back to all.
            if (copy.DisplayScope == MediaDisplayScope.SpecificDisplay && copy.MonitorDevicePath == null)
                copy.DisplayScope = MediaDisplayScope.AllDisplays;
            return copy;
        }

        public bool SemanticallyEquals(MediaProfile? other)
        {
            if (other == null) return false;
            MediaProfile a = Sanitized();
            MediaProfile b = other.Sanitized();
            return a.OverrideGamma == b.OverrideGamma &&
                   a.GammaMode == b.GammaMode &&
                   a.OverrideBrightness == b.OverrideBrightness &&
                   a.Brightness.Equals(b.Brightness) &&
                   a.ShadowDetailStrength.Equals(b.ShadowDetailStrength) &&
                   a.DisplayScope == b.DisplayScope &&
                   string.Equals(a.MonitorDevicePath, b.MonitorDevicePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when this profile covers <paramref name="monitorDevicePath"/>.</summary>
        public bool AppliesTo(string? monitorDevicePath)
        {
            MediaProfile profile = Sanitized();
            return profile.DisplayScope == MediaDisplayScope.AllDisplays ||
                   string.Equals(profile.MonitorDevicePath, monitorDevicePath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The gamma mode and adjustments a covered monitor renders with: its own settings,
        /// with each overridden value replaced.
        /// </summary>
        public (GammaMode Mode, DisplayAdjustments Adjustments) ComposeWith(
            GammaMode monitorMode, DisplayAdjustments monitorAdjustments)
        {
            ArgumentNullException.ThrowIfNull(monitorAdjustments);
            MediaProfile profile = Sanitized();
            DisplayAdjustments adjustments = monitorAdjustments.Clone();
            adjustments.ShadowDetailStrength = profile.ShadowDetailStrength;
            if (profile.OverrideBrightness)
                adjustments.Brightness = profile.Brightness;
            return (profile.OverrideGamma ? profile.GammaMode : monitorMode, adjustments);
        }

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}
