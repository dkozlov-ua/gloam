using Gloam.Core;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Exact LUT outputs, pinned when LutGenerator was cut down to gamma, brightness and
    /// dark-area visibility. Before the cut, the old and new generators were compared over
    /// 144 scenarios (gamma 2.2 / 2.4 / native, SDR white 80 / 200 / 480, brightness 100 / 75 /
    /// 50 / 10, linear and perceptual dimming, SDR and HDR paths): every entry was identical.
    /// These samples hold that output in place; a change that moves them changes what users see
    /// and must say so in the release notes.
    /// </summary>
    public class LutPinningTests
    {
        private static readonly int[] Indices = { 64, 128, 256, 384, 512, 768, 896, 1023 };

        [Fact]
        public void Hdr_Gamma22_FullBrightness()
        {
            AssertSamples(LutGenerator.GenerateLut(GammaMode.Gamma22, 200, DisplayAdjustments.Default, isHdr: true).R,
                0.011718610837946303, 0.07462088351668611, 0.23773406691322002, 0.3752969430129061,
                0.5022769930784896, 0.7507331378299029, 0.875855327468221, 1);
        }

        [Fact]
        public void Hdr_Gamma24_Dimmed()
        {
            AssertSamples(LutGenerator.GenerateLut(GammaMode.Gamma24, 240, new DisplayAdjustments { Brightness = 75 }, isHdr: true).R,
                0.00811882348274793, 0.05761583806827291, 0.21192414374612592, 0.34653564250271024,
                0.47263484117449595, 0.7086794876459503, 0.8251468465521786, 0.9420760674453282);
        }

        [Fact]
        public void Sdr_Gamma24_Dimmed()
        {
            AssertSamples(LutGenerator.GenerateLut(GammaMode.Gamma24, 80, new DisplayAdjustments { Brightness = 50 }, isHdr: false).R,
                0.05264080216911637, 0.1015978575632603, 0.19608600621782274, 0.288064468144371,
                0.37845012440853043, 0.5559704943238597, 0.6435148598418434, 0.7297400528407231);
        }

        [Fact]
        public void Hdr_Gamma22_DimmedWithDarkAreaVisibility()
        {
            var adjustments = new DisplayAdjustments { Brightness = 60, ShadowDetailStrength = 0.5 };
            AssertSamples(LutGenerator.GenerateLut(GammaMode.Gamma22, 200, adjustments, isHdr: true).R,
                0.02153702612211682, 0.10027084413997775, 0.2544991955096979, 0.35034879056506024,
                0.4599319740217596, 0.6766842033167009, 0.7882234432203025, 0.9010983836731382);
        }

        private static void AssertSamples(double[] lut, params double[] expected)
        {
            Assert.Equal(0.0, lut[0], 15);
            for (int i = 0; i < Indices.Length; i++)
                Assert.Equal(expected[i], lut[Indices[i]], 12);
        }
    }
}
