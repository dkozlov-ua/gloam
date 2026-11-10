using System;
using System.IO;
using System.Linq;
using Gloam.Core;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Tests for the settings-loading hardening: unknown enum tolerance, never
    /// reset-and-save on parse failure, and SchemaVersion forward-compatibility
    /// (an old binary must never clobber a newer settings file).
    /// </summary>
    public sealed class SettingsResilienceTests : IDisposable
    {
        private readonly string _originalDataDir;
        private readonly string _originalRoamingDir;
        private readonly string _tempDir;

        public SettingsResilienceTests()
        {
            _originalDataDir = AppPaths.DataDir;
            _originalRoamingDir = AppPaths.RoamingDataDir;
            _tempDir = Path.Combine(Path.GetTempPath(), "GloamSettingsResilienceTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            AppPaths.UseDataDirectoriesForCurrentProcess(_tempDir, Path.Combine(_tempDir, "roaming"));
        }

        private string SettingsPath => Path.Combine(AppPaths.DataDir, "settings.json");

        [Fact]
        public void Load_UnknownEnumValue_DoesNotThrowAndKeepsOtherProperties()
        {
            // An enum value from another build must not fail the whole load (the legacy v1.0.0
            // build threw here and then destructively reset settings).
            File.WriteAllText(SettingsPath, """
                {
                  "SchemaVersion": 2,
                  "MonitorProfiles": {
                    "\\\\?\\DISPLAY#TEST#1": { "GammaMode": "SomeFutureCurve", "Brightness": 80 }
                  },
                  "ProfileSelection": "Media",
                  "StartupDefaultApplied": true
                }
                """);

            var sm = new SettingsManager();

            Assert.True(sm.LoadedExistingSettingsFile);
            Assert.False(sm.LoadFailedPreservingFile);

            var profile = sm.GetMonitorProfile(@"\\?\DISPLAY#TEST#1");
            Assert.NotNull(profile);
            // Unknown enum string maps to the enum default instead of throwing.
            Assert.Equal(default(GammaMode), profile.GammaMode);
            // Everything else in the file survives.
            Assert.Equal(80.0, profile.Brightness);
            Assert.Equal(DisplayProfileSelection.Media, sm.ProfileSelection);
            Assert.True(sm.StartupDefaultApplied);
        }

        [Fact]
        public void Load_ParseFailure_DoesNotRewriteFileOnDisk()
        {
            const string corrupt = "{ this is not valid json !!!";
            File.WriteAllText(SettingsPath, corrupt);

            var sm = new SettingsManager();

            Assert.True(sm.LoadFailedPreservingFile);
            // The unreadable file must be left untouched on disk...
            Assert.Equal(corrupt, File.ReadAllText(SettingsPath));
            // ...with the existing backup behavior preserved.
            Assert.NotEmpty(Directory.GetFiles(AppPaths.DataDir, "settings.json.bak-*"));

            // A routine Save() with no in-memory change must not overwrite it either.
            sm.Save();
            Assert.Equal(corrupt, File.ReadAllText(SettingsPath));
        }

        [Fact]
        public void Load_OversizedSettingsFile_IsPreservedWithoutParsing()
        {
            using (var stream = new FileStream(SettingsPath, FileMode.Create, FileAccess.Write, FileShare.None))
                stream.SetLength(SettingsManager.MaxSettingsFileBytes + 1);

            var sm = new SettingsManager();

            Assert.True(sm.LoadFailedPreservingFile);
            Assert.Equal(SettingsManager.MaxSettingsFileBytes + 1, new FileInfo(SettingsPath).Length);
            Assert.False(sm.Save());
            Assert.Equal(SettingsManager.MaxSettingsFileBytes + 1, new FileInfo(SettingsPath).Length);
        }

        [Fact]
        public void Load_ParseFailure_SaveResumesAfterRealChange()
        {
            File.WriteAllText(SettingsPath, "{ this is not valid json !!!");

            var sm = new SettingsManager();
            Assert.True(sm.LoadFailedPreservingFile);

            // The user actually changing a setting is consent to persist again.
            Assert.True(sm.TrySetProfileSelection(DisplayProfileSelection.Media));

            var reloaded = new SettingsManager();
            Assert.False(reloaded.LoadFailedPreservingFile);
            Assert.Equal(DisplayProfileSelection.Media, reloaded.ProfileSelection);
        }

        [Fact]
        public void Load_NewerSchemaVersion_LoadsBestEffortButRefusesSave()
        {
            string newer = """
                {
                  "SchemaVersion": 99,
                  "ProfileSelection": "Media"
                }
                """;
            File.WriteAllText(SettingsPath, newer);

            var sm = new SettingsManager();

            // Best-effort load still surfaces what this binary understands...
            Assert.True(sm.SettingsFileFromNewerVersion);
            Assert.Equal(DisplayProfileSelection.Media, sm.ProfileSelection);

            // ...but no save path may clobber the newer file, not even user changes.
            sm.Save();
            sm.SetProfileForMonitor("display-1", GammaMode.Gamma24);
            sm.MarkStartupDefaultApplied();

            Assert.Equal(newer, File.ReadAllText(SettingsPath));
        }

        [Fact]
        public void ProfileChanges_RollBackWhenDurableSaveIsRefused()
        {
            string newer = """
                {
                  "SchemaVersion": 99,
                  "MediaProfile": { "OverrideBrightness": true, "Brightness": 90 }
                }
                """;
            File.WriteAllText(SettingsPath, newer);
            var sm = new SettingsManager();

            MediaProfile media = sm.MediaProfile;
            Assert.False(sm.TrySetMediaProfile(new MediaProfile { OverrideGamma = true }));
            Assert.False(sm.TrySetProfileSelection(DisplayProfileSelection.Media));

            Assert.True(media.SemanticallyEquals(sm.MediaProfile));
            Assert.Equal(90, sm.MediaProfile.Brightness);
            Assert.Equal(DisplayProfileSelection.Normal, sm.ProfileSelection);
            Assert.Equal(newer, File.ReadAllText(SettingsPath));
        }

        [Fact]
        public void Save_StampsCurrentSchemaVersion()
        {
            var sm = new SettingsManager();
            sm.MarkStartupDefaultApplied();

            string json = File.ReadAllText(SettingsPath);
            Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", json);

            var reloaded = new SettingsManager();
            Assert.False(reloaded.SettingsFileFromNewerVersion);
            Assert.True(reloaded.StartupDefaultApplied);
        }

        [Fact]
        public void Load_MissingSchemaVersion_TreatedAsV1AndWritable()
        {
            // Pre-versioning (v1) files have no SchemaVersion property at all.
            File.WriteAllText(SettingsPath, """
                {
                  "StartupDefaultApplied": true
                }
                """);

            var sm = new SettingsManager();

            Assert.False(sm.SettingsFileFromNewerVersion);
            Assert.True(sm.StartupDefaultApplied);

            // Saving upgrades the file to the current schema stamp.
            sm.MarkLegacyInstallWarningShown();
            Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", File.ReadAllText(SettingsPath));
        }

        [Fact]
        public void Load_RetiredSettings_AreIgnoredAndTheRestKept()
        {
            // What calibration, night mode, colour balance, Game Lab and the old UI saved. None
            // of it may fail the load or survive the next save.
            File.WriteAllText(SettingsPath, """
                { "SchemaVersion": 5, "DarkTheme": true, "TrustCheckReminderDays": 14,
                  "ExcludedApps": ["game.exe"],
                  "NightMode": { "Enabled": true, "Algorithm": "Perceptual" },
                  "WindowBounds": { "Dashboard": { "Left": 1, "Top": 2, "Width": 900, "Height": 700 } },
                  "UiSectionExpanded": { "Dashboard.GameLab": true },
                  "MonitorProfiles": { "display-1": { "GammaMode": "Gamma24", "Brightness": 75,
                      "Temperature": -10, "RedGain": 1.1, "Mhc2ProfileName": "x.icm", "CalibrationProfileId": null } },
                  "StartupDefaultApplied": true }
                """);

            var sm = new SettingsManager();

            Assert.False(sm.LoadFailedPreservingFile);
            Assert.True(sm.StartupDefaultApplied);
            Assert.Equal(GammaMode.Gamma24, sm.GetProfileForMonitor("display-1"));
            Assert.Equal(75, sm.GetMonitorProfile("display-1")!.Brightness);

            sm.MarkLegacyInstallWarningShown();
            string saved = File.ReadAllText(SettingsPath);
            foreach (string retired in new[] { "DarkTheme", "TrustCheckReminderDays", "ExcludedApps", "NightMode",
                         "WindowBounds", "UiSectionExpanded", "Temperature", "RedGain", "Mhc2ProfileName" })
                Assert.DoesNotContain(retired, saved);
        }

        [Theory]
        [InlineData("MonitorProfiles", "[\"display-1\"]")]
        [InlineData("ProfileSelection", "{\"future\": true}")]
        public void Load_NewerSchemaWithIncompatibleProperty_NeverOverwritesFile(string name, string value)
        {
            string json = "{\"sChEmAvErSiOn\": 99,\"" + name + "\":" + value + "}";
            File.WriteAllText(SettingsPath, json);

            var sm = new SettingsManager();

            Assert.True(sm.SettingsFileFromNewerVersion);
            Assert.False(sm.Save());
            sm.MarkStartupDefaultApplied();
            Assert.Equal(json, File.ReadAllText(SettingsPath));
        }

        [Theory]
        [InlineData("null")]
        [InlineData("{\"StartupDefaultApplied\": \"invalid\"}")]
        public void Load_InvalidSettingsShape_PreservesFile(string json)
        {
            File.WriteAllText(SettingsPath, json);

            var sm = new SettingsManager();

            Assert.True(sm.LoadFailedPreservingFile);
            Assert.False(sm.Save());
            Assert.Equal(json, File.ReadAllText(SettingsPath));
        }

        [Fact]
        public void Load_NullMonitorProfile_DropsInvalidEntryAndAllowsReplacement()
        {
            File.WriteAllText(SettingsPath, """
                { "MonitorProfiles": { "broken": null, "valid": { "GammaMode": "Gamma24" } } }
                """);

            var sm = new SettingsManager();

            Assert.False(sm.LoadFailedPreservingFile);
            Assert.Null(sm.GetProfileForMonitor("broken"));
            Assert.Equal(GammaMode.Gamma24, sm.GetProfileForMonitor("valid"));
            sm.SetProfileForMonitor("broken", GammaMode.Gamma22);
            Assert.Equal(GammaMode.Gamma22, new SettingsManager().GetProfileForMonitor("broken"));
        }

        public void Dispose()
        {
            AppPaths.UseDataDirectoriesForCurrentProcess(_originalDataDir, _originalRoamingDir);
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }
}
