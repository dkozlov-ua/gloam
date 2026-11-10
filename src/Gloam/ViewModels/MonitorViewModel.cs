using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Gloam.Core;

namespace Gloam.ViewModels
{
    /// <summary>
    /// One monitor's own settings in the window: gamma, brightness and how dimming treats
    /// shadows. Every change is saved at once; the saved change is what re-applies the ramp
    /// (see <see cref="MainViewModel"/>).
    /// </summary>
    public sealed class MonitorViewModel : ObservableObject
    {
        private readonly SettingsManager _settings;
        private bool _loading;

        public MonitorViewModel(MonitorInfo monitor, int number, SettingsManager settings)
        {
            Model = monitor ?? throw new ArgumentNullException(nameof(monitor));
            Number = number;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Reload();
        }

        public MonitorInfo Model { get; }

        public int Number { get; }

        public string DevicePath => Model.MonitorDevicePath;

        public string Title => string.IsNullOrWhiteSpace(Model.FriendlyName)
            ? $"Display {Number}"
            : $"{Number}. {Model.FriendlyName}";

        public bool IsHdr => Model.IsHdrActive;

        // The display picker's items are named by it, for screen readers and UI Automation.
        public override string ToString() => Title;

        /// <summary>The signal the display gets: HDR with its SDR white level, or SDR.</summary>
        public string Signal => Model.IsHdrActive
            ? $"HDR · SDR white {MonitorInfo.SanitizeSdrWhiteLevel(Model.SdrWhiteLevel):F0} nits"
            : "SDR";

        private GammaMode _gamma;
        public GammaMode Gamma
        {
            get => _gamma;
            set
            {
                if (!SetProperty(ref _gamma, value)) return;
                OnPropertyChanged(nameof(IsGamma22));
                OnPropertyChanged(nameof(IsGamma24));
                Save(profile => profile.GammaMode = value);
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

        private double _brightness = 100.0;
        public double Brightness
        {
            get => _brightness;
            set
            {
                value = Math.Round(Math.Clamp(double.IsFinite(value) ? value : 100.0, 10.0, 100.0));
                if (!SetProperty(ref _brightness, value)) return;
                OnPropertyChanged(nameof(BrightnessText));
                Save(profile => profile.Brightness = value);
            }
        }

        public string BrightnessText => $"{Brightness:F0}%";

        private bool _preserveShadows = true;
        /// <summary>Dim with the shadow-preserving curve rather than a straight multiply.</summary>
        public bool PreserveShadows
        {
            get => _preserveShadows;
            set
            {
                if (!SetProperty(ref _preserveShadows, value)) return;
                Save(profile => profile.UseLinearBrightness = !value);
            }
        }

        private string? _mediaNote;
        /// <summary>What the active Media profile overrides on this display; null when nothing.</summary>
        public string? MediaNote
        {
            get => _mediaNote;
            set
            {
                if (SetProperty(ref _mediaNote, value))
                    OnPropertyChanged(nameof(HasMediaNote));
            }
        }

        public bool HasMediaNote => !string.IsNullOrEmpty(MediaNote);

        /// <summary>Reads the saved settings again, without saving anything.</summary>
        public void Reload()
        {
            MonitorProfileData profile = _settings.GetMonitorProfile(DevicePath)
                ?? new MonitorProfileData { GammaMode = Model.CurrentGamma };
            _loading = true;
            try
            {
                Gamma = profile.GammaMode;
                Brightness = profile.Brightness;
                PreserveShadows = !profile.UseLinearBrightness;
            }
            finally
            {
                _loading = false;
            }
        }

        private void Save(Action<MonitorProfileData> edit)
        {
            if (_loading || string.IsNullOrEmpty(DevicePath)) return;
            MonitorProfileData profile = _settings.GetMonitorProfile(DevicePath)
                ?? new MonitorProfileData { GammaMode = Model.CurrentGamma };
            edit(profile);
            _settings.SetMonitorProfile(DevicePath, profile);
        }
    }
}
