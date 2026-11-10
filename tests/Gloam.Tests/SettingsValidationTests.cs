using System;
using System.IO;
using System.Threading.Tasks;
using Gloam.Core;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Tests for settings validation and security-related input handling.
    /// </summary>
    public class SettingsValidationTests
    {
        #region MonitorProfileData

        [Fact]
        public void MonitorProfileData_DefaultValues_AreValid()
        {
            var profile = new MonitorProfileData();

            Assert.Equal(GammaMode.Gamma22, profile.GammaMode);
            Assert.Equal(100.0, profile.Brightness);
            Assert.False(profile.UseLinearBrightness);
        }

        [Fact]
        public void MonitorProfileData_Clone_CreatesIndependentCopy()
        {
            var original = new MonitorProfileData { Brightness = 75.0, UseLinearBrightness = true, GammaMode = GammaMode.Gamma24 };

            var clone = original.Clone();
            clone.Brightness = 50.0;

            Assert.Equal(75.0, original.Brightness);
            Assert.True(clone.UseLinearBrightness);
            Assert.Equal(GammaMode.Gamma24, clone.GammaMode);
        }

        [Fact]
        public void MonitorProfileData_ToAdjustments_PreservesValues()
        {
            var profile = new MonitorProfileData { Brightness = 80.0, UseLinearBrightness = true };

            var adjustments = profile.ToAdjustments();

            Assert.Equal(80.0, adjustments.Brightness);
            Assert.True(adjustments.UseLinearBrightness);
            Assert.Equal(0.0, adjustments.ShadowDetailStrength);
        }

        [Theory]
        [InlineData(75.0, 75.0)]
        [InlineData(2.0, 10.0)]
        [InlineData(250.0, 100.0)]
        [InlineData(double.NaN, 100.0)]
        public void SetMonitorProfile_ClampsBrightness(double saved, double expected)
        {
            WithTempData(() =>
            {
                var settings = new SettingsManager();
                settings.SetMonitorProfile("display-1", new MonitorProfileData { Brightness = saved });

                Assert.Equal(expected, settings.GetMonitorProfile("display-1")!.Brightness);
                Assert.Equal(expected, new SettingsManager().GetMonitorProfile("display-1")!.Brightness);
            });
        }

        [Fact]
        public void GetMonitorProfile_ReturnsACopy()
        {
            WithTempData(() =>
            {
                var settings = new SettingsManager();
                settings.SetMonitorProfile("display-1", new MonitorProfileData { Brightness = 60 });

                settings.GetMonitorProfile("display-1")!.Brightness = 20;

                Assert.Equal(60, settings.GetMonitorProfile("display-1")!.Brightness);
            });
        }

        [Fact]
        public void MonitorProfileChanged_IsRaisedOnlyForRealChanges()
        {
            WithTempData(() =>
            {
                var settings = new SettingsManager();
                var raised = new System.Collections.Generic.List<string>();
                settings.MonitorProfileChanged += raised.Add;

                settings.SetProfileForMonitor("display-1", GammaMode.Gamma24);
                settings.SetProfileForMonitor("display-1", GammaMode.Gamma24);
                settings.SetMonitorProfile("display-2", new MonitorProfileData { Brightness = 50 });
                settings.SetProfileForMonitor(string.Empty, GammaMode.Gamma22);

                Assert.Equal(new[] { "display-1", "display-2" }, raised);
            });
        }

        #endregion

        #region DisplayAdjustments

        [Fact]
        public void DisplayAdjustments_Default_HasNoAdjustments()
        {
            Assert.False(DisplayAdjustments.Default.HasAdjustments);
        }

        [Fact]
        public void DisplayAdjustments_WithBrightnessOrShadowDetail_HasAdjustments()
        {
            Assert.True(new DisplayAdjustments { Brightness = 80.0 }.HasAdjustments);
            Assert.True(new DisplayAdjustments { ShadowDetailStrength = 0.2 }.HasAdjustments);
            Assert.True(new DisplayAdjustments { Brightness = double.NaN }.HasAdjustments);
            // How dimming would behave is no adjustment by itself.
            Assert.False(new DisplayAdjustments { UseLinearBrightness = true }.HasAdjustments);
        }

        [Fact]
        public void DisplayAdjustments_Sanitized_ClampsEveryField()
        {
            var sanitized = new DisplayAdjustments
            {
                Brightness = 140,
                ShadowDetailStrength = -1,
                ShadowDetailPivot = double.NaN,
            }.Sanitized();

            Assert.Equal(100, sanitized.Brightness);
            Assert.Equal(0, sanitized.ShadowDetailStrength);
            Assert.Equal(ToneAdjustments.DefaultShadowPivot, sanitized.ShadowDetailPivot);
        }

        #endregion

        #region SettingsManager

        /// <summary>
        /// SettingsManager is read and written from the UI thread, the pipe server and the
        /// apply service's threads. This is a smoke test that concurrent Set/Get access doesn't
        /// throw (InvalidOperationException from enumerating a mutating dictionary) or corrupt
        /// the in-memory state.
        /// </summary>
        [Fact]
        public async Task SettingsManager_ConcurrentAccess_DoesNotThrow()
        {
            var sm = new SettingsManager();
            string device = @"\\?\DISPLAY#TEST#...";

            await Task.WhenAll(
                Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++)
                        sm.SetProfileForMonitor(device, (i & 1) == 0 ? GammaMode.Gamma24 : GammaMode.Gamma22);
                }),
                Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++)
                        sm.GetProfileForMonitor(device);
                }),
                Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++)
                        sm.GetMonitorProfile(device);
                }),
                Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++)
                        sm.TrySetMediaProfile(new MediaProfile { Brightness = 10 + i % 90 });
                }));

            // After the storm: the last written mode should be consistent on read.
            sm.SetProfileForMonitor(device, GammaMode.Gamma24);
            Assert.Equal(GammaMode.Gamma24, sm.GetProfileForMonitor(device));
        }

        [Fact]
        public void SettingsManager_StartupDefaultFlag_Persists()
        {
            WithTempData(() =>
            {
                var first = new SettingsManager();
                Assert.False(first.LoadedExistingSettingsFile);
                Assert.False(first.StartupDefaultApplied);

                first.MarkStartupDefaultApplied();

                var second = new SettingsManager();
                Assert.True(second.LoadedExistingSettingsFile);
                Assert.True(second.StartupDefaultApplied);
            });
        }

        [Fact]
        public void SettingsManager_MediaProfile_IsSanitizedAndACopy()
        {
            WithTempData(() =>
            {
                var settings = new SettingsManager();
                Assert.True(settings.TrySetMediaProfile(new MediaProfile { OverrideBrightness = true, Brightness = 500 }));

                MediaProfile media = settings.MediaProfile;
                Assert.Equal(100, media.Brightness);
                media.Brightness = 20;
                Assert.Equal(100, settings.MediaProfile.Brightness);
                Assert.Equal(100, new SettingsManager().MediaProfile.Brightness);
            });
        }

        #endregion

        private static void WithTempData(Action body)
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string dir = Path.Combine(Path.GetTempPath(), "GloamSettingsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(dir, Path.Combine(dir, "roaming"));
                body();
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
