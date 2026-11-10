using System.Linq;
using Gloam.Core;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// From settings to the ramp on each display: <see cref="GammaApplyService"/> resolving what
    /// a monitor should show and <see cref="GammaRampWriter"/> writing and guarding it.
    /// </summary>
    public sealed class GammaPipelineTests
    {
        private static readonly MonitorInfo Hdr = FakeMonitorSource.Monitor(1, hdr: true, white: 240);
        private static readonly MonitorInfo Sdr = FakeMonitorSource.Monitor(2, hdr: false, white: 80);

        [Fact]
        public void Writer_SkipsARampTheDisplayAlreadyHas()
        {
            var ramp = new FakeGammaRamp();
            var writer = new GammaRampWriter(ramp);

            writer.ApplyGamma(Hdr, GammaMode.Gamma22, 240);
            writer.ApplyGamma(Hdr, GammaMode.Gamma22, 240);
            Assert.Equal(1, ramp.Writes);

            writer.ApplyGamma(Hdr, GammaMode.Gamma24, 240);
            Assert.Equal(2, ramp.Writes);

            // After a display change Windows may have reset it: the next apply writes again.
            writer.InvalidateAppliedState();
            writer.ApplyGamma(Hdr, GammaMode.Gamma24, 240);
            Assert.Equal(3, ramp.Writes);
        }

        [Fact]
        public void Writer_RestoresARampSomethingElseOverwrote()
        {
            var ramp = new FakeGammaRamp();
            var writer = new GammaRampWriter(ramp);
            writer.ApplyGamma(Hdr, GammaMode.Gamma24, 240);
            ushort[] applied = ramp.Ramp(Hdr.DeviceName)!;

            Assert.Equal(0, writer.VerifyAndRestoreRamps());
            ramp.Overwrite(Hdr.DeviceName);
            Assert.Equal(1, writer.VerifyAndRestoreRamps());

            Assert.Equal(applied, ramp.Ramp(Hdr.DeviceName));
        }

        [Fact]
        public void Writer_ARejectedRampIsWrittenAgainNextTime()
        {
            var ramp = new FakeGammaRamp { Accepts = false };
            var writer = new GammaRampWriter(ramp);

            writer.ApplyGamma(Hdr, GammaMode.Gamma24, 240);
            Assert.Null(ramp.Ramp(Hdr.DeviceName));

            ramp.Accepts = true;
            writer.ApplyGamma(Hdr, GammaMode.Gamma24, 240);
            Assert.NotNull(ramp.Ramp(Hdr.DeviceName));
        }

        [Fact]
        public void Writer_ClearForgetsTheRampSoTheGuardLeavesItAlone()
        {
            var ramp = new FakeGammaRamp();
            var writer = new GammaRampWriter(ramp);
            writer.ApplyGamma(Hdr, GammaMode.Gamma24, 240);

            writer.ClearGamma(Hdr);
            ramp.Overwrite(Hdr.DeviceName);

            Assert.Equal(0, writer.VerifyAndRestoreRamps());
        }

        [Fact]
        public void ApplyAll_WritesEachMonitorsOwnSettings()
        {
            using var data = new TempDataScope();
            var settings = new SettingsManager();
            settings.SetMonitorProfile(Hdr.MonitorDevicePath, new MonitorProfileData { GammaMode = GammaMode.Gamma24, Brightness = 75 });
            settings.SetMonitorProfile(Sdr.MonitorDevicePath, new MonitorProfileData { GammaMode = GammaMode.Gamma22, Brightness = 50, UseLinearBrightness = true });
            var ramp = new FakeGammaRamp();
            using var apply = new GammaApplyService(new GammaRampWriter(ramp), settings);

            apply.ApplyAll(new[] { Hdr, Sdr });

            AssertRamp(ramp, Hdr, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 75 });
            AssertRamp(ramp, Sdr, GammaMode.Gamma22, new DisplayAdjustments { Brightness = 50, UseLinearBrightness = true });
        }

        [Fact]
        public void ApplyAll_UnderMedia_WritesTheOverridesOnlyWhereMediaApplies()
        {
            using var data = new TempDataScope();
            var settings = new SettingsManager();
            settings.SetMonitorProfile(Hdr.MonitorDevicePath, new MonitorProfileData { GammaMode = GammaMode.Gamma24, Brightness = 75 });
            settings.SetMonitorProfile(Sdr.MonitorDevicePath, new MonitorProfileData { GammaMode = GammaMode.Gamma24, Brightness = 75 });
            Assert.True(settings.TrySetMediaProfile(new MediaProfile
            {
                OverrideBrightness = true, Brightness = 100, ShadowDetailStrength = 0.4,
                DisplayScope = MediaDisplayScope.SpecificDisplay, MonitorDevicePath = Hdr.MonitorDevicePath,
            }));
            Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Media));
            var ramp = new FakeGammaRamp();
            using var apply = new GammaApplyService(new GammaRampWriter(ramp), settings);

            apply.ApplyAll(new[] { Hdr, Sdr });

            AssertRamp(ramp, Hdr, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 100, ShadowDetailStrength = 0.4 });
            AssertRamp(ramp, Sdr, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 75 });

            // Back to Normal: the Media display returns to its own settings.
            Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Normal));
            apply.ApplyAll(new[] { Hdr, Sdr });
            AssertRamp(ramp, Hdr, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 75 });
        }

        [Fact]
        public void RequestApply_RapidChangesEndOnTheLatest()
        {
            using var data = new TempDataScope();
            var settings = new SettingsManager();
            var ramp = new FakeGammaRamp();
            using var apply = new GammaApplyService(new GammaRampWriter(ramp), settings);

            // A slider drag: many saved values in a row. The coalescer may skip some, never the last.
            foreach (int brightness in Enumerable.Range(40, 30))
            {
                settings.SetMonitorProfile(Hdr.MonitorDevicePath, new MonitorProfileData { GammaMode = GammaMode.Gamma22, Brightness = brightness });
                apply.RequestApply(Hdr);
            }

            AssertRamp(ramp, Hdr, GammaMode.Gamma22, new DisplayAdjustments { Brightness = 69 });
            Assert.True(ramp.Writes < 30, $"{ramp.Writes} writes for 30 changes: nothing was coalesced.");
        }

        private static void AssertRamp(FakeGammaRamp ramp, MonitorInfo monitor, GammaMode mode, DisplayAdjustments adjustments)
        {
            ushort[] expected = FakeGammaRamp.Expected(mode, monitor.SdrWhiteLevel, adjustments, monitor.IsHdrActive);
            Eventually.True(() => ramp.Ramp(monitor.DeviceName) is { } actual && actual.SequenceEqual(expected),
                $"{monitor.DeviceName} to show {mode} at {adjustments.Brightness}% (shadow {adjustments.ShadowDetailStrength})");
        }
    }
}
