using System;
using System.Collections.Concurrent;

namespace Gloam.Core
{
    /// <summary>
    /// Generates 1D lookup tables for HDR/SDR gamma correction with tone adjustments.
    /// </summary>
    /// <remarks>
    /// The LUT generator implements a multi-stage pipeline:
    /// 1. PQ EOTF decode (signal to linear nits)
    /// 2. Normalize to SDR range
    /// 3. Apply target gamma curve (2.2 or 2.4)
    /// 4. Apply tone adjustments (visibility toe, dimming) in linear space
    /// 5. PQ OETF encode (linear nits to signal)
    /// 6. Preserve HDR headroom, fading only the correction at SDR white
    ///
    /// Every adjustment is neutral, so the three channels are equal; they stay separate
    /// arrays because the hardware ramp takes one per channel.
    ///
    /// LUT results are cached to avoid redundant computation for identical parameters.
    /// </remarks>
    public static class LutGenerator
    {
        // Cache for computed LUTs to avoid redundant computation.
        // Key: (GammaMode, exact WhiteLevel, full adjustments, IsHdr)
        //
        // CONTRACT: Cached arrays are READ-ONLY. Callers must not mutate them — the cache
        // hands out shared references directly to avoid 32 KB of per-call allocation at
        // slider-drag frequencies. Any write to a returned array corrupts future lookups.
        private static readonly ConcurrentDictionary<(GammaMode, double, AdjustmentsKey, bool), (double[] R, double[] G, double[] B, double[] Grey)> _lutCache = new();

        // A hash code alone is not identity: collisions returned a wrong cached LUT. Keep
        // every input in the dictionary key so equality is checked field-by-field.
        private readonly record struct AdjustmentsKey(
            double Brightness, bool LinearBrightness, double ShadowDetailStrength, double ShadowDetailPivot)
        {
            public static AdjustmentsKey From(DisplayAdjustments value) => new(
                value.Brightness, value.UseLinearBrightness, value.ShadowDetailStrength, value.ShadowDetailPivot);
        }

        // Cache ceiling. When we cross it we clear the entire cache at once rather than try to
        // approximate LRU from ConcurrentDictionary.Keys (which has no defined order). A full
        // flush is simpler and the next few slider ticks repopulate hot entries.
        private const int MaxCacheSize = 100;

        /// <summary>
        /// Generates a 1024-point 1D LUT for gamma correction (single channel, no adjustments).
        /// </summary>
        public static double[] GenerateLut(GammaMode gammaMode, double sdrWhiteLevel, bool isHdr = true)
        {
            return GenerateLut(gammaMode, sdrWhiteLevel, DisplayAdjustments.Default, isHdr).Grey;
        }

        /// <summary>
        /// Clears the LUT cache to free memory.
        /// </summary>
        public static void ClearCache()
        {
            _lutCache.Clear();
        }

        /// <summary>
        /// Generates per-channel 1024-point 1D LUTs for gamma correction with adjustments.
        /// Results are cached for performance when called with identical parameters.
        /// </summary>
        /// <param name="gammaMode">The target gamma curve (2.2 or 2.4).</param>
        /// <param name="sdrWhiteLevel">The SDR white level in nits (e.g. 80, 200, 480).</param>
        /// <param name="adjustments">Brightness and dark-area visibility.</param>
        /// <param name="isHdr">Whether the target display is in HDR mode.</param>
        /// <returns>Per-channel LUTs (R, G, B) and a Grey reference.</returns>
        public static (double[] R, double[] G, double[] B, double[] Grey) GenerateLut(
            GammaMode gammaMode,
            double sdrWhiteLevel,
            DisplayAdjustments adjustments,
            bool isHdr = true)
        {
            ValidateSdrWhiteLevel(sdrWhiteLevel);
            adjustments = (adjustments ?? DisplayAdjustments.Default).Sanitized();

            // Display white is part of the actual transfer function. Coarse bucketing made
            // two monitors order-dependent: whichever generated a LUT first supplied its
            // curve to the other.
            var cacheKey = (gammaMode, sdrWhiteLevel, AdjustmentsKey.From(adjustments), isHdr);

            if (_lutCache.TryGetValue(cacheKey, out var cachedLut))
            {
                return cachedLut;
            }

            var result = GenerateLutInternal(gammaMode, sdrWhiteLevel, adjustments, isHdr);

            // Flush on overflow rather than pretending to LRU over an unordered dictionary.
            if (_lutCache.Count >= MaxCacheSize)
            {
                _lutCache.Clear();
            }

            _lutCache.TryAdd(cacheKey, result);
            return result;
        }

        /// <summary>
        /// Internal LUT generation without caching.
        /// </summary>
        private static (double[] R, double[] G, double[] B, double[] Grey) GenerateLutInternal(
            GammaMode gammaMode,
            double sdrWhiteLevel,
            DisplayAdjustments adjustments,
            bool isHdr)
        {
            double[] curve = new double[1024];

            if (gammaMode == GammaMode.WindowsDefault && !adjustments.HasAdjustments)
            {
                // Identity LUT
                for (int i = 0; i < 1024; i++)
                {
                    curve[i] = i / 1023.0;
                }
                return Channels(curve);
            }

            if (!isHdr)
            {
                // SDR generation: decode the incoming signal using the TARGET gamma, adjust in
                // linear light, then re-encode for the display's assumed native ~2.2 response.
                // Windows SDR content is mastered on a ~2.2 display, so the display decodes as
                // gamma 2.2 regardless of what the user selects here — the gammaMode therefore
                // controls the DECODE of the (re-)mapped signal, not the display.
                // Gamma22/WindowsDefault net to a passthrough (decode 2.2 / encode 1/2.2);
                // Gamma24 darkens shadows.
                double targetGamma = gammaMode switch
                {
                    GammaMode.Gamma24 => 2.4,
                    _ => 2.2 // Gamma22 and WindowsDefault
                };

                for (int i = 0; i < 1024; i++)
                {
                    double input = i / 1023.0;
                    double linear = Math.Pow(input, targetGamma);
                    double adjusted = ToneAdjustments.Apply(linear, adjustments);
                    curve[i] = Math.Pow(adjusted, 1.0 / 2.2);
                }
                return Channels(curve);
            }

            double gamma = gammaMode switch
            {
                GammaMode.Gamma24 => 2.4,
                GammaMode.Gamma22 => 2.2,
                _ => 1.0 // WindowsDefault with adjustments
            };

            double blackLevel = 0.0;

            // Above SDR white, preserve the headroom target's full signal range and fade
            // only the adjustment at white. Blending a clamped SDR value toward the target
            // instead flattens the lower highlights, even at 100% with gamma alone.
            double pqSdrWhite = TransferFunctions.PqInverseEotf(sdrWhiteLevel);
            double headroomWhiteSignal = ComputeHeadroomTarget(sdrWhiteLevel, adjustments, sdrWhiteLevel);

            for (int i = 0; i < 1024; i++)
            {
                double normalized = i / 1023.0;

                // 1. PQ EOTF -> linear nits
                double linear = TransferFunctions.PqEotf(normalized);

                double output;

                if (gammaMode == GammaMode.WindowsDefault)
                {
                    // No gamma correction, just the adjustments
                    output = linear;
                }
                else
                {
                    // 2. Input Light -> Simulated Signal (inverse sRGB)
                    double srgbNormalized = TransferFunctions.SrgbInverseEotf(linear, sdrWhiteLevel, blackLevel);

                    // 3. Apply gamma
                    double gammaApplied = Math.Pow(srgbNormalized, gamma);

                    // 4. Scale to output nits
                    output = blackLevel + (sdrWhiteLevel - blackLevel) * gammaApplied;
                }

                // 5. Tone adjustments, on the 0..1-of-white scale
                if (adjustments.HasAdjustments)
                {
                    output = ToneAdjustments.Apply(Clamp01(output / sdrWhiteLevel), adjustments) * sdrWhiteLevel;
                }

                // 6. Encode to PQ
                double pq = TransferFunctions.PqInverseEotf(output);

                if (linear <= sdrWhiteLevel)
                {
                    curve[i] = pq;
                }
                else
                {
                    // pq above white is the adjusted SDR-white anchor (the SDR transfer and
                    // adjustment functions are bounded). Preserve the FULL headroom signal,
                    // adding only the anchor's residual correction. Gamma alone has zero
                    // residual, giving identity at EVERY HDR level; brightness alone gives the
                    // requested dimming without a second shoulder.
                    double headroomSignal = ComputeHeadroomTarget(linear, adjustments, sdrWhiteLevel);

                    // Smoothstep fades the residual to zero at the PQ ceiling. It keeps
                    // the value continuous at SDR white; the SDR curve and HDR target can
                    // still have slightly different slopes at that boundary.
                    double t = Clamp01((normalized - pqSdrWhite) / Math.Max(1.0 - pqSdrWhite, 1e-9));
                    double blendFactor = t * t * (3.0 - 2.0 * t);

                    curve[i] = headroomSignal + (pq - headroomWhiteSignal) * (1.0 - blendFactor);
                }
            }

            return Channels(curve);
        }

        // The channels are equal, but each gets its own array: callers treat them as
        // independent read-only ramps, and the grey reference must not alias them.
        private static (double[] R, double[] G, double[] B, double[] Grey) Channels(double[] curve) =>
            (curve, (double[])curve.Clone(), (double[])curve.Clone(), (double[])curve.Clone());

        /// <summary>
        /// Target the headroom blend fades toward. We preserve HDR creative intent (no gamma
        /// on highlights) but keep brightness dimming active — otherwise dimming the screen
        /// leaves HDR highlights at full brightness, which the user experiences as specular
        /// bloom punching through a dimmed UI.
        /// </summary>
        private static double ComputeHeadroomTarget(
            double linearNits, DisplayAdjustments adjustments, double sdrWhiteLevel)
        {
            if (adjustments.Brightness >= 100.0)
            {
                // No dimming — target is pure passthrough. Re-encode original linear nits
                // to PQ. For i at the upper end this equals the input signal.
                return TransferFunctions.PqInverseEotf(linearNits);
            }

            double dimmed = ToneAdjustments.ApplyDimmingNits(
                linearNits, adjustments.Brightness, sdrWhiteLevel, adjustments.UseLinearBrightness);
            return TransferFunctions.PqInverseEotf(dimmed);
        }

        private static void ValidateSdrWhiteLevel(double sdrWhiteLevel)
        {
            if (!double.IsFinite(sdrWhiteLevel) || sdrWhiteLevel <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteLevel), sdrWhiteLevel,
                    "SDR white level must be a positive finite luminance in nits.");
        }

        private static double Clamp01(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
    }
}
