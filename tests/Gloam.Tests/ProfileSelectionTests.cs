using System.IO;
using Gloam.Core;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Three display profiles: Normal (each monitor's settings), Media (one set of overrides
    /// layered on them) and SDR (each monitor's settings with HDR off). The choice is sticky:
    /// it survives restarts until another is made.
    /// </summary>
    public sealed class ProfileSelectionTests
    {
        [Fact]
        public void Selection_DefaultsToNormalAndSurvivesRestart()
        {
            WithSettings(settings =>
            {
                Assert.Equal(DisplayProfileSelection.Normal, settings.ProfileSelection);
                Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Media));
                Assert.Equal(DisplayProfileSelection.Media, new SettingsManager().ProfileSelection);
            });
        }

        [Fact]
        public void Selection_RaisesOneEventPerChange()
        {
            WithSettings(settings =>
            {
                int raised = 0;
                settings.DisplayProfileChanged += () => raised++;

                Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Media));
                Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Media));
                Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Normal));
                Assert.False(settings.TrySetProfileSelection((DisplayProfileSelection)7));

                Assert.Equal(2, raised);
            });
        }

        [Fact]
        public void Selection_ThatCannotBeSaved_KeepsThePreviousOneAndRaisesNothing()
        {
            WithDataRoot(root =>
            {
                // A newer build's file: this build must not overwrite it, so nothing saves.
                File.WriteAllText(Path.Combine(root, "settings.json"), """{ "SchemaVersion": 999 }""");
                var settings = new SettingsManager();
                int raised = 0;
                settings.DisplayProfileChanged += () => raised++;

                Assert.False(settings.TrySetProfileSelection(DisplayProfileSelection.Media));
                Assert.False(settings.TrySetMediaProfile(new MediaProfile { OverrideBrightness = true }));

                Assert.Equal(DisplayProfileSelection.Normal, settings.ProfileSelection);
                Assert.False(settings.MediaProfile.OverrideBrightness);
                Assert.Equal(0, raised);
            });
        }

        [Theory]
        [InlineData("Automatic")]
        [InlineData("Normal")]
        [InlineData("Bogus")]
        public void Schema5Selection_OtherThanMedia_LoadsAsNormal(string saved)
        {
            WithDataRoot(root =>
            {
                File.WriteAllText(Path.Combine(root, "settings.json"),
                    $$"""{ "SchemaVersion": 5, "ProfileSelection": "{{saved}}" }""");
                Assert.Equal(DisplayProfileSelection.Normal, new SettingsManager().ProfileSelection);
            });
        }

        [Fact]
        public void Schema5MediaProfile_KeepsItsOverridesAndDropsGameLabFields()
        {
            // The shape Game Lab saved: the overrides carry over, game-window targeting (gone with
            // the game library) becomes every display, and everything else is ignored.
            WithDataRoot(root =>
            {
                File.WriteAllText(Path.Combine(root, "settings.json"), """
                    {
                      "SchemaVersion": 5,
                      "GamerModeEnabled": true,
                      "GamerProfiles": [ { "AppName": "arena.exe" } ],
                      "MediaProfile": {
                        "AppName": "", "PictureIntent": "Reference", "DisplayScope": "WindowDisplays",
                        "OverrideGamma": true, "GammaMode": "Gamma22",
                        "OverrideBrightness": true, "Brightness": 100,
                        "ShadowDetailStrength": 0.25, "ShadowDetailPivot": 0.1,
                        "NightPolicy": "ForceDaylight", "GameplayLock": true
                      },
                      "ProfileSelection": "Media"
                    }
                    """);

                var settings = new SettingsManager();
                MediaProfile media = settings.MediaProfile;

                Assert.Equal(DisplayProfileSelection.Media, settings.ProfileSelection);
                Assert.True(media.OverrideGamma);
                Assert.Equal(GammaMode.Gamma22, media.GammaMode);
                Assert.True(media.OverrideBrightness);
                Assert.Equal(100, media.Brightness);
                Assert.Equal(0.25, media.ShadowDetailStrength);
                Assert.Equal(MediaDisplayScope.AllDisplays, media.DisplayScope);

                // Saving writes the new schema without the retired fields.
                Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Normal));
                string saved = File.ReadAllText(Path.Combine(root, "settings.json"));
                Assert.Contains($"\"SchemaVersion\": {SettingsManager.CurrentSchemaVersion}", saved);
                Assert.DoesNotContain("GamerProfiles", saved);
                Assert.DoesNotContain("PictureIntent", saved);
            });
        }

        [Fact]
        public void Resolve_NormalUsesEachMonitorsOwnSettings()
        {
            var profile = new MonitorProfileData { GammaMode = GammaMode.Gamma24, Brightness = 75 };
            var media = new MediaProfile { OverrideGamma = true, OverrideBrightness = true, Brightness = 100, ShadowDetailStrength = 0.5 };

            var (mode, adjustments) = GammaApplyService.Resolve(
                profile, GammaMode.Gamma22, "display-1", DisplayProfileSelection.Normal, media);

            Assert.Equal(GammaMode.Gamma24, mode);
            Assert.Equal(75, adjustments.Brightness);
            Assert.Equal(0, adjustments.ShadowDetailStrength);
        }

        [Fact]
        public void Resolve_MediaLayersItsOverridesOnTheMonitor()
        {
            var profile = new MonitorProfileData { GammaMode = GammaMode.Gamma24, Brightness = 75, UseLinearBrightness = true };
            var media = new MediaProfile { OverrideBrightness = true, Brightness = 100, ShadowDetailStrength = 0.5 };

            var (mode, adjustments) = GammaApplyService.Resolve(
                profile, GammaMode.Gamma22, "display-1", DisplayProfileSelection.Media, media);

            Assert.Equal(GammaMode.Gamma24, mode);
            Assert.Equal(100, adjustments.Brightness);
            // Dark-area visibility is off whatever the Media profile saved.
            Assert.Equal(0, adjustments.ShadowDetailStrength);
            Assert.True(adjustments.UseLinearBrightness);
        }

        [Theory]
        [InlineData(DisplayProfileSelection.Normal)]
        [InlineData(DisplayProfileSelection.Media)]
        [InlineData(DisplayProfileSelection.Sdr)]
        public void Resolve_IgnoresSavedShadowPreservingDimmingAndDarkAreaVisibility(DisplayProfileSelection selection)
        {
            // What an older version saved: shadow-preserving dimming on, dark areas lifted.
            var profile = new MonitorProfileData { GammaMode = GammaMode.Gamma22, Brightness = 60, UseLinearBrightness = false };
            var media = new MediaProfile { ShadowDetailStrength = 0.6 };

            var (_, adjustments) = GammaApplyService.Resolve(profile, GammaMode.Gamma22, "display-1", selection, media);

            Assert.True(adjustments.UseLinearBrightness);
            Assert.Equal(0, adjustments.ShadowDetailStrength);
            Assert.Equal(60, adjustments.Brightness);
            // Nothing is rewritten: the saved values stay as they were.
            Assert.False(profile.UseLinearBrightness);
            Assert.Equal(0.6, media.ShadowDetailStrength);
        }

        [Fact]
        public void Resolve_MediaForOneDisplay_LeavesTheOthersOnTheirOwnSettings()
        {
            var profile = new MonitorProfileData { GammaMode = GammaMode.Gamma24, Brightness = 75 };
            var media = new MediaProfile
            {
                OverrideGamma = true, GammaMode = GammaMode.Gamma22,
                DisplayScope = MediaDisplayScope.SpecificDisplay, MonitorDevicePath = "display-2",
            };

            var first = GammaApplyService.Resolve(profile, GammaMode.Gamma22, "display-1", DisplayProfileSelection.Media, media);
            var second = GammaApplyService.Resolve(profile, GammaMode.Gamma22, "display-2", DisplayProfileSelection.Media, media);

            Assert.Equal(GammaMode.Gamma24, first.Mode);
            Assert.Equal(GammaMode.Gamma22, second.Mode);
        }

        [Fact]
        public void Resolve_SdrUsesEachMonitorsOwnSettingsAndIgnoresMedia()
        {
            var profile = new MonitorProfileData { GammaMode = GammaMode.Gamma24, Brightness = 75 };
            var media = new MediaProfile { OverrideGamma = true, GammaMode = GammaMode.Gamma22, OverrideBrightness = true, Brightness = 100 };

            var (mode, adjustments) = GammaApplyService.Resolve(
                profile, GammaMode.Gamma22, "display-1", DisplayProfileSelection.Sdr, media);

            Assert.Equal(GammaMode.Gamma24, mode);
            Assert.Equal(75, adjustments.Brightness);
        }

        [Fact]
        public void Selection_Sdr_IsSavedByNameAndSurvivesRestart()
        {
            using var data = new TempDataScope();
            Assert.True(new SettingsManager().TrySetProfileSelection(DisplayProfileSelection.Sdr));

            Assert.Contains("\"ProfileSelection\": \"Sdr\"", File.ReadAllText(data.SettingsFile));
            Assert.Equal(DisplayProfileSelection.Sdr, new SettingsManager().ProfileSelection);
        }

        [Fact]
        public void Resolve_MonitorWithoutSavedSettings_UsesItsCurrentGammaAndNeutralAdjustments()
        {
            var (mode, adjustments) = GammaApplyService.Resolve(
                null, GammaMode.Gamma24, "new-display", DisplayProfileSelection.Normal, new MediaProfile());

            Assert.Equal(GammaMode.Gamma24, mode);
            Assert.False(adjustments.HasAdjustments);
        }

        [Theory]
        [InlineData("--profile-set normal", DisplayProfileSelection.Normal)]
        [InlineData("--PROFILE-SET Media", DisplayProfileSelection.Media)]
        [InlineData("--profile-set=SDR", DisplayProfileSelection.Sdr)]
        // Automatic switching went with the game library; scripts that end a session with
        // "auto" mean "back to normal".
        [InlineData("--profile-set=auto", DisplayProfileSelection.Normal)]
        [InlineData("--other --profile-set automatic", DisplayProfileSelection.Normal)]
        public void CommandLine_ParsesProfileSet(string commandLine, DisplayProfileSelection expected)
        {
            ProfileCommand command = ProfileCommandLine.Parse(commandLine.Split(' '));

            Assert.True(command.Present);
            Assert.Null(command.Error);
            Assert.Equal(expected, command.Selection);
        }

        [Theory]
        [InlineData("--profile-set")]
        [InlineData("--profile-set game")]
        [InlineData("--profile-set=")]
        [InlineData("--profile-set normal --profile-set media")]
        public void CommandLine_RejectsMissingUnknownOrRepeatedValues(string commandLine)
        {
            ProfileCommand command = ProfileCommandLine.Parse(commandLine.Split(' '));

            Assert.True(command.Present);
            Assert.Null(command.Selection);
            Assert.NotNull(command.Error);
        }

        [Fact]
        public void CommandLine_AbsentOptionIsNotACommand()
        {
            Assert.False(ProfileCommandLine.Parse(new[] { "--smoke", "--data-dir", "x" }).Present);
            Assert.False(ProfileCommandLine.Parse(Array.Empty<string>()).Present);
        }

        [Fact]
        public void Channel_ForwardsSelectionToRunningInstanceAndReportsOutcome()
        {
            string pipeName = "Gloam.ProfileCommand.Test." + Guid.NewGuid().ToString("N");
            var received = new List<DisplayProfileSelection>();
            bool saveSucceeds = true;
            using (var server = new ProfileCommandServer(selection =>
            {
                lock (received) received.Add(selection);
                return saveSucceeds;
            }, pipeName))
            {
                server.Start();

                Assert.Equal(ProfileCommandSendResult.Applied,
                    ProfileCommandChannel.Send(DisplayProfileSelection.Media, TimeSpan.FromSeconds(5), pipeName));
                Assert.Equal(ProfileCommandSendResult.Applied,
                    ProfileCommandChannel.Send(DisplayProfileSelection.Normal, TimeSpan.FromSeconds(5), pipeName));
                saveSucceeds = false;
                Assert.Equal(ProfileCommandSendResult.NotSaved,
                    ProfileCommandChannel.Send(DisplayProfileSelection.Normal, TimeSpan.FromSeconds(5), pipeName));
            }

            lock (received)
            {
                Assert.Equal(new[]
                {
                    DisplayProfileSelection.Media,
                    DisplayProfileSelection.Normal,
                    DisplayProfileSelection.Normal
                }, received);
            }

            // After the instance exits nothing is listening, and the client gives up in time.
            Assert.Equal(ProfileCommandSendResult.Unavailable,
                ProfileCommandChannel.Send(DisplayProfileSelection.Media, TimeSpan.FromMilliseconds(300), pipeName));
        }

        [Theory]
        [InlineData(DisplayProfileSelection.Normal)]
        [InlineData(DisplayProfileSelection.Media)]
        [InlineData(DisplayProfileSelection.Sdr)]
        public void CommandLine_TrayLaunchCarriesTheSelectionAndIsMarked(DisplayProfileSelection selection)
        {
            string[] args = ProfileCommandLine.TrayLaunchArguments(selection);

            ProfileCommand command = ProfileCommandLine.Parse(args);
            Assert.Null(command.Error);
            Assert.Equal(selection, command.Selection);
            Assert.True(ProfileCommandLine.IsTrayLaunch(args));
        }

        [Fact]
        public void CommandLine_ScriptLaunchIsNotATrayLaunch()
        {
            // Otherwise a script's launch would become the tray and never exit.
            Assert.False(ProfileCommandLine.IsTrayLaunch(new[] { "--profile-set", "media" }));
            Assert.False(ProfileCommandLine.IsTrayLaunch(Array.Empty<string>()));
            Assert.False(ProfileCommandLine.IsTrayLaunch(null));
        }

        [Fact]
        public void Channel_StartingInstance_WaitsForThePipeToOpen()
        {
            string pipeName = "Gloam.ProfileCommand.Test." + Guid.NewGuid().ToString("N");
            var received = new List<DisplayProfileSelection>();
            using var server = new ProfileCommandServer(selection =>
            {
                lock (received) received.Add(selection);
                return true;
            }, pipeName);
            // The tray opens its pipe only after startup has applied the saved profiles.
            using var lateStart = new Timer(_ => server.Start(), null, 1500, Timeout.Infinite);

            Assert.Equal(ProfileCommandSendResult.Applied,
                ProfileCommandChannel.SendToStartingInstance(
                    DisplayProfileSelection.Media, TimeSpan.FromSeconds(10), () => false, pipeName));
            lock (received) Assert.Equal(DisplayProfileSelection.Media, Assert.Single(received));
        }

        [Fact]
        public void Channel_StartingInstance_StopsWaitingSoonAfterTheStartedTrayExits()
        {
            string pipeName = "Gloam.ProfileCommand.Test." + Guid.NewGuid().ToString("N");
            var clock = System.Diagnostics.Stopwatch.StartNew();

            Assert.Equal(ProfileCommandSendResult.Unavailable,
                ProfileCommandChannel.SendToStartingInstance(
                    DisplayProfileSelection.Media, TimeSpan.FromSeconds(30), () => clock.Elapsed > TimeSpan.FromSeconds(1), pipeName));
            // 1 s until the "exit", one in-flight poll, then the 2 s grace: far from the timeout.
            Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
        }

        [Theory]
        [InlineData("profile-set media", "ok")]
        [InlineData("profile-set bogus", "error invalid request")]
        [InlineData("profile-set", "error invalid request")]
        [InlineData("delete-everything normal", "error invalid request")]
        [InlineData(null, "error invalid request")]
        public void Channel_RejectsMalformedRequestsWithoutApplying(string? request, string expected)
        {
            int applied = 0;
            Assert.Equal(expected, ProfileCommandChannel.HandleRequest(request, _ =>
            {
                applied++;
                return true;
            }));
            Assert.Equal(expected == "ok" ? 1 : 0, applied);
        }

        private static void WithSettings(Action<SettingsManager> body) =>
            WithDataRoot(_ => body(new SettingsManager()));

        private static void WithDataRoot(Action<string> body)
        {
            string oldData = AppPaths.DataDir;
            string oldRoaming = AppPaths.RoamingDataDir;
            string root = Path.Combine(Path.GetTempPath(), "GloamProfileSelectionTests", Guid.NewGuid().ToString("N"));
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(root, Path.Combine(root, "roaming"));
                Directory.CreateDirectory(root);
                body(root);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(oldData, oldRoaming);
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
        }
    }
}
