using Gloam.Core;
using Gloam.UiTests.Support;

namespace Gloam.UiTests;

/// <summary>Starting, the tray start, the single instance, exiting, and <c>--profile-set</c> from scripts.</summary>
public sealed class LifecycleUiTests : IDisposable
{
    private readonly AppUnderTest _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void Closing_hides_the_flyout_and_starting_gloam_again_opens_it()
    {
        var window = _app.Start()!;

        window.ById("Close").Invoke();
        Ui.Until(() => _app.TryWindow() is null, "the flyout to hide");
        Assert.False(_app.Process!.HasExited);

        // A second plain launch (the Start menu shortcut) opens the running Gloam's flyout.
        Assert.Equal(0, _app.Run());
        _app.Window();
        Assert.Single(_app.RunningPids());
    }

    [Fact]
    public void Background_start_applies_the_displays_without_a_window()
    {
        _app.Start("--background");

        _app.ShowsRamp(0, GammaMode.Gamma24);
        Thread.Sleep(500);
        Assert.Null(_app.TryWindow());
    }

    [Fact]
    public void Exit_ends_the_app()
    {
        var window = _app.Start()!;

        window.ById("Exit").Invoke();

        Assert.True(_app.Process!.WaitForExit(TimeSpan.FromSeconds(15)));
        Assert.Contains("App: exited.", _app.Log());
    }

    [Fact]
    public void Profile_set_switches_the_running_gloam()
    {
        _app.Seed("""
            { "SchemaVersion": 6, "MediaProfile": { "OverrideBrightness": true, "Brightness": 50 } }
            """);
        var window = _app.Start()!;

        Assert.Equal(0, _app.Run("--profile-set", "media"));
        Ui.Until(() => window.ById("ProfileMedia").IsSelected(), "Media to be shown as selected");
        _app.ShowsRamp(0, GammaMode.Gamma24, brightness: 50);

        // "auto" (from scripts written for automatic switching) means Normal now.
        Assert.Equal(0, _app.Run("--profile-set", "auto"));
        Ui.Until(() => window.ById("ProfileNormal").IsSelected(), "Normal to be shown as selected");
        _app.ShowsRamp(0, GammaMode.Gamma24);

        Assert.Equal(2, _app.Run("--profile-set", "game"));
        Assert.Single(_app.RunningPids());
    }

    [Fact]
    public void Profile_set_with_no_gloam_running_starts_one_in_the_tray()
    {
        _app.Seed("""
            { "SchemaVersion": 6, "MediaProfile": { "OverrideBrightness": true, "Brightness": 50 } }
            """);

        Assert.Equal(0, _app.Run("--profile-set", "media"));

        // The tray it started holds Media on the displays, and opens no window.
        Assert.Single(_app.RunningPids());
        _app.ShowsRamp(0, GammaMode.Gamma24, brightness: 50);
        Assert.Equal("Media", _app.Settings()["ProfileSelection"]!.GetValue<string>());
        Assert.Null(_app.TryWindow());

        // Starting Gloam now opens that tray's window, already on Media.
        Assert.Equal(0, _app.Run());
        Assert.True(_app.Window().ById("ProfileMedia").IsSelected());
    }

    [Fact]
    public void Settings_from_before_the_rewrite_load_and_apply()
    {
        // What 1.11 saved: Game Lab's Media profile (game-window targeting), a selection of
        // Automatic, and fields of features that are gone.
        _app.Seed("""
            {
              "SchemaVersion": 5,
              "MonitorProfiles": { "SIM\\ONE": { "GammaMode": "Gamma24", "Brightness": 75, "Temperature": 0, "Mhc2ProfileName": null } },
              "NightMode": { "Enabled": false },
              "GamerModeEnabled": true, "GamerProfiles": [],
              "MediaProfile": { "PictureIntent": "Reference", "DisplayScope": "WindowDisplays", "OverrideBrightness": true, "Brightness": 100 },
              "ProfileSelection": "Automatic",
              "DarkTheme": true
            }
            """);

        var window = _app.Start()!;

        Assert.True(window.ById("ProfileNormal").IsSelected());
        Assert.Equal(75, window.ById("DisplayBrightness").Range());
        _app.ShowsRamp(0, GammaMode.Gamma24, brightness: 75);

        window.ById("ProfileMedia").Select();
        _app.ShowsRamp(0, GammaMode.Gamma24, brightness: 100);
        _app.ShowsRamp(1, GammaMode.Gamma24, brightness: 100);
    }
}
