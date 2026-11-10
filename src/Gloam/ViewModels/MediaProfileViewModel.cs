using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Gloam.Core;

namespace Gloam.ViewModels
{
    /// <summary>A choice in "Applies to": every display, or one of them.</summary>
    public sealed record DisplayChoice(string Label, string? DevicePath)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// The Media profile's editor. Every change is saved at once; the settings event that
    /// follows re-applies the displays when Media is the selected profile.
    /// </summary>
    public sealed class MediaProfileViewModel : ObservableObject
    {
        public const string AllDisplaysLabel = "All displays";

        private readonly SettingsManager _settings;
        private bool _loading;

        public MediaProfileViewModel(SettingsManager settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Reload();
        }

        /// <summary>Raised when a change could not be saved; the editor shows the saved values again.</summary>
        public event Action<string>? SaveFailed;

        private bool _overrideGamma;
        public bool OverrideGamma
        {
            get => _overrideGamma;
            set { if (SetProperty(ref _overrideGamma, value)) Changed(p => p.OverrideGamma = value); }
        }

        private GammaMode _gamma = GammaMode.Gamma22;
        public GammaMode Gamma
        {
            get => _gamma;
            set
            {
                if (!SetProperty(ref _gamma, value)) return;
                OnPropertyChanged(nameof(IsGamma22));
                OnPropertyChanged(nameof(IsGamma24));
                Changed(p => p.GammaMode = value);
            }
        }

        public bool IsGamma22
        {
            get => Gamma == GammaMode.Gamma22;
            set { if (value) Gamma = GammaMode.Gamma22; }
        }

        public bool IsGamma24
        {
            get => Gamma == GammaMode.Gamma24;
            set { if (value) Gamma = GammaMode.Gamma24; }
        }

        private bool _overrideBrightness;
        public bool OverrideBrightness
        {
            get => _overrideBrightness;
            set { if (SetProperty(ref _overrideBrightness, value)) Changed(p => p.OverrideBrightness = value); }
        }

        private double _brightness = 100.0;
        public double Brightness
        {
            get => _brightness;
            set
            {
                value = Math.Round(Math.Clamp(double.IsFinite(value) ? value : 100.0, 10.0, 100.0));
                if (!SetProperty(ref _brightness, value)) return;
                OnPropertyChanged(nameof(BrightnessText));
                Changed(p => p.Brightness = value);
            }
        }

        public string BrightnessText => $"{Brightness:F0}%";

        public ObservableCollection<DisplayChoice> DisplayChoices { get; } = [new DisplayChoice(AllDisplaysLabel, null)];

        private DisplayChoice? _appliesTo;
        public DisplayChoice? AppliesTo
        {
            get => _appliesTo;
            set
            {
                if (value == null || !SetProperty(ref _appliesTo, value)) return;
                Changed(p =>
                {
                    p.DisplayScope = value.DevicePath == null ? MediaDisplayScope.AllDisplays : MediaDisplayScope.SpecificDisplay;
                    p.MonitorDevicePath = value.DevicePath;
                });
            }
        }

        /// <summary>What Media changes, in one line, for the section header.</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (OverrideGamma) parts.Add($"gamma {(Gamma == GammaMode.Gamma24 ? "2.4" : "2.2")}");
                if (OverrideBrightness) parts.Add($"brightness {Brightness:F0}%");
                if (parts.Count == 0) return "Same as Normal";
                string text = string.Join(", ", parts);
                return char.ToUpperInvariant(text[0]) + text[1..];
            }
        }

        /// <summary>Lists the displays for "Applies to", keeping a chosen display that is disconnected.</summary>
        public void SetDisplays(IEnumerable<MonitorViewModel> monitors)
        {
            _loading = true;
            try
            {
                // The list is rebuilt, so the choice is chosen again even when it is equal to
                // the old one (a record): otherwise no change is raised and the picker, which
                // lost its selection with the old items, shows nothing.
                _appliesTo = null;
                DisplayChoices.Clear();
                DisplayChoices.Add(new DisplayChoice(AllDisplaysLabel, null));
                foreach (MonitorViewModel monitor in monitors)
                {
                    if (!string.IsNullOrEmpty(monitor.DevicePath))
                        DisplayChoices.Add(new DisplayChoice(monitor.Title, monitor.DevicePath));
                }
            }
            finally
            {
                _loading = false;
            }
            Reload();
        }

        /// <summary>Reads the saved Media profile again, without saving anything.</summary>
        public void Reload()
        {
            MediaProfile profile = _settings.MediaProfile.Sanitized();
            _loading = true;
            try
            {
                OverrideGamma = profile.OverrideGamma;
                Gamma = profile.GammaMode == GammaMode.Gamma24 ? GammaMode.Gamma24 : GammaMode.Gamma22;
                OverrideBrightness = profile.OverrideBrightness;
                Brightness = profile.Brightness;

                DisplayChoice choice;
                if (profile.DisplayScope == MediaDisplayScope.SpecificDisplay)
                {
                    choice = DisplayChoices.FirstOrDefault(c =>
                                 string.Equals(c.DevicePath, profile.MonitorDevicePath, StringComparison.OrdinalIgnoreCase))
                             ?? AddDisconnected(profile.MonitorDevicePath!);
                }
                else
                {
                    choice = DisplayChoices[0];
                }
                AppliesTo = choice;
            }
            finally
            {
                _loading = false;
            }
            OnPropertyChanged(nameof(Summary));
        }

        private DisplayChoice AddDisconnected(string devicePath)
        {
            var choice = new DisplayChoice("A disconnected display", devicePath);
            DisplayChoices.Add(choice);
            return choice;
        }

        private void Changed(Action<MediaProfile> edit)
        {
            OnPropertyChanged(nameof(Summary));
            if (_loading) return;

            MediaProfile profile = _settings.MediaProfile;
            edit(profile);
            if (!_settings.TrySetMediaProfile(profile))
            {
                Reload();
                SaveFailed?.Invoke("Gloam could not save the Media profile.");
            }
        }
    }
}
