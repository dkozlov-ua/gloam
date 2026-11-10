using Gloam.Core;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>The Media profile's model: what it overrides, and on which displays.</summary>
    public class MediaProfileTests
    {
        [Fact]
        public void Default_OverridesNothingOnEveryDisplay()
        {
            var media = new MediaProfile();
            var monitor = new DisplayAdjustments { Brightness = 75, UseLinearBrightness = true };

            var (mode, adjustments) = media.ComposeWith(GammaMode.Gamma24, monitor);

            Assert.True(media.AppliesTo("ANY"));
            Assert.Equal(GammaMode.Gamma24, mode);
            Assert.Equal(75, adjustments.Brightness);
            Assert.True(adjustments.UseLinearBrightness);
            Assert.Equal(0, adjustments.ShadowDetailStrength);
        }

        [Fact]
        public void Overrides_ReplaceOnlyWhatIsChecked()
        {
            var media = new MediaProfile
            {
                OverrideGamma = true, GammaMode = GammaMode.Gamma22,
                OverrideBrightness = true, Brightness = 100,
                ShadowDetailStrength = 0.4,
            };
            var monitor = new DisplayAdjustments { Brightness = 75, UseLinearBrightness = true };

            var (mode, adjustments) = media.ComposeWith(GammaMode.Gamma24, monitor);

            Assert.Equal(GammaMode.Gamma22, mode);
            Assert.Equal(100, adjustments.Brightness);
            Assert.Equal(0.4, adjustments.ShadowDetailStrength);
            // How dimming treats shadows stays the monitor's.
            Assert.True(adjustments.UseLinearBrightness);
            // The monitor's own adjustments are not changed.
            Assert.Equal(75, monitor.Brightness);
        }

        [Fact]
        public void UncheckedValues_AreKeptButIgnored()
        {
            var media = new MediaProfile { GammaMode = GammaMode.Gamma22, Brightness = 40 };

            var (mode, adjustments) = media.ComposeWith(GammaMode.Gamma24, new DisplayAdjustments { Brightness = 80 });

            Assert.Equal(GammaMode.Gamma24, mode);
            Assert.Equal(80, adjustments.Brightness);
            Assert.Equal(40, media.Sanitized().Brightness);
        }

        [Fact]
        public void SpecificDisplay_CoversOnlyThatDisplay_IgnoringCase()
        {
            var media = new MediaProfile { DisplayScope = MediaDisplayScope.SpecificDisplay, MonitorDevicePath = @"SIM\ONE" };

            Assert.True(media.AppliesTo(@"sim\one"));
            Assert.False(media.AppliesTo(@"SIM\TWO"));
            Assert.False(media.AppliesTo(null));
        }

        [Fact]
        public void SpecificDisplayWithoutADisplay_FallsBackToAll()
        {
            var media = new MediaProfile { DisplayScope = MediaDisplayScope.SpecificDisplay, MonitorDevicePath = "  " };

            Assert.Equal(MediaDisplayScope.AllDisplays, media.Sanitized().DisplayScope);
            Assert.True(media.AppliesTo(@"SIM\TWO"));
        }

        [Fact]
        public void Sanitized_ClampsCorruptValues()
        {
            var media = new MediaProfile
            {
                GammaMode = (GammaMode)99,
                Brightness = 2,
                ShadowDetailStrength = double.NaN,
                DisplayScope = (MediaDisplayScope)7,
            }.Sanitized();

            Assert.Equal(GammaMode.Gamma22, media.GammaMode);
            Assert.Equal(10, media.Brightness);
            Assert.Equal(0, media.ShadowDetailStrength);
            Assert.Equal(MediaDisplayScope.AllDisplays, media.DisplayScope);
            Assert.Equal(100, new MediaProfile { Brightness = double.PositiveInfinity }.Sanitized().Brightness);
        }

        [Fact]
        public void CloneAndSemanticEquality_CoverEveryField()
        {
            var media = new MediaProfile
            {
                OverrideGamma = true, GammaMode = GammaMode.Gamma24,
                OverrideBrightness = true, Brightness = 65,
                ShadowDetailStrength = 0.3,
                DisplayScope = MediaDisplayScope.SpecificDisplay, MonitorDevicePath = "A",
            };
            MediaProfile copy = media.Clone();
            Assert.True(copy.SemanticallyEquals(media));
            Assert.True(copy.SemanticallyEquals(new MediaProfile
            {
                OverrideGamma = true, GammaMode = GammaMode.Gamma24,
                OverrideBrightness = true, Brightness = 65,
                ShadowDetailStrength = 0.3,
                DisplayScope = MediaDisplayScope.SpecificDisplay, MonitorDevicePath = "a",
            }));

            void Differs(System.Action<MediaProfile> edit)
            {
                MediaProfile other = media.Clone();
                edit(other);
                Assert.False(other.SemanticallyEquals(media));
            }
            Differs(p => p.OverrideGamma = false);
            Differs(p => p.GammaMode = GammaMode.Gamma22);
            Differs(p => p.OverrideBrightness = false);
            Differs(p => p.Brightness = 70);
            Differs(p => p.ShadowDetailStrength = 0.5);
            Differs(p => p.MonitorDevicePath = "B");
            Assert.False(media.SemanticallyEquals(null));
        }
    }
}
