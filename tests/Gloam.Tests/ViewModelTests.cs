using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Gloam.Core;
using Gloam.Services;
using Gloam.ViewModels;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// The window's view models against a fake display, updater and Start with Windows switch:
    /// every control's effect on settings.json and on the ramps the displays get.
    /// </summary>
    public sealed class ViewModelTests : IDisposable
    {
        private readonly TempDataScope _data = new();
        private readonly SettingsManager _settings = new();
        private readonly FakeMonitorSource _monitors = new()
        {
            Monitors = { FakeMonitorSource.Monitor(1, hdr: true, white: 240), FakeMonitorSource.Monitor(2, hdr: false, white: 80) },
        };
        private readonly FakeGammaRamp _ramp = new();
        private readonly FakeUpdater _updater = new();
        private readonly FakeStartupRegistration _startup = new();
        private readonly FakeHdrSwitch _hdr;
        private readonly GammaApplyService _apply;
        private readonly List<string> _revealed = [];
        private int _exits;

        public ViewModelTests()
        {
            _apply = new GammaApplyService(new GammaRampWriter(_ramp), _settings);
            _hdr = new FakeHdrSwitch(_monitors);
        }

        public void Dispose()
        {
            _apply.Dispose();
            _data.Dispose();
        }

        private MainViewModel Start()
        {
            var vm = new MainViewModel(_settings, _monitors, _apply, _updater, _startup,
                post: action => action(), exit: () => _exits++, reveal: _revealed.Add,
                hdr: new HdrModeSwitcher(_hdr), background: action => action());
            vm.Start();
            return vm;
        }

        private void AssertRamp(int display, GammaMode mode, DisplayAdjustments adjustments)
        {
            MonitorInfo monitor = _monitors.Monitors[display - 1];
            ushort[] expected = FakeGammaRamp.Expected(mode, monitor.SdrWhiteLevel, FakeGammaRamp.AsShown(adjustments), monitor.IsHdrActive);
            Eventually.True(() => _ramp.Ramp(monitor.DeviceName) is { } actual && actual.SequenceEqual(expected),
                $"display {display} to show {mode} at {adjustments.Brightness}% (shadow {adjustments.ShadowDetailStrength})");
        }

        #region Displays

        [Fact]
        public void Start_ListsTheDisplaysAndAppliesEachOne()
        {
            var vm = Start();

            Assert.Equal(new[] { "1. Panel 1", "2. Panel 2" }, vm.Monitors.Select(m => m.Title));
            Assert.True(vm.HasSeveralMonitors);
            Assert.Same(vm.Monitors[0], vm.SelectedMonitor);
            Assert.Equal("HDR · SDR white 240 nits", vm.Monitors[0].Signal);
            Assert.Equal("SDR", vm.Monitors[1].Signal);
            // A display with no saved settings starts on its current gamma (2.4) at full brightness.
            AssertRamp(1, GammaMode.Gamma24, DisplayAdjustments.Default);
            AssertRamp(2, GammaMode.Gamma24, DisplayAdjustments.Default);
        }

        [Fact]
        public void Start_RestoresSavedSettings()
        {
            _settings.SetMonitorProfile(@"SIM\1", new MonitorProfileData { GammaMode = GammaMode.Gamma22, Brightness = 60, UseLinearBrightness = true });

            var vm = Start();
            MonitorViewModel display = vm.Monitors[0];

            Assert.True(display.IsGamma22);
            Assert.Equal(60, display.Brightness);
            Assert.Equal("60%", display.BrightnessText);
            AssertRamp(1, GammaMode.Gamma22, new DisplayAdjustments { Brightness = 60, UseLinearBrightness = true });
        }

        [Fact]
        public void DisplayControls_SaveAndApplyAtOnce()
        {
            var vm = Start();
            MonitorViewModel display = vm.Monitors[1];

            display.IsGamma22 = true;
            display.Brightness = 70;

            MonitorProfileData saved = new SettingsManager().GetMonitorProfile(@"SIM\2")!;
            Assert.Equal(GammaMode.Gamma22, saved.GammaMode);
            Assert.Equal(70, saved.Brightness);
            AssertRamp(2, GammaMode.Gamma22, new DisplayAdjustments { Brightness = 70, UseLinearBrightness = true });
            // The other display is untouched.
            Assert.Null(_settings.GetMonitorProfile(@"SIM\1"));
        }

        [Theory]
        [InlineData(5, 10)]
        [InlineData(140, 100)]
        [InlineData(55.4, 55)]
        [InlineData(double.NaN, 100)]
        public void Brightness_IsWholePercentWithinTenToHundred(double set, double expected)
        {
            var vm = Start();
            vm.Monitors[0].Brightness = set;
            Assert.Equal(expected, vm.Monitors[0].Brightness);
        }

        [Fact]
        public void Refresh_FindsNewDisplaysAndKeepsTheSelectedOne()
        {
            var vm = Start();
            vm.SelectedMonitor = vm.Monitors[1];

            _monitors.Monitors.Insert(0, FakeMonitorSource.Monitor(3));
            vm.RefreshCommand.Execute(null);

            Assert.Equal(3, vm.Monitors.Count);
            Assert.Equal(@"SIM\2", vm.SelectedMonitor!.DevicePath);
            Assert.Contains(vm.Media.DisplayChoices, c => c.DevicePath == @"SIM\3");
        }

        [Fact]
        public void Refresh_ChoosesTheMediaDisplayAgainFromTheNewList()
        {
            var vm = Start();
            vm.Media.AppliesTo = vm.Media.DisplayChoices.Single(c => c.DevicePath == @"SIM\2");
            var raised = new List<string?>();
            vm.Media.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.RefreshCommand.Execute(null);

            // The picker lost its selection with the old items: the choice must be raised
            // again, and be an item of the new list.
            Assert.Contains(nameof(MediaProfileViewModel.AppliesTo), raised);
            Assert.Contains(vm.Media.DisplayChoices, c => ReferenceEquals(c, vm.Media.AppliesTo));
            Assert.Equal(@"SIM\2", vm.Media.AppliesTo!.DevicePath);
        }

        [Fact]
        public void NoDisplays_IsShownAsSuch()
        {
            _monitors.Monitors.Clear();
            var vm = Start();

            Assert.False(vm.HasMonitors);
            Assert.False(vm.HasSeveralMonitors);
            Assert.Null(vm.SelectedMonitor);
        }

        [Fact]
        public async Task DisplayEvent_EnumeratesAgainAndRewritesTheRamps()
        {
            var vm = Start();
            AssertRamp(1, GammaMode.Gamma24, DisplayAdjustments.Default);
            int writes = _ramp.Writes;

            // Windows reset the ramp during the display change.
            _ramp.Overwrite(@"\\.\DISPLAY1");
            await vm.HandleDisplayEventAsync(TimeSpan.FromMilliseconds(10));

            AssertRamp(1, GammaMode.Gamma24, DisplayAdjustments.Default);
            Assert.True(_ramp.Writes > writes);
        }

        #endregion

        #region Profiles

        [Fact]
        public void Media_AppliesItsOverridesAndNormalRestoresTheDisplays()
        {
            _settings.SetMonitorProfile(@"SIM\1", new MonitorProfileData { GammaMode = GammaMode.Gamma24, Brightness = 75 });
            var vm = Start();
            vm.Media.OverrideBrightness = true;
            vm.Media.Brightness = 100;
            var changes = 0;
            vm.ProfileChanged += () => changes++;

            vm.IsMedia = true;

            Assert.True(vm.IsMedia);
            Assert.False(vm.IsNormal);
            Assert.Equal(DisplayProfileSelection.Media, new SettingsManager().ProfileSelection);
            Assert.Equal(1, changes);
            Assert.Equal("Media on all displays", vm.ProfileDetail);
            Assert.Equal("Media is active on this display and sets its brightness.", vm.Monitors[0].MediaNote);
            AssertRamp(1, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 100 });

            vm.IsNormal = true;

            Assert.Null(vm.Monitors[0].MediaNote);
            Assert.Equal("Each display's own settings", vm.ProfileDetail);
            AssertRamp(1, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 75 });
            Assert.Equal(2, changes);
        }

        [Fact]
        public void Sdr_TurnsHdrOffAndAppliesTheDisplaysOwnSettingsInSdr()
        {
            _settings.SetMonitorProfile(@"SIM\1", new MonitorProfileData { GammaMode = GammaMode.Gamma24, Brightness = 75 });
            var vm = Start();
            vm.Media.OverrideBrightness = true;
            vm.Media.Brightness = 100;
            var changes = 0;
            vm.ProfileChanged += () => changes++;

            vm.IsSdr = true;

            Assert.True(vm.IsSdr);
            Assert.False(vm.IsNormal);
            Assert.False(vm.IsMedia);
            Assert.Equal(1, changes);
            Assert.Equal(DisplayProfileSelection.Sdr, new SettingsManager().ProfileSelection);
            Assert.Equal("HDR off, each display's own settings", vm.ProfileDetail);
            // Only display 1 has HDR to turn off; display 2 is SDR-only.
            Assert.Equal(new[] { (@"\\.\DISPLAY1", false) }, _hdr.Switches);
            Assert.False(vm.Monitors[0].IsHdr);
            Assert.Null(vm.Monitors[0].MediaNote);
            AssertRamp(1, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 75 });

            vm.IsNormal = true;

            Assert.Equal(new[] { (@"\\.\DISPLAY1", false), (@"\\.\DISPLAY1", true) }, _hdr.Switches);
            Assert.True(vm.Monitors[0].IsHdr);
            AssertRamp(1, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 75 });
        }

        [Fact]
        public void Media_TurnsHdrBackOnAfterSdr()
        {
            var vm = Start();
            vm.IsSdr = true;

            vm.IsMedia = true;

            Assert.True(_monitors.Monitors[0].IsHdrActive);
            Assert.True(vm.Monitors[0].IsHdr);
        }

        [Fact]
        public void Start_PutsTheDisplaysInTheSavedProfilesMode()
        {
            Assert.True(_settings.TrySetProfileSelection(DisplayProfileSelection.Sdr));

            var vm = Start();

            Assert.Equal(new[] { (@"\\.\DISPLAY1", false) }, _hdr.Switches);
            Assert.False(vm.Monitors[0].IsHdr);
            AssertRamp(1, GammaMode.Gamma24, DisplayAdjustments.Default);
        }

        [Fact]
        public async Task HdrChangedByHand_HoldsUntilTheNextProfileSwitch()
        {
            var vm = Start();
            vm.IsMedia = true;

            // Win+Alt+B: HDR goes off outside Gloam, Windows reports a display change.
            _monitors.Monitors[0].IsHdrActive = false;
            await vm.HandleDisplayEventAsync(TimeSpan.Zero);
            // Editing the Media profile is not a profile switch either.
            vm.Media.Brightness = 60;

            Assert.Empty(_hdr.Switches);
            Assert.False(vm.Monitors[0].IsHdr);

            vm.IsNormal = true;

            Assert.Equal(new[] { (@"\\.\DISPLAY1", true) }, _hdr.Switches);
        }

        [Fact]
        public void RefusedHdrSwitch_KeepsTheProfileAndTheDisplaysAsTheyAre()
        {
            var vm = Start();
            _hdr.Accepts = false;

            vm.IsSdr = true;

            Assert.True(vm.IsSdr);
            Assert.True(vm.Monitors[0].IsHdr);
            AssertRamp(1, GammaMode.Gamma24, DisplayAdjustments.Default);
        }

        [Fact]
        public async Task ProfileSetFromAnotherThread_ReachesTheWindowAndTheDisplays()
        {
            // `Gloam.exe --profile-set media` arrives on the pipe server's thread.
            var vm = Start();
            vm.Media.OverrideGamma = true;
            vm.Media.IsGamma22 = true;

            Assert.True(await Task.Run(() => vm.SelectProfile(DisplayProfileSelection.Media)));

            Assert.True(vm.IsMedia);
            AssertRamp(1, GammaMode.Gamma22, DisplayAdjustments.Default);
            AssertRamp(2, GammaMode.Gamma22, DisplayAdjustments.Default);
        }

        [Fact]
        public void MediaEdits_WhileMediaIsOn_ApplyAtOnce()
        {
            var vm = Start();
            vm.IsMedia = true;

            vm.Media.OverrideBrightness = true;
            vm.Media.Brightness = 40;

            AssertRamp(1, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 40 });
            Assert.Equal("Brightness 40%", vm.Media.Summary);
        }

        [Fact]
        public void MediaForOneDisplay_LeavesTheOtherOnItsOwnSettings()
        {
            var vm = Start();
            vm.Media.OverrideBrightness = true;
            vm.Media.Brightness = 30;
            vm.Media.AppliesTo = vm.Media.DisplayChoices.Single(c => c.DevicePath == @"SIM\2");

            vm.IsMedia = true;

            Assert.Equal("Media on 2. Panel 2", vm.ProfileDetail);
            Assert.Null(vm.Monitors[0].MediaNote);
            Assert.NotNull(vm.Monitors[1].MediaNote);
            AssertRamp(1, GammaMode.Gamma24, DisplayAdjustments.Default);
            AssertRamp(2, GammaMode.Gamma24, new DisplayAdjustments { Brightness = 30 });
        }

        [Fact]
        public void ChoiceThatCannotBeSaved_KeepsTheOldOneAndSaysSo()
        {
            var vm = Start();
            // A newer Gloam's settings file appears: this build may not write over it.
            File.WriteAllText(_data.SettingsFile, """{ "SchemaVersion": 999 }""");
            _settings.Load();

            vm.IsMedia = true;

            Assert.True(vm.IsNormal);
            Assert.Equal("Gloam could not save the profile choice.", vm.Status);
            Assert.True(vm.HasStatus);
        }

        #endregion

        #region Media profile editor

        [Fact]
        public void MediaEditor_SavesEveryControl()
        {
            var vm = Start();
            MediaProfileViewModel media = vm.Media;

            media.OverrideGamma = true;
            media.IsGamma24 = true;
            media.OverrideBrightness = true;
            media.Brightness = 65;
            media.AppliesTo = media.DisplayChoices.Single(c => c.DevicePath == @"SIM\1");

            MediaProfile saved = new SettingsManager().MediaProfile;
            Assert.True(saved.OverrideGamma);
            Assert.Equal(GammaMode.Gamma24, saved.GammaMode);
            Assert.True(saved.OverrideBrightness);
            Assert.Equal(65, saved.Brightness);
            Assert.Equal(MediaDisplayScope.SpecificDisplay, saved.DisplayScope);
            Assert.Equal(@"SIM\1", saved.MonitorDevicePath);
            Assert.Equal("Gamma 2.4, brightness 65%", media.Summary);

            media.AppliesTo = media.DisplayChoices[0];
            Assert.Equal(MediaDisplayScope.AllDisplays, new SettingsManager().MediaProfile.DisplayScope);
        }

        [Fact]
        public void MediaEditor_DefaultsToTheSameAsNormal()
        {
            var vm = Start();

            Assert.Equal("Same as Normal", vm.Media.Summary);
            Assert.Equal(MediaProfileViewModel.AllDisplaysLabel, vm.Media.AppliesTo!.Label);
            Assert.Equal(new[] { "All displays", "1. Panel 1", "2. Panel 2" }, vm.Media.DisplayChoices.Select(c => c.Label));
        }

        [Fact]
        public void MediaEditor_KeepsADisconnectedDisplayChoice()
        {
            Assert.True(_settings.TrySetMediaProfile(new MediaProfile
            {
                DisplayScope = MediaDisplayScope.SpecificDisplay, MonitorDevicePath = @"SIM\GONE",
            }));

            var vm = Start();

            Assert.Equal(@"SIM\GONE", vm.Media.AppliesTo!.DevicePath);
            Assert.Equal("A disconnected display", vm.Media.AppliesTo.Label);
            // Choosing it again saved nothing new: the profile still names the missing display.
            Assert.Equal(@"SIM\GONE", new SettingsManager().MediaProfile.MonitorDevicePath);
        }

        [Fact]
        public void MediaEditor_EditThatCannotBeSaved_ShowsTheSavedValuesAgain()
        {
            var vm = Start();
            File.WriteAllText(_data.SettingsFile, """{ "SchemaVersion": 999 }""");
            _settings.Load();

            vm.Media.OverrideBrightness = true;

            Assert.False(vm.Media.OverrideBrightness);
            Assert.Equal("Gloam could not save the Media profile.", vm.Status);
        }

        #endregion

        #region Footer

        [Fact]
        public void StartWithWindows_FollowsTheRegistration()
        {
            _startup.IsEnabled = true;
            var vm = Start();
            Assert.True(vm.StartWithWindows);

            vm.StartWithWindows = false;
            Assert.False(_startup.IsEnabled);

            _startup.Fails = true;
            vm.StartWithWindows = true;
            Assert.False(vm.StartWithWindows);
            Assert.Equal("Gloam could not change Start with Windows.", vm.Status);
        }

        [Fact]
        public void ExportDiagnostics_SavesABundleAndShowsIt()
        {
            var vm = Start();

            vm.ExportDiagnosticsCommand.Execute(null);

            string path = Assert.Single(_revealed);
            Assert.True(File.Exists(path));
            Assert.StartsWith(Path.Combine(_data.Root, "Diagnostics"), path);
            Assert.Equal($"Diagnostics saved to {path}", vm.Status);
        }

        [Fact]
        public void Exit_ExitsTheApp()
        {
            var vm = Start();
            vm.ExitCommand.Execute(null);
            Assert.Equal(1, _exits);
        }

        #endregion

        #region Updates

        [Fact]
        public async Task Update_UpToDate()
        {
            var vm = Start();
            Assert.Equal("v1.2.3", vm.Update.Version);
            Assert.Equal("Check for updates", vm.Update.ActionText);

            await vm.Update.ActAsync();

            Assert.Equal("You have the latest version.", vm.Update.Status);
            Assert.Equal("Check for updates", vm.Update.ActionText);
            Assert.False(vm.Update.IsBusy);
            Assert.True(vm.Update.ActCommand.CanExecute(null));
        }

        [Fact]
        public async Task Update_OffersTheNewReleaseThenInstallsItAndExits()
        {
            UpdateRelease release = FakeUpdater.Release("1.3.0", "Fixes.");
            _updater.Check = () => new UpdateCheckResult(UpdateCheckStatus.Available, release, null);
            _updater.Download = _ => @"C:\Updates\Gloam-1.3.0-setup.exe";
            var vm = Start();

            await vm.Update.ActAsync();
            Assert.Equal("Update to 1.3.0", vm.Update.ActionText);
            Assert.StartsWith("Gloam 1.3.0 is available.", vm.Update.Status);
            Assert.Equal("Fixes.", vm.Update.Notes);
            Assert.True(vm.Update.HasNotes);
            Assert.Equal(0, _exits);

            await vm.Update.ActAsync();
            Assert.Equal((@"C:\Updates\Gloam-1.3.0-setup.exe", release.Sha256), Assert.Single(_updater.Installed));
            Assert.Equal(1, _exits);
            // Busy for good: the setup is about to replace this process.
            Assert.True(vm.Update.IsBusy);
        }

        [Fact]
        public async Task Update_NotAnInstallation_PointsAtTheReleasesPage()
        {
            _updater.IsInstallation = false;
            _updater.Check = () => new UpdateCheckResult(UpdateCheckStatus.Available, FakeUpdater.Release(), null);
            var vm = Start();

            await vm.Update.ActAsync();

            Assert.Contains("not an installation", vm.Update.Status);
            Assert.Contains(UpdateService.ReleasesPageUrl, vm.Update.Status);
            Assert.Equal("Check for updates", vm.Update.ActionText);
        }

        [Fact]
        public async Task Update_FailedCheckAndFailedInstall_SayWhy()
        {
            _updater.Check = () => new UpdateCheckResult(UpdateCheckStatus.Failed, null, "GitHub answered 404");
            var vm = Start();
            await vm.Update.ActAsync();
            Assert.Equal("Could not check for updates: GitHub answered 404", vm.Update.Status);

            _updater.Check = () => throw new TaskCanceledException();
            await vm.Update.ActAsync();
            Assert.Equal("Could not check for updates: GitHub did not answer in time", vm.Update.Status);

            _updater.Check = () => new UpdateCheckResult(UpdateCheckStatus.Available, FakeUpdater.Release(), null);
            _updater.InstallFailure = new InvalidDataException("hash mismatch");
            await vm.Update.ActAsync();
            await vm.Update.ActAsync();
            Assert.Equal("The update failed: hash mismatch", vm.Update.Status);
            Assert.Equal("Update to 1.3.0", vm.Update.ActionText);
            Assert.False(vm.Update.IsBusy);
            Assert.Equal(0, _exits);
        }

        [Fact]
        public async Task Update_LongNotesAreCut()
        {
            _updater.Check = () => new UpdateCheckResult(UpdateCheckStatus.Available,
                FakeUpdater.Release(notes: new string('x', UpdateViewModel.MaxNotesLength + 500)), null);
            var vm = Start();

            await vm.Update.ActAsync();

            Assert.Equal(UpdateViewModel.MaxNotesLength + 2, vm.Update.Notes.Length);
            Assert.EndsWith("\n…", vm.Update.Notes);
        }

        #endregion

        [Fact]
        public void MediaNote_NamesWhatMediaSets()
        {
            var media = new MediaProfile { OverrideGamma = true, OverrideBrightness = true };
            Assert.Equal("Media is active on this display and sets its gamma and brightness.",
                MainViewModel.MediaNote(DisplayProfileSelection.Media, media, "A"));
            Assert.Equal("Media is active on this display.",
                MainViewModel.MediaNote(DisplayProfileSelection.Media, new MediaProfile(), "A"));
            Assert.Null(MainViewModel.MediaNote(DisplayProfileSelection.Normal, media, "A"));
        }
    }
}
