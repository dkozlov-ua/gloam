using System;
using Gloam.Core;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Dimming and the dark-area visibility toe: the tone math every LUT runs after its gamma
    /// curve. Carried over from the colour-adjustment and Game Lab suites when colour balance and
    /// Game Lab were removed; the curves themselves are unchanged.
    /// </summary>
    public class ToneAdjustmentsTests
    {
        #region Dimming

        [Fact]
        public void ApplyDimming_100Percent_ReturnsOriginal()
        {
            Assert.Equal(0.5, ToneAdjustments.ApplyDimming(0.5, 100.0), 4);
        }

        [Fact]
        public void ApplyDimming_0Percent_ReturnsZero()
        {
            Assert.Equal(0.0, ToneAdjustments.ApplyDimming(0.5, 0.0), 4);
        }

        [Fact]
        public void ApplyDimming_NonFiniteInputs_ReturnsFiniteBoundedValue()
        {
            Assert.Equal(0.0, ToneAdjustments.ApplyDimming(double.NaN, 50.0), 10);
            Assert.Equal(0.5, ToneAdjustments.ApplyDimming(0.5, double.NaN), 10);
            Assert.Equal(0.0, ToneAdjustments.ApplyDimmingNits(double.NaN, 50.0, 200.0), 10);
            Assert.Equal(100.0, ToneAdjustments.ApplyDimmingNits(100.0, double.NaN, 200.0), 10);
        }

        [Fact]
        public void ApplyDimming_Linear_HalvesBrightness()
        {
            Assert.Equal(0.5, ToneAdjustments.ApplyDimming(1.0, 50.0, linear: true), 4);
        }

        [Fact]
        public void ApplyDimming_Perceptual_PreservesShadows()
        {
            // Perceptual dimming should preserve shadows better than linear
            double perceptual = ToneAdjustments.ApplyDimming(0.1, 50.0, linear: false);
            double linear = ToneAdjustments.ApplyDimming(0.1, 50.0, linear: true);

            Assert.True(perceptual > linear);
        }

        [Fact]
        public void ApplyDimming_Perceptual_PinsTheCurve()
        {
            // output = input^(1/(1 + 0.3*(1-b))) * b: pinned so a refactor cannot drift it.
            Assert.Equal(Math.Pow(0.25, 1.0 / 1.15) * 0.5, ToneAdjustments.ApplyDimming(0.25, 50.0), 12);
            Assert.Equal(0.75, ToneAdjustments.ApplyDimming(1.0, 75.0), 12);
        }

        [Fact]
        public void ApplyDimmingNits_MatchesSdrDimmingAtWhiteLevel()
        {
            // The headroom curve must meet the SDR curve exactly at the SDR white level,
            // otherwise the LUT has a brightness shelf at the SDR/HDR boundary.
            const double sdrWhite = 200.0;
            foreach (double brightness in new[] { 90.0, 50.0, 20.0 })
            {
                double sdrSide = ToneAdjustments.ApplyDimming(1.0, brightness) * sdrWhite;
                double hdrSide = ToneAdjustments.ApplyDimmingNits(sdrWhite, brightness, sdrWhite);
                Assert.Equal(sdrSide, hdrSide, 6);
            }
        }

        [Fact]
        public void ApplyDimmingNits_MonotonicInNits()
        {
            const double sdrWhite = 200.0;
            double prev = 0;
            for (double nits = 0; nits <= 10000; nits += 50)
            {
                double dimmed = ToneAdjustments.ApplyDimmingNits(nits, 50.0, sdrWhite);
                Assert.True(dimmed >= prev, $"Dimming not monotonic at {nits} nits: {prev} -> {dimmed}");
                prev = dimmed;
            }
        }

        [Fact]
        public void ApplyDimmingNits_ReducesHighlights()
        {
            // A 1000-nit highlight at 50% brightness must come down meaningfully,
            // but not below the dimmed SDR white.
            const double sdrWhite = 200.0;
            double dimmed = ToneAdjustments.ApplyDimmingNits(1000.0, 50.0, sdrWhite);
            double dimmedWhite = ToneAdjustments.ApplyDimming(1.0, 50.0) * sdrWhite;

            Assert.True(dimmed < 1000.0, $"Highlight not dimmed: {dimmed}");
            Assert.True(dimmed > dimmedWhite, $"Highlight crushed below dimmed white: {dimmed} <= {dimmedWhite}");
        }

        #endregion

        #region Dark-area visibility

        [Fact]
        public void ShadowVisibility_AnchorsBlackAndPivot_AndProtectsMidtones()
        {
            const double pivot = 0.10;
            Assert.Equal(0.0, ToneAdjustments.ApplyShadowVisibility(0.0, 1.0, pivot), 15);
            Assert.Equal(pivot, ToneAdjustments.ApplyShadowVisibility(pivot, 1.0, pivot), 15);
            Assert.Equal(0.50, ToneAdjustments.ApplyShadowVisibility(0.50, 1.0, pivot), 15);
            Assert.True(ToneAdjustments.ApplyShadowVisibility(0.025, 1.0, pivot) > 0.025);
        }

        [Fact]
        public void ShadowVisibility_IsStrictlyMonotonicAtMaximumStrength()
        {
            double previous = ToneAdjustments.ApplyShadowVisibility(0, 1.0);
            for (int i = 1; i <= 10000; i++)
            {
                double input = i / 10000.0;
                double current = ToneAdjustments.ApplyShadowVisibility(input, 1.0);
                Assert.True(current > previous,
                    $"Visibility curve stopped increasing at {input:F5}: {previous:F8} -> {current:F8}");
                previous = current;
            }
            Assert.True(ToneAdjustments.MinimumShadowSlope(1.0) > 0);
        }

        [Fact]
        public void ShadowVisibility_ZeroStrengthAndNonFiniteInputs_AreIdentityOrBounded()
        {
            Assert.Equal(0.03, ToneAdjustments.ApplyShadowVisibility(0.03, 0.0), 15);
            Assert.Equal(0.0, ToneAdjustments.ApplyShadowVisibility(double.NaN, 1.0), 15);
            Assert.Equal(0.03, ToneAdjustments.ApplyShadowVisibility(0.03, double.NaN), 15);
        }

        [Fact]
        public void Apply_LiftsShadowsThenDims()
        {
            var neutral = ToneAdjustments.Apply(0.03, DisplayAdjustments.Default);
            var lifted = ToneAdjustments.Apply(0.03, new DisplayAdjustments { ShadowDetailStrength = 0.75 });
            var protectedMid = ToneAdjustments.Apply(0.30, new DisplayAdjustments { ShadowDetailStrength = 1.0 });

            Assert.Equal(0.03, neutral, 15);
            Assert.True(lifted > neutral);
            Assert.Equal(0.30, protectedMid, 15);

            // The toe runs first: dimming then scales the lifted value, not the original.
            var both = ToneAdjustments.Apply(0.03, new DisplayAdjustments { ShadowDetailStrength = 0.75, Brightness = 50 });
            Assert.Equal(ToneAdjustments.ApplyDimming(lifted, 50), both, 15);
        }

        [Fact]
        public void Apply_NullAndCorruptAdjustments_StayBounded()
        {
            Assert.Equal(0.5, ToneAdjustments.Apply(0.5, null!), 15);
            var corrupt = new DisplayAdjustments { Brightness = double.NaN, ShadowDetailStrength = 9, ShadowDetailPivot = -1 };
            double value = ToneAdjustments.Apply(0.05, corrupt);
            Assert.True(double.IsFinite(value));
            Assert.InRange(value, 0.0, 1.0);
        }

        #endregion
    }
}
