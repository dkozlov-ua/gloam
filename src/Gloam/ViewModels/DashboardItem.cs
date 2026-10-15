using System;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gloam.Core;

namespace Gloam.ViewModels
{
    public class DashboardItem : ObservableObject
    {
        private MonitorInfo _model = new MonitorInfo();
        private string _friendlyName = "";
        private string _badgeText = "";
        private Brush _badgeColor = Brushes.Gray;
        private GammaMode _currentGamma;
        private double _currentBrightness;
        private string _currentTemperatureText = "";
        private bool _isCalibrating;

        public DashboardItem()
        {
            // The card's quick controls pass their value as a string CommandParameter; the
            // dashboard view model owns the save + apply, so the item only forwards.
            SetGammaCommand = new RelayCommand<string>(value =>
            {
                if (Enum.TryParse(value, out GammaMode mode))
                    QuickGammaRequested?.Invoke(this, mode);
            });
            SetBrightnessCommand = new RelayCommand<string>(value =>
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double brightness))
                    QuickBrightnessRequested?.Invoke(this, brightness);
            });
        }

        public ICommand SetGammaCommand { get; }
        public ICommand SetBrightnessCommand { get; }

        internal Action<DashboardItem, GammaMode>? QuickGammaRequested { get; set; }
        internal Action<DashboardItem, double>? QuickBrightnessRequested { get; set; }

        public MonitorInfo Model
        {
            get => _model;
            set => SetProperty(ref _model, value);
        }

        public string FriendlyName
        {
            get => _friendlyName;
            set => SetProperty(ref _friendlyName, value);
        }

        public string BadgeText
        {
            get => _badgeText;
            set => SetProperty(ref _badgeText, value);
        }

        public Brush BadgeColor
        {
            get => _badgeColor;
            set => SetProperty(ref _badgeColor, value);
        }

        public GammaMode CurrentGamma
        {
            get => _currentGamma;
            set
            {
                if (!SetProperty(ref _currentGamma, value)) return;
                OnPropertyChanged(nameof(IsGamma22));
                OnPropertyChanged(nameof(IsGamma24));
            }
        }

        public bool IsGamma22 => CurrentGamma == GammaMode.Gamma22;
        public bool IsGamma24 => CurrentGamma == GammaMode.Gamma24;

        public double CurrentBrightness
        {
            get => _currentBrightness;
            set
            {
                if (!SetProperty(ref _currentBrightness, value)) return;
                OnPropertyChanged(nameof(IsBrightness100));
                OnPropertyChanged(nameof(IsBrightness75));
                OnPropertyChanged(nameof(IsBrightness50));
            }
        }

        public bool IsBrightness100 => IsBrightness(100);
        public bool IsBrightness75 => IsBrightness(75);
        public bool IsBrightness50 => IsBrightness(50);

        private bool IsBrightness(double preset) => Math.Abs(CurrentBrightness - preset) < 0.5;

        public string CurrentTemperatureText
        {
            get => _currentTemperatureText;
            set => SetProperty(ref _currentTemperatureText, value);
        }

        /// <summary>
        /// A calibration is measuring this display with corrections bypassed; the card
        /// fades its (inactive) settings and shows a badge.
        /// </summary>
        public bool IsCalibrating
        {
            get => _isCalibrating;
            set => SetProperty(ref _isCalibrating, value);
        }

        internal void UpdateFrom(DashboardItem updated)
        {
            Model = updated.Model;
            FriendlyName = updated.FriendlyName;
            BadgeText = updated.BadgeText;
            BadgeColor = updated.BadgeColor;
            CurrentGamma = updated.CurrentGamma;
            CurrentBrightness = updated.CurrentBrightness;
            CurrentTemperatureText = updated.CurrentTemperatureText;
            IsCalibrating = updated.IsCalibrating;
            QuickGammaRequested = updated.QuickGammaRequested;
            QuickBrightnessRequested = updated.QuickBrightnessRequested;
        }
    }
}
