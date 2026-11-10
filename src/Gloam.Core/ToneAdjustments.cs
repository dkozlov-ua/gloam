using System;

namespace Gloam.Core
{
    /// <summary>
    /// Tone math applied in linear light on top of the gamma curve. Every adjustment here is
    /// neutral (it scales R, G and B alike), so it acts on one grey value.
    /// </summary>
    public static class ToneAdjustments
    {
        /// <summary>Linear-light point where the dark-area visibility toe rejoins the curve.</summary>
        public const double DefaultShadowPivot = 0.10;

        // The toe's largest lift. With the shoulder below, the curve stays monotonic for every
        // allowed strength (minimum slope 1 - 1.5/3 = 0.5 at full strength).
        private const double MaximumToeAmount = 1.5;

        /// <summary>
        /// Applies dimming to value. Can use either linear scaling or perceptual (gamma-compressed) scaling.
        /// </summary>
        /// <param name="value">Input value (0-1)</param>
        /// <param name="brightnessPercent">Brightness percentage (0-100)</param>
        /// <param name="linear">If true, uses linear multiplication. If false, uses power-law compression to preserve shadows.</param>
        /// <returns>Dimmed value</returns>
        public static double ApplyDimming(double value, double brightnessPercent, bool linear = false)
        {
            value = ClampFinite(value, 0.0, 1.0, 0.0);
            brightnessPercent = ClampFinite(brightnessPercent, 0.0, 100.0, 100.0);

            if (brightnessPercent >= 100.0) return value;
            if (brightnessPercent <= 0) return 0;

            double brightness = brightnessPercent / 100.0;

            if (linear)
            {
                return value * brightness;
            }

            // Power-law compression: lifts shadow detail relative to a straight linear dim
            // while still landing white at exactly `brightness`. At value=1 the output equals
            // brightness; at value<1 the shadow lift preserves near-black separation.
            // Formula: output = input^(1/gamma_boost) * brightness
            // where gamma_boost grows as brightness falls (1.0 at 100%, 1.27 at 10%).
            double gammaBoost = 1.0 + (1.0 - brightness) * 0.3;

            return Math.Pow(value, 1.0 / gammaBoost) * brightness;
        }

        /// <summary>
        /// Applies the same dimming curve to an absolute-nit value. Used by the HDR headroom
        /// blend so highlights follow the brightness slider instead of snapping back to passthrough.
        /// </summary>
        /// <remarks>
        /// Anchored at the SDR white level so the headroom curve meets the SDR portion of
        /// the LUT exactly: at nits == sdrWhiteLevel this returns
        /// ApplyDimming(1.0) * sdrWhiteLevel. Normalizing against the 10,000-nit PQ ceiling
        /// instead made the headroom target start ~1.7× brighter than the dimmed SDR white at
        /// 50% brightness — a visible shelf at the boundary. Above white the same power curve
        /// keeps the rolloff monotonic.
        /// </remarks>
        public static double ApplyDimmingNits(double nits, double brightnessPercent, double sdrWhiteLevel, bool linear = false)
        {
            nits = double.IsFinite(nits) ? Math.Max(nits, 0.0) : 0.0;
            brightnessPercent = ClampFinite(brightnessPercent, 0.0, 100.0, 100.0);
            sdrWhiteLevel = double.IsFinite(sdrWhiteLevel) ? Math.Max(sdrWhiteLevel, 1.0) : 1.0;

            if (brightnessPercent >= 100.0) return nits;
            if (brightnessPercent <= 0) return 0;

            double brightness = brightnessPercent / 100.0;

            if (linear)
            {
                return nits * brightness;
            }

            double ratio = nits / sdrWhiteLevel;
            double gammaBoost = 1.0 + (1.0 - brightness) * 0.3;
            return Math.Pow(ratio, 1.0 / gammaBoost) * brightness * sdrWhiteLevel;
        }

        /// <summary>
        /// Black-anchored dark-area visibility curve. The analytic toe is C1-continuous at the
        /// pivot, leaves the entire range above the pivot unchanged, and remains monotonic for
        /// every allowed setting. It is deliberately evaluated in linear light.
        /// </summary>
        public static double ApplyShadowVisibility(double linear, double strength, double pivot = DefaultShadowPivot)
        {
            linear = double.IsFinite(linear) ? Math.Clamp(linear, 0.0, 1.0) : 0.0;
            strength = double.IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : 0.0;
            pivot = double.IsFinite(pivot) ? Math.Clamp(pivot, 0.02, 0.25) : DefaultShadowPivot;
            if (strength <= 0.0 || linear <= 0.0 || linear >= pivot) return linear;

            double u = linear / pivot;
            double shoulder = 1.0 - u;
            double lifted = linear + MaximumToeAmount * strength * linear * shoulder * shoulder;
            return Math.Min(lifted, pivot);
        }

        /// <summary>Minimum analytic slope inside the toe; always positive at valid strength.</summary>
        public static double MinimumShadowSlope(double strength)
        {
            strength = double.IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : 0.0;
            return 1.0 - MaximumToeAmount * strength / 3.0;
        }

        /// <summary>
        /// The full adjustment chain on one linear value (0..1 of SDR white): the visibility toe
        /// first, so it lifts what the gamma curve produced, then dimming.
        /// </summary>
        public static double Apply(double linear, DisplayAdjustments adjustments)
        {
            adjustments = (adjustments ?? DisplayAdjustments.Default).Sanitized();
            double value = ClampFinite(linear, 0.0, 1.0, 0.0);

            if (adjustments.ShadowDetailStrength > 0.0)
                value = ApplyShadowVisibility(value, adjustments.ShadowDetailStrength, adjustments.ShadowDetailPivot);

            if (adjustments.Brightness < 100.0)
                value = ApplyDimming(value, adjustments.Brightness, adjustments.UseLinearBrightness);

            return Math.Clamp(value, 0.0, 1.0);
        }

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}
