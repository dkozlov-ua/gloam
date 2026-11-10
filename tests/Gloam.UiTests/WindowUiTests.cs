using System.Windows.Automation;
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

        _app.ShowsRamp(0, GammaMode.Gamma22, brightness: 60);
        _app.ShowsRamp(1, GammaMode.Gamma24);
        Ui.Until(() => _app.Settings()["MonitorProfiles"]?[@"SIM\ONE"]?["Brightness"]?.GetValue<double>() == 60,
            "the brightness to be saved");
        var saved = _app.Settings()["MonitorProfiles"]![@"SIM\ONE"]!;
        Assert.Equal("Gamma22", saved["GammaMode"]!.GetValue<string>());
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
    public void Saved_shadow_preserving_dimming_and_dark_area_visibility_are_hidden_and_ignored()
    {
        // What an older version saved: shadow-preserving dimming on, Media lifting dark areas.
        _app.Seed("""
            { "SchemaVersion": 6,
              "MonitorProfiles": { "SIM\\ONE": { "GammaMode": "Gamma22", "Brightness": 60, "UseLinearBrightness": false } },
              "MediaProfile": { "ShadowDetailStrength": 0.5 },
              "ProfileSelection": "Media" }
            """);

        var window = _app.Start()!;

        _app.ShowsRamp(0, GammaMode.Gamma22, brightness: 60); // straight dimming, no dark-area lift
        _app.ShowsRamp(1, GammaMode.Gamma24);
        window.HasText("MediaSummary", "Same as Normal");
        Assert.Null(window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "DisplayPreserveShadows")));
        Assert.Null(window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "MediaShadowDetail")));
        // Ignored, not erased.
        Assert.Equal(0.5, _app.Settings()["MediaProfile"]!["ShadowDetailStrength"]!.GetValue<double>());
    }

    [Fact]
    public void Media_profile_overrides_apply_while_media_is_on()
    {
        var window = _app.Start()!;
        window.ById("MediaOverrideBrightness").SetChecked(true);
        window.ById("MediaBrightness").SetRange(40);
        window.HasText("MediaSummary", "Brightness 40%");
        _app.ShowsRamp(0, GammaMode.Gamma24); // Normal: Media changes nothing yet

        window.ById("ProfileMedia").Select();

        window.HasText("ProfileDetail", "Media on all displays");
        window.HasText("DisplayMediaNote", "Media is active on this display and sets its brightness.");
        _app.ShowsRamp(0, GammaMode.Gamma24, brightness: 40);
        _app.ShowsRamp(1, GammaMode.Gamma24, brightness: 40);
        Ui.Until(() => _app.Settings()["ProfileSelection"]?.GetValue<string>() == "Media", "Media to be saved");

        window.ById("ProfileNormal").Select();
        _app.ShowsRamp(0, GammaMode.Gamma24);
    }

    [Fact]
    public void Sdr_turns_hdr_off_and_normal_turns_it_back_on()
    {
        var window = _app.Start()!;
        Assert.True(_app.IsHdr(0));

        window.ById("ProfileSdr").Select();

        window.HasText("ProfileDetail", "HDR off, each display's own settings");
        Ui.Until(() => !_app.IsHdr(0), "Sim One to leave HDR");
        _app.ShowsRamp(0, GammaMode.Gamma24, hdr: false);
        Ui.Until(() => _app.Settings()["ProfileSelection"]?.GetValue<string>() == "Sdr", "SDR to be saved");

        window.ById("ProfileNormal").Select();

        Ui.Until(() => _app.IsHdr(0), "Sim One to return to HDR");
        _app.ShowsRamp(0, GammaMode.Gamma24, hdr: true);
        Assert.False(_app.IsHdr(1)); // Sim Two cannot show HDR at all
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
    public void Export_diagnostics_saves_a_bundle_in_the_data_folder_and_shows_it()
    {
        var window = _app.Start()!;

        window.ById("ExportDiagnostics").Invoke();

        string diagnostics = Path.Combine(_app.DataFolder, "Diagnostics");
        Ui.Until(() => Directory.Exists(diagnostics) && Directory.GetFiles(diagnostics, "*.zip").Length == 1,
            "the diagnostics bundle");
        Assert.StartsWith("Diagnostics saved to ", window.ById("Status").Text());
        // Shown through the simulator, not Explorer: an Explorer window would outlive the test.
        string bundle = Directory.GetFiles(diagnostics, "*.zip").Single();
        Ui.Until(() => File.Exists(_app.RevealsFile) && File.ReadAllLines(_app.RevealsFile).Contains(bundle),
            "the bundle to be shown");
    }
}
