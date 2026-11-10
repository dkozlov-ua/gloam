using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gloam.Core;
using Gloam.Services;

namespace Gloam.ViewModels
{
    /// <summary>
    /// The window's view model and the app's coordinator: it owns the monitor list, applies
    /// what the settings say whenever they change (from the window, the tray menu or
    /// <c>--profile-set</c>), and re-applies after display changes and resume. At startup
    /// and on every profile switch it also puts the displays in the profile's HDR mode.
    /// </summary>
    /// <remarks>
    /// Settings events can arrive on any thread (the <c>--profile-set</c> pipe server raises
    /// them on its own), so every reaction is posted to the UI thread through the
    /// <c>post</c> callback. Tests pass one that runs the action at once. Switching HDR takes
    /// a mode change of a second or two, so it runs through <c>background</c>, off the UI
    /// thread; tests run it at once too.
    /// </remarks>
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private readonly SettingsManager _settings;
        private readonly IMonitorSource _monitorSource;
        private readonly GammaApplyService _applyService;
        private readonly IStartupRegistration _startup;
        private readonly Action<Action> _post;
        private readonly Action<string> _reveal;
        private readonly HdrModeSwitcher? _hdr;
        private readonly Action<Action> _background;
        private int _displayEventSeq;
        private bool _started;

        public MainViewModel(
            SettingsManager settings,
            IMonitorSource monitorSource,
            GammaApplyService applyService,
            IUpdater updater,
            IStartupRegistration startup,
            Action<Action> post,
            Action exit,
            Action<string>? reveal = null,
            HdrModeSwitcher? hdr = null,
            Action<Action>? background = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _monitorSource = monitorSource ?? throw new ArgumentNullException(nameof(monitorSource));
            _applyService = applyService ?? throw new ArgumentNullException(nameof(applyService));
            _startup = startup ?? throw new ArgumentNullException(nameof(startup));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _reveal = reveal ?? (_ => { });
            _hdr = hdr;
            _background = background ?? (action => Task.Run(action));
            ArgumentNullException.ThrowIfNull(exit);

            Media = new MediaProfileViewModel(settings);
            Media.SaveFailed += message => Status = message;
            Update = new UpdateViewModel(updater, exit);
            _startWithWindows = startup.IsEnabled;
            _selection = settings.ProfileSelection;

            ExitCommand = new RelayCommand(exit);
            RefreshCommand = new RelayCommand(RefreshMonitors);
            ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
        }

        public MediaProfileViewModel Media { get; }

        public UpdateViewModel Update { get; }

        public IRelayCommand ExitCommand { get; }

        public IRelayCommand RefreshCommand { get; }

        public IRelayCommand ExportDiagnosticsCommand { get; }

        public ObservableCollection<MonitorViewModel> Monitors { get; } = [];

        /// <summary>Raised on the UI thread after the selected profile changed, from anywhere.</summary>
        public event Action? ProfileChanged;

        private MonitorViewModel? _selectedMonitor;
        public MonitorViewModel? SelectedMonitor
        {
            get => _selectedMonitor;
            set => SetProperty(ref _selectedMonitor, value);
        }

        public bool HasMonitors => Monitors.Count > 0;

        public bool HasSeveralMonitors => Monitors.Count > 1;

        private DisplayProfileSelection _selection;
        public DisplayProfileSelection Selection => _selection;

        public bool IsNormal
        {
            get => _selection == DisplayProfileSelection.Normal;
            set { if (value) SelectProfile(DisplayProfileSelection.Normal); }
        }

        public bool IsMedia
        {
            get => _selection == DisplayProfileSelection.Media;
            set { if (value) SelectProfile(DisplayProfileSelection.Media); }
        }

        public bool IsSdr
        {
            get => _selection == DisplayProfileSelection.Sdr;
            set { if (value) SelectProfile(DisplayProfileSelection.Sdr); }
        }

        /// <summary>The profile section's status line.</summary>
        public string ProfileDetail => _selection switch
        {
            DisplayProfileSelection.Media =>
                Media.AppliesTo?.DevicePath == null ? "Media on all displays" : $"Media on {Media.AppliesTo.Label}",
            DisplayProfileSelection.Sdr => "HDR off, each display's own settings",
            _ => "Each display's own settings",
        };

        private bool _startWithWindows;
        public bool StartWithWindows
        {
            get => _startWithWindows;
            set
            {
                if (_startWithWindows == value) return;
                if (_startup.TrySetEnabled(value))
                {
                    SetProperty(ref _startWithWindows, value);
                }
                else
                {
                    Status = "Gloam could not change Start with Windows.";
                    OnPropertyChanged();
                }
            }
        }

        private string? _status;
        /// <summary>The last problem or result worth telling, such as where diagnostics went.</summary>
        public string? Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                    OnPropertyChanged(nameof(HasStatus));
            }
        }

        public bool HasStatus => !string.IsNullOrEmpty(Status);

        /// <summary>Lists the monitors, applies every one, and follows settings changes from now on.</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            _settings.MonitorProfileChanged += OnMonitorProfileChanged;
            _settings.DisplayProfileChanged += OnDisplayProfileChanged;
            RefreshMonitors();
            SwitchHdrMode();
        }

        /// <summary>
        /// Saves and applies a profile choice. Callable from any thread (the pipe server calls
        /// it); returns false when the choice could not be saved, which leaves the old one.
        /// </summary>
        public bool SelectProfile(DisplayProfileSelection selection)
        {
            if (_settings.TrySetProfileSelection(selection)) return true;
            _post(() =>
            {
                Status = "Gloam could not save the profile choice.";
                SyncSelection();
            });
            return false;
        }

        /// <summary>Enumerates the monitors again and applies every one.</summary>
        public void RefreshMonitors()
        {
            string? selectedPath = SelectedMonitor?.DevicePath;
            List<MonitorInfo> monitors = _monitorSource.EnumerateMonitors();
            Log.Info($"MainViewModel: {monitors.Count} monitor(s).");

            Monitors.Clear();
            int number = 1;
            foreach (MonitorInfo monitor in monitors)
            {
                if (_settings.GetProfileForMonitor(monitor.MonitorDevicePath) is { } saved)
                    monitor.CurrentGamma = saved;
                Monitors.Add(new MonitorViewModel(monitor, number++, _settings));
            }

            SelectedMonitor = Monitors.FirstOrDefault(m =>
                                  string.Equals(m.DevicePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                              ?? Monitors.FirstOrDefault();
            OnPropertyChanged(nameof(HasMonitors));
            OnPropertyChanged(nameof(HasSeveralMonitors));

            Media.SetDisplays(Monitors);
            SyncSelection();
            ApplyAll();
        }

        public void ApplyAll() => _applyService.ApplyAll(Monitors.Select(m => m.Model).ToList());

        /// <summary>
        /// A display change or resume: Windows may have reset the ramps and changed the
        /// displays, so wait for the burst to settle, then enumerate again and re-apply.
        /// Windows fires WM_DISPLAYCHANGE in bursts during HDR mode transitions; only the last
        /// event of a burst acts, because overlapping re-applies are visible as flicker.
        /// </summary>
        public async Task HandleDisplayEventAsync(TimeSpan settle)
        {
            int seq = Interlocked.Increment(ref _displayEventSeq);
            await Task.Delay(settle);
            if (seq != Volatile.Read(ref _displayEventSeq)) return; // a newer event supersedes it

            // Windows may have reset the gamma ramps; don't let the identical-LUT dedupe
            // skip the re-apply.
            _applyService.InvalidateAppliedState();
            _post(RefreshMonitors);
        }

        private void OnMonitorProfileChanged(string devicePath) => _post(() =>
        {
            foreach (MonitorViewModel monitor in Monitors)
            {
                if (string.Equals(monitor.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                    _applyService.RequestApply(monitor.Model);
            }
        });

        private void OnDisplayProfileChanged() => _post(() =>
        {
            DisplayProfileSelection previous = _selection;
            Media.Reload();
            SyncSelection();
            ApplyAll();
            // Only a profile switch changes the HDR mode; editing the Media profile does not.
            if (_selection != previous) SwitchHdrMode();
        });

        /// <summary>
        /// Puts the displays in the selected profile's HDR mode, in the background. Both the
        /// profile and the displays' modes are read when the switch runs, not when it was asked
        /// for, so of two quick switches the later one wins and none acts on a stale mode.
        /// After a change the displays are listed and applied again at once: the LUT depends on
        /// the mode. Windows' own display-change messages follow and re-apply once more, which
        /// the identical-ramp dedupe makes free.
        /// </summary>
        private void SwitchHdrMode()
        {
            if (_hdr == null) return;
            _background(() =>
            {
                try
                {
                    if (_hdr.Apply(_monitorSource, () => _settings.ProfileSelection) == 0) return;
                    _applyService.InvalidateAppliedState();
                    _post(RefreshMonitors);
                }
                catch (Exception ex)
                {
                    Log.Error($"MainViewModel: HDR switch failed: {ex}");
                }
            });
        }

        private void SyncSelection()
        {
            DisplayProfileSelection previous = _selection;
            _selection = _settings.ProfileSelection;
            OnPropertyChanged(nameof(IsNormal));
            OnPropertyChanged(nameof(IsMedia));
            OnPropertyChanged(nameof(IsSdr));
            OnPropertyChanged(nameof(Selection));
            OnPropertyChanged(nameof(ProfileDetail));

            MediaProfile media = _settings.MediaProfile;
            foreach (MonitorViewModel monitor in Monitors)
                monitor.MediaNote = MediaNote(_selection, media, monitor.DevicePath);

            if (previous != _selection)
                ProfileChanged?.Invoke();
        }

        /// <summary>What Media overrides on a display while it is active there; null when nothing.</summary>
        internal static string? MediaNote(DisplayProfileSelection selection, MediaProfile media, string devicePath)
        {
            if (selection != DisplayProfileSelection.Media || !media.AppliesTo(devicePath)) return null;
            MediaProfile profile = media.Sanitized();
            var overridden = new List<string>();
            if (profile.OverrideGamma) overridden.Add("gamma");
            if (profile.OverrideBrightness) overridden.Add("brightness");
            return overridden.Count == 0
                ? "Media is active on this display."
                : $"Media is active on this display and sets its {string.Join(" and ", overridden)}.";
        }

        private void ExportDiagnostics()
        {
            try
            {
                string outputDir = Path.Combine(AppPaths.DataDir, "Diagnostics");
                string path = new DiagnosticsBundle().Create(outputDir, Monitors.Select(m => m.Model).ToList(), _settings);
                Log.Info($"MainViewModel: diagnostics bundle exported to {path}");
                Status = $"Diagnostics saved to {path}";
                _reveal(path);
            }
            catch (Exception ex)
            {
                Log.Error($"MainViewModel.ExportDiagnostics: {ex}");
                Status = $"Could not export diagnostics: {ex.Message}";
            }
        }

        public void Dispose()
        {
            _settings.MonitorProfileChanged -= OnMonitorProfileChanged;
            _settings.DisplayProfileChanged -= OnDisplayProfileChanged;
        }
    }
}
