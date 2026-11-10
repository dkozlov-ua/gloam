using System;

namespace Gloam.Core
{
    /// <summary>
    /// The tone adjustments layered on a monitor's gamma curve: brightness (dimming) and the
    /// Media profile's dark-area visibility toe. Built per apply from the saved monitor
    /// profile plus the active display profile; never persisted itself.
    /// </summary>
    public sealed class DisplayAdjustments
    {
        /// <summary>Brightness level (10-100%).</summary>
        public double Brightness { get; set; } = 100.0;

        /// <summary>
        /// True dims linearly. False (the default) dims with power-law compression, which keeps
        /// near-black separation that a straight multiply would crush.
        /// </summary>
        public bool UseLinearBrightness { get; set; }

        /// <summary>
        /// Dark-area visibility toe strength, 0..1. The curve is black-anchored, monotonic and
        /// rejoins the unmodified response at <see cref="ShadowDetailPivot"/>.
        /// </summary>
        public double ShadowDetailStrength { get; set; }

        /// <summary>Linear-light point where the visibility toe becomes identity.</summary>
        public double ShadowDetailPivot { get; set; } = ToneAdjustments.DefaultShadowPivot;

        /// <summary>True when any value differs from neutral.</summary>
        public bool HasAdjustments =>
            !double.IsFinite(Brightness) || Math.Abs(Brightness - 100.0) > 0.01 ||
            !double.IsFinite(ShadowDetailStrength) || Math.Abs(ShadowDetailStrength) > 0.0001;

        public static DisplayAdjustments Default => new();

        public DisplayAdjustments Clone() => new()
        {
            Brightness = Brightness,
            UseLinearBrightness = UseLinearBrightness,
            ShadowDetailStrength = ShadowDetailStrength,
            ShadowDetailPivot = ShadowDetailPivot,
        };

        /// <summary>Returns a copy clamped to the ranges the LUT pipeline can safely evaluate.</summary>
        public DisplayAdjustments Sanitized() => new()
        {
            Brightness = ClampFinite(Brightness, 0.0, 100.0, 100.0),
            UseLinearBrightness = UseLinearBrightness,
            ShadowDetailStrength = ClampFinite(ShadowDetailStrength, 0.0, 1.0, 0.0),
            ShadowDetailPivot = ClampFinite(ShadowDetailPivot, 0.02, 0.25, ToneAdjustments.DefaultShadowPivot),
        };

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}
