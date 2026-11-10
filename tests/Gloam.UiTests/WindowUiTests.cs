using Gloam.Core;
using Gloam.UiTests.Support;

namespace Gloam.UiTests;

/// <summary>The flyout's controls, end to end: what each one saves and what the displays then show.</summary>
public sealed class WindowUiTests : IDisposable
{
    private readonly AppUnderTest _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void Starting_opens_the_flyout_and_applies_every_display()
    {
        var window = _app.Start()!;

        Assert.True(window.ById("ProfileNormal").IsSelected());
        window.HasText("ProfileDetail", "Each display's own settings");
        Assert.Equal("1. Sim One", window.ById("DisplayPicker").Selection());
        window.HasText("DisplaySignal", "HDR · SDR white 240 nits");
        Assert.True(window.ById("DisplayGamma24").IsSelected());
        // A display Gloam has no settings for starts on gamma 2.4 at full brightness.
        _app.ShowsRamp(0, GammaMode.Gamma24);
        _app.ShowsRamp(1, GammaMode.Gamma24);
    }

    [Fact]
    public void Display_controls_save_and_apply_at_once()
    {
        var window = _app.Start()!;

        window.ById("DisplayGamma22").Select();
        window.ById("DisplayBrightness").SetRange(60);
        window.HasText("DisplayBrightnessText", "60%");
        window.ById("DisplayPreserveShadows").SetChecked(false);

        _app.ShowsRamp(0, GammaMode.Gamma22, brightness: 60, linear: true);
        _app.ShowsRamp(1, GammaMode.Gamma24);
        Ui.Until(() => _app.Settings()["MonitorProfiles"]?[@"SIM\ONE"]?["Brightness"]?.GetValue<double>() == 60,
            "the brightness to be saved");
        var saved = _app.Settings()["MonitorProfiles"]![@"SIM\ONE"]!;
        Assert.Equal("Gamma22", saved["GammaMode"]!.GetValue<string>());
        Assert.True(saved["UseLinearBrightness"]!.GetValue<bool>());
    }

    [Fact]
    public void The_display_picker_switches_which_display_the_controls_change()
    {
        var window = _app.Start()!;

        window.ById("DisplayPicker").Choose("2. Sim Two");
        window.HasText("DisplaySignal", "SDR");
        window.ById("DisplayGamma22").Select();
        window.ById("DisplayBrightness").SetRange(40);

        _app.ShowsRamp(1, GammaMode.Gamma22, brightness: 40);
        _app.ShowsRamp(0, GammaMode.Gamma24);
    }

    [Fact]
    public void Saved_settings_are_shown_and_applied_at_start()
    {
        _app.Seed("""
            { "SchemaVersion": 6,
              "MonitorProfiles": { "SIM\\ONE": { "GammaMode": "Gamma22", "Brightness": 75 } } }
            """);

        var window = _app.Start()!;

        Assert.True(window.ById("DisplayGamma22").IsSelected());
        Assert.Equal(75, window.ById("DisplayBrightness").Range());
        _app.ShowsRamp(0, GammaMode.Gamma22, brightness: 75);
    }

    [Fact]
    public void Media_profile_overrides_apply_while_media_is_on()
    {
        var window = _app.Start()!;
        window.ById("MediaOverrideBrightness").SetChecked(true);
        window.ById("MediaBrightness").SetRange(40);
        window.ById("MediaShadowDetail").SetRange(30);
        window.HasText("MediaSummary", "Brightness 40%, dark areas +30%");
        _app.ShowsRamp(0, GammaMode.Gamma24); // Normal: Media changes nothing yet

        window.ById("ProfileMedia").Select();

        window.HasText("ProfileDetail", "Media on all displays");
        window.HasText("DisplayMediaNote", "Media is active on this display and sets its brightness.");
        _app.ShowsRamp(0, GammaMode.Gamma24, brightness: 40, shadow: 0.3);
        _app.ShowsRamp(1, GammaMode.Gamma24, brightness: 40, shadow: 0.3);
        Ui.Until(() => _app.Settings()["ProfileSelection"]?.GetValue<string>() == "Media", "Media to be saved");

        window.ById("ProfileNormal").Select();
        _app.ShowsRamp(0, GammaMode.Gamma24);
    }

    [Fact]
    public void Media_can_cover_one_display_only()
    {
        var window = _app.Start()!;
        window.ById("MediaOverrideGamma").SetChecked(true);
        window.ById("MediaGamma22").Select();
        window.ById("MediaAppliesTo").Choose("2. Sim Two");

        window.ById("ProfileMedia").Select();

        window.HasText("ProfileDetail", "Media on 2. Sim Two");
        _app.ShowsRamp(1, GammaMode.Gamma22);
        _app.ShowsRamp(0, GammaMode.Gamma24);
    }

    [Fact]
    public void Start_with_windows_registers_a_tray_start()
    {
        var window = _app.Start()!;

        window.ById("StartWithWindows").SetChecked(true);
        Ui.Until(() => _app.RunValue() != null, "the Run value");
        Assert.Equal($"\"{AppUnderTest.Exe}\" --background", _app.RunValue());

        window.ById("StartWithWindows").SetChecked(false);
        Ui.Until(() => _app.RunValue() == null, "the Run value to go");
    }

    [Fact]
    public void Export_diagnostics_saves_a_bundle_in_the_data_folder()
    {
        var window = _app.Start()!;

        window.ById("ExportDiagnostics").Invoke();

        Ui.Until(() => Directory.Exists(Path.Combine(_app.DataFolder, "Diagnostics")) &&
                       Directory.GetFiles(Path.Combine(_app.DataFolder, "Diagnostics"), "*.zip").Length == 1,
            "the diagnostics bundle");
        Assert.StartsWith("Diagnostics saved to ", window.ById("Status").Text());
    }
}
