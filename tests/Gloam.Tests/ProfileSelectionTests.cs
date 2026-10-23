using System.IO;
using Gloam.Core;
using Gloam.Interop;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Two display profiles: Normal (monitor settings) and Media (the one Game Lab settings
    /// set). Automatic switches between them on library games; a manual choice is sticky.
    /// </summary>
    public sealed class ProfileSelectionTests
    {
        [Fact]
        public void GameSession_UsesMediaSettingsUnderTheGamesIdentity()
        {
            WithSettings(settings =>
            {
                var monitors = Monitors();
                GamerProfileRule game = Game("arena.exe");
                Assert.True(settings.TrySetGamerProfiles(new[] { game }));
                GamerProfileRule media = GamerPresetCatalog.Create(string.Empty, GamerPictureIntent.CompetitiveClarity);
                media.ShadowDetailStrength = 0.30;
                media.DisplayScope = GamerDisplayScope.AllDisplays;
                Assert.True(settings.TrySetMediaProfile(media));
                Assert.True(settings.TrySetGamerModeEnabled(true));
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                using var coordinator = new GamerModeCoordinator(settings, apply, () => monitors,
                    () => Array.Empty<RunningAppObservation>());

                coordinator.ObserveForeground(game.AppName, game.ExecutablePath, monitors[0].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);

                Assert.Equal(2, coordinator.ActiveSessions.Count);
                Assert.All(coordinator.ActiveSessions, session =>
                {
                    Assert.Equal("arena.exe", session.AppName);
                    Assert.Equal(GamerPictureIntent.CompetitiveClarity, session.PictureIntent);
                    Assert.Equal(0.30, session.ShadowDetailStrength, 6);
                });

                // Leaving the game restores Normal.
                coordinator.ObserveForeground("editor.exe", null, monitors[0].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Empty(coordinator.ActiveSessions);
            });
        }

        [Fact]
        public void ManualSelection_IsStickyAcrossFocusAndRestartUntilAutomatic()
        {
            WithSettings(settings =>
            {
                var monitors = Monitors();
                GamerProfileRule game = Game("arena.exe");
                Assert.True(settings.TrySetGamerProfiles(new[] { game }));
                Assert.True(settings.TrySetGamerModeEnabled(true));
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                using var coordinator = new GamerModeCoordinator(settings, apply, () => monitors,
                    () => Array.Empty<RunningAppObservation>());
                coordinator.ObserveForeground(game.AppName, game.ExecutablePath, monitors[0].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Single(coordinator.ActiveSessions);

                // Normal wins over a focused library game.
                Assert.True(coordinator.TrySetProfileSelection(DisplayProfileSelection.Normal));
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Empty(coordinator.ActiveSessions);
                coordinator.ObserveForeground(game.AppName, game.ExecutablePath, monitors[0].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Empty(coordinator.ActiveSessions);

                // Media covers every display without a game; focus changes don't restart it.
                Assert.True(coordinator.TrySetProfileSelection(DisplayProfileSelection.Media));
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal(2, coordinator.ActiveSessions.Count);
                Assert.All(coordinator.ActiveSessions, session =>
                    Assert.Equal(GamerMediaProfile.ManualSessionTitle, session.DisplayName));
                DateTime started = coordinator.ActiveSessions[0].StartedUtc;
                coordinator.ObserveForeground("editor.exe", null, monitors[0].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);
                coordinator.ObserveForeground(game.AppName, game.ExecutablePath, monitors[0].MonitorBounds);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal(2, coordinator.ActiveSessions.Count);
                Assert.Equal(started, coordinator.ActiveSessions[0].StartedUtc);

                // Pausing automatic switching doesn't override an explicit Media choice.
                Assert.True(coordinator.TrySetEnabled(false));
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal(2, coordinator.ActiveSessions.Count);
                Assert.True(coordinator.TrySetEnabled(true));

                Assert.Equal(DisplayProfileSelection.Media, new SettingsManager().ProfileSelection);

                Assert.True(coordinator.TrySetProfileSelection(DisplayProfileSelection.Automatic));
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal("arena.exe", Assert.Single(coordinator.ActiveSessions).AppName);
            });
        }

        [Fact]
        public void ManualMedia_HonorsSpecificDisplayAndPanicUntilReselected()
        {
            WithSettings(settings =>
            {
                var monitors = Monitors();
                GamerProfileRule media = GamerMediaProfile.CreateDefault();
                media.DisplayScope = GamerDisplayScope.SpecificDisplay;
                media.MonitorDevicePath = "display-2";
                Assert.True(settings.TrySetMediaProfile(media));
                Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Media));
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                using var coordinator = new GamerModeCoordinator(settings, apply, () => monitors,
                    () => Array.Empty<RunningAppObservation>());

                // No library, Game Mode off: a manual Media choice still applies.
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Equal("display-2", Assert.Single(coordinator.ActiveSessions).MonitorDevicePath);

                coordinator.EmergencySuspend();
                Assert.Empty(coordinator.ActiveSessions);
                coordinator.ReevaluateLatest(immediate: true);
                Assert.Empty(coordinator.ActiveSessions);

                // Choosing a profile again is an explicit re-arm, even when it is unchanged.
                Assert.True(coordinator.TrySetProfileSelection(DisplayProfileSelection.Media));
                Assert.Single(coordinator.ActiveSessions);
            });
        }

        [Fact]
        public void ActiveSessions_ManualMediaNeedsNoExecutableButStaysSingleOwner()
        {
            WithSettings(settings =>
            {
                var monitors = Monitors();
                using var night = new NightModeService(new NightModeSettings());
                using var apply = new GammaApplyService(new DispwinRunner(), settings, night,
                    (_, _) => GamerCalibrationStatus.None);
                GamerProfileRule manual = GamerMediaProfile.ForManualSession(GamerMediaProfile.CreateDefault());

                // Without the manual flag an ownerless rule fails the executable safety check.
                Assert.False(apply.UpdateActiveGamerSessions(new[] { new GamerSessionAssignment(monitors[0], manual) }));
                Assert.Empty(apply.ActiveGamerSessions);

                GamerProfileRule game = GamerMediaProfile.ForGame(GamerMediaProfile.CreateDefault(), Game("arena.exe"));
                Assert.True(apply.UpdateActiveGamerSessions(new[]
                {
                    new GamerSessionAssignment(monitors[0], manual, Manual: true),
                    new GamerSessionAssignment(monitors[1], game)
                }));
                GamerSessionSnapshot session = Assert.Single(apply.ActiveGamerSessions);
                Assert.Equal(GamerMediaProfile.ManualSessionTitle, session.DisplayName);
                Assert.Equal(string.Empty, session.AppName);
            });
        }

        [Fact]
        public void Schema4Migration_SeedsMediaFromMostRecentEnabledGameAndStripsLibrary()
        {
            WithDataRoot(root =>
            {
                File.WriteAllText(Path.Combine(root, "settings.json"), """
                    {
                      "SchemaVersion": 4,
                      "GamerModeEnabled": true,
                      "GamerProfiles": [
                        {
                          "AppName": "older.exe",
                          "PictureIntent": "CompetitiveClarity",
                          "ShadowDetailStrength": 0.55,
                          "LastUsedUtc": "2026-07-01T20:00:00Z"
                        },
                        {
                          "AppName": "newest.exe",
                          "PictureIntent": "Custom",
                          "ShadowDetailStrength": 0.25,
                          "OverrideBrightness": true,
                          "Brightness": 70,
                          "ApplyWhileRunning": true,
                          "LastUsedUtc": "2026-07-10T20:00:00Z"
                        },
                        {
                          "AppName": "disabled.exe",
                          "Enabled": false,
                          "PictureIntent": "NightOps",
                          "LastUsedUtc": "2026-07-20T20:00:00Z"
                        }
                      ]
                    }
                    """);

                var settings = new SettingsManager();

                GamerProfileRule media = settings.MediaProfile;
                Assert.Equal(GamerPictureIntent.Custom, media.PictureIntent);
                Assert.Equal(0.25, media.ShadowDetailStrength, 6);
                Assert.True(media.OverrideBrightness);
                Assert.Equal(70.0, media.Brightness, 6);
                Assert.True(media.ApplyWhileRunning);
                Assert.Equal(string.Empty, media.AppName);
                Assert.Null(media.LastUsedUtc);
                Assert.Equal(DisplayProfileSelection.Automatic, settings.ProfileSelection);

                Assert.Equal(new[] { "older.exe", "newest.exe", "disabled.exe" },
                    settings.GamerProfiles.Select(entry => entry.AppName));
                Assert.All(settings.GamerProfiles, entry =>
                {
                    Assert.True(entry.SemanticallyEquals(GamerMediaProfile.ToLibraryEntry(entry)));
                    Assert.NotNull(entry.LastUsedUtc);
                });
                Assert.False(settings.GamerProfiles[2].Enabled);

                // Once written as schema 5, the seeded profile is not re-derived on load.
                Assert.True(settings.TrySetProfileSelection(DisplayProfileSelection.Normal));
                var reloaded = new SettingsManager();
                Assert.Equal(GamerPictureIntent.Custom, reloaded.MediaProfile.PictureIntent);
                Assert.Equal(DisplayProfileSelection.Normal, reloaded.ProfileSelection);
            });
        }

        [Fact]
        public void Schema4Migration_NeverActivatedLibraryStartsFromDefaultMedia()
        {
            WithDataRoot(root =>
            {
                File.WriteAllText(Path.Combine(root, "settings.json"), """
                    {
                      "SchemaVersion": 4,
                      "GamerProfiles": [
                        { "AppName": "arena.exe", "PictureIntent": "NightOps", "NightPolicy": "NightOps" }
                      ]
                    }
                    """);

                var settings = new SettingsManager();

                Assert.True(settings.MediaProfile.SemanticallyEquals(GamerMediaProfile.CreateDefault()));
                Assert.Equal("arena.exe", Assert.Single(settings.GamerProfiles).AppName);
            });
        }

        [Theory]
        [InlineData("--profile-set normal", DisplayProfileSelection.Normal)]
        [InlineData("--PROFILE-SET Media", DisplayProfileSelection.Media)]
        [InlineData("--profile-set=auto", DisplayProfileSelection.Automatic)]
        [InlineData("--other --profile-set automatic", DisplayProfileSelection.Automatic)]
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
                    ProfileCommandChannel.Send(DisplayProfileSelection.Automatic, TimeSpan.FromSeconds(5), pipeName));
                saveSucceeds = false;
                Assert.Equal(ProfileCommandSendResult.NotSaved,
                    ProfileCommandChannel.Send(DisplayProfileSelection.Normal, TimeSpan.FromSeconds(5), pipeName));
            }

            lock (received)
            {
                Assert.Equal(new[]
                {
                    DisplayProfileSelection.Media,
                    DisplayProfileSelection.Automatic,
                    DisplayProfileSelection.Normal
                }, received);
            }

            // After the instance exits nothing is listening, and the client gives up in time.
            Assert.Equal(ProfileCommandSendResult.Unavailable,
                ProfileCommandChannel.Send(DisplayProfileSelection.Media, TimeSpan.FromMilliseconds(300), pipeName));
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

        private static GamerProfileRule Game(string name) => new()
        {
            AppName = name,
            ExecutablePath = Path.GetFullPath(@"C:\Games\" + name)
        };

        private static MonitorInfo[] Monitors() => new[]
        {
            new MonitorInfo { HMonitor = (IntPtr)201, MonitorDevicePath = "display-1", FriendlyName = "Left",
                MonitorBounds = new Dxgi.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 } },
            new MonitorInfo { HMonitor = (IntPtr)202, MonitorDevicePath = "display-2", FriendlyName = "Right",
                MonitorBounds = new Dxgi.RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 } }
        };

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
