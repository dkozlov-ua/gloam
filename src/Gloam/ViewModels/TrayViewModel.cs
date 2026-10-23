using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gloam.Core;
using Gloam.Core.Calibration;
using Gloam.Services;
using Gloam.Interop;

namespace Gloam.ViewModels
{
    public class TrayViewModel : ObservableObject, IDisposable
    {
        public event Action<string, string>? NotificationRequested;

        private readonly MonitorManager _monitorManager;
        private readonly AdvancedColorProfileMigrationService? _profileModeService;
        private readonly ProfileManager _profileManager;
        private readonly DispwinRunner _dispwinRunner;
        private readonly SettingsManager _settingsManager;
        private readonly NightModeService _nightModeService;
        private readonly GammaApplyService _applyService;
        private readonly UpdateService _updateService;
        private readonly IToastService? _toastService;

        private readonly AppDetectionService _appDetectionService;
        private readonly MelanopicMonitorService? _melanopicService;
        // Guarded by _activeMonitorsLock: RefreshMonitors (UI thread) mutates this while
        // OnForegroundAppChanged can enumerate it from the foreground-app hook thread.
        private List<MonitorInfo> _activeMonitors = new List<MonitorInfo>();
        private readonly object _activeMonitorsLock = new();
        private readonly GamerModeCoordinator _gamerModeCoordinator;
        private readonly ProfileCommandServer _profileCommandServer;
        private readonly CalibrationLaunchCoordinator _calibrationLaunchCoordinator;

        private Dictionary<string, MonitorInfo> _savedConfigs = new Dictionary<string, MonitorInfo>();

        public ObservableCollection<object> TrayItems { get; } = new ObservableCollection<object>();

        public ICommand ExitCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand StartupCommand { get; }
        public ICommand DashboardCommand { get; }
        public ICommand CalibrateCommand { get; }
        public ICommand TrustCheckCommand { get; }
        public ICommand MatchDisplaysCommand { get; }
        public ICommand ExportDiagnosticsCommand { get; }
        public ICommand CheckForUpdatesCommand { get; }
        public ICommand ToggleGamerModeCommand { get; }
        public string AppVersion => _updateService.DisplayVersion;
        public string TrayToolTipText => $"Gloam {AppVersion}";

        public TrayViewModel(
            MonitorManager monitorManager,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            ProfileManager profileManager,
            DispwinRunner dispwinRunner,
            GammaApplyService applyService,
            AppDetectionService appDetectionService,
            UpdateService updateService,
            IToastService? toastService = null,
            MelanopicMonitorService? melanopicService = null,
            AdvancedColorProfileMigrationService? profileModeService = null)
        {
            // Dependencies are constructed by the DI container (App.ConfigureServices)
            // and must all be assigned before starting any service: NightModeService and
            // AppDetectionService both fire their events synchronously from Start(),
            // and those handlers reach the apply service. The wiring/start order in
            // this constructor body is load-bearing; do not reorder it.
            _monitorManager = monitorManager;
            _profileModeService = profileModeService;
            _settingsManager = settingsManager;
            _nightModeService = nightModeService;
            _profileManager = profileManager;
            _dispwinRunner = dispwinRunner;
            _applyService = applyService;
            _appDetectionService = appDetectionService;
            _updateService = updateService;
            _toastService = toastService;
            _melanopicService = melanopicService;
            _gamerModeCoordinator = new GamerModeCoordinator(
                _settingsManager,
                _applyService,
                () =>
                {
                    lock (_activeMonitorsLock) return _activeMonitors.ToList();
                });
            _gamerModeCoordinator.PolicyChanged += OnGamerPolicyChanged;
            _calibrationLaunchCoordinator = new CalibrationLaunchCoordinator(
                _settingsManager,
                _dispwinRunner,
                _nightModeService,
                () =>
                {
                    lock (_activeMonitorsLock) return _activeMonitors.ToList();
                },
                RefreshMonitors,
                () =>
                {
                    _applyService.InvalidateAppliedState();
                    ApplyAll();
                });

            _dispwinRunner.DispwinUnavailable += () =>
                OnUiThread(() =>
                    NotificationRequested?.Invoke("ArgyllCMS Not Found",
                        "Native gamma apply failed and dispwin.exe is unavailable."));

            _nightModeService.BlendChanged += (blend) =>
            {
                // The hardware apply is thread-agnostic, but ApplyAll reads TrayItems,
                // which belongs to the UI thread.
                OnUiThread(() => ApplyAll());
            };
            _settingsManager.NightModeChanged += (newSettings) => _nightModeService.UpdateSettings(newSettings);
            _settingsManager.GamerSettingsChanged += OnGamerSettingsChanged;

            _appDetectionService.ForegroundAppChanged += _gamerModeCoordinator.ObserveForeground;

            ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
            RefreshCommand = new RelayCommand(RefreshMonitors);
            StartupCommand = new RelayCommand(ToggleStartup);
            DashboardCommand = new RelayCommand(OpenDashboard);
            CalibrateCommand = new RelayCommand(OpenCalibration);
            TrustCheckCommand = new RelayCommand(OpenTrustCheck);
            MatchDisplaysCommand = new RelayCommand(OpenMatchDisplays);
            ExportDiagnosticsCommand = new RelayCommand(ExportDiagnostics);
            CheckForUpdatesCommand = new RelayCommand(CheckForUpdates);
            ToggleGamerModeCommand = new RelayCommand(ToggleGamerMode);

            RefreshMonitors();

            _nightModeService.Start();
            _appDetectionService.Start();

            // Apply saved profiles on startup
            ApplyAll();

            // `Gloam.exe --profile-set ...` from a second launch arrives here. The coordinator
            // persists the choice and re-evaluates on its own settings-changed event.
            _profileCommandServer = new ProfileCommandServer(
                selection => _gamerModeCoordinator.TrySetProfileSelection(selection));
            _profileCommandServer.Start();

            // The trust-check reminder (CheckTrustCheckReminder) is not scheduled while the
            // calibration, trust check and display matching UI is hidden: its toast would
            // point at a hidden tool.
        }

        private void CheckTrustCheckReminder()
        {
            try
            {
                int intervalDays = _settingsManager.TrustCheckReminderDays;
                if (intervalDays <= 0) return;

                // Last-run time is derived from the trend store itself (the source of
                // truth) across all calibrated monitors.
                DateTime? lastCheckUtc = null;
                List<MonitorInfo> monitors;
                lock (_activeMonitorsLock) { monitors = _activeMonitors.ToList(); }
                bool anyCalibrated = false;
                foreach (var monitor in monitors)
                {
                    if (string.IsNullOrEmpty(monitor.MonitorDevicePath)) continue;
                    if (!_settingsManager.HasMhc2Calibration(monitor.MonitorDevicePath)) continue;
                    anyCalibrated = true;
                    var history = TrustCheckHistory.Load(monitor.MonitorDevicePath);
                    if (history.Count > 0)
                    {
                        var newest = history[^1].TimestampUtc;
                        if (lastCheckUtc == null || newest > lastCheckUtc) lastCheckUtc = newest;
                    }
                }

                if (!anyCalibrated) return;
                if (!TrustCheckHistory.ShouldRemind(lastCheckUtc, DateTime.UtcNow, intervalDays)) return;

                _toastService?.Show(
                    "Time for a trust check",
                    lastCheckUtc is { } last
                        ? $"The last 20-second display check was {(DateTime.UtcNow - last).TotalDays:F0} days ago."
                        : "Run a 20-second display check to start tracking calibration drift.",
                    ToastKind.Info, "Run check", OpenTrustCheck);
            }
            catch (Exception ex)
            {
                Log.Info($"TrayViewModel: trust-check reminder failed: {ex.Message}");
            }
        }

        private void OpenTrustCheck()
        {
            // Only one probe exists: refuse while a calibration flow or another trust
            // check is running (same guard as OpenCalibration).
            foreach (Window window in Application.Current.Windows)
            {
                bool inUse = window is CalibrationSetupWindow or CalibrationWindow or TrustCheckWindow
                    || (window is CalibrationReportWindow report && report.IsVerifyRunning);
                if (!inUse) continue;

                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
                return;
            }

            var trustCheckWindow = new TrustCheckWindow(
                _monitorManager, _settingsManager, _dispwinRunner, _nightModeService, _toastService);
            trustCheckWindow.Show();
        }

        private void OpenMatchDisplays()
        {
            var matchWindow = new DisplayMatchWindow(_monitorManager, _settingsManager, () => OnUiThread(ApplyAll));
            matchWindow.Show();
        }

        // Updates are checked on a click only (see UpdateService). One check-and-install
        // flow at a time: a second click while the first is still talking to GitHub or
        // downloading is ignored.
        private bool _updateInProgress;

        // Release notes are shown under the offer as plain text; a long body is cut so the
        // dialog stays on screen. The full notes are on the release page.
        private const int MaxNotesLength = 1500;

        private void CheckForUpdates()
            => SafeAsync.FireAndForget(CheckForUpdatesAsync, nameof(CheckForUpdates));

        private async Task CheckForUpdatesAsync()
        {
            if (_updateInProgress) return;
            _updateInProgress = true;
            try
            {
                UpdateCheckResult check = await _updateService.CheckAsync();
                switch (check.Status)
                {
                    case UpdateCheckStatus.Failed:
                        ShowNotification("Update check failed", check.Message ?? "Unknown error.", ToastKind.Warning);
                        return;
                    case UpdateCheckStatus.UpToDate:
                        ShowNotification("Gloam is up to date",
                            $"{AppVersion} is the latest release.", ToastKind.Success);
                        return;
                }

                UpdateRelease release = check.Release!;
                Window? owner = DialogOwner();
                if (owner == null) return;

                string version = release.Version.ToString(3);
                if (!_updateService.IsInstallation)
                {
                    ConfirmDialog.Info(owner, $"Gloam {version} is available",
                        $"This copy of Gloam ({AppVersion}) is not an installation, so it cannot update itself. " +
                        $"Install the release from {UpdateService.ReleasesPageUrl}.");
                    return;
                }

                string notes = release.Notes.Length > MaxNotesLength
                    ? release.Notes[..MaxNotesLength].TrimEnd() + "\n\n..."
                    : release.Notes;
                string message = (notes.Length > 0 ? notes + "\n\n" : "") +
                    $"Install closes Gloam, replaces it with {version} and starts it again. " +
                    $"Settings and calibration data are kept.";
                if (!ConfirmDialog.Confirm(owner, $"Gloam {version} is available", message,
                        confirmLabel: "Install", cancelLabel: "Later"))
                    return;

                string package;
                var busy = BusyDialog.Open(owner, $"Downloading Gloam {version}...");
                try
                {
                    package = await _updateService.DownloadAsync(release);
                }
                finally
                {
                    busy.Dismiss();
                }

                _updateService.StartInstaller(package, release.Sha256);
                Log.Info($"TrayViewModel: installing {release.Tag}; exiting for the installer.");
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error($"TrayViewModel.CheckForUpdates: {ex}");
                ShowNotification("Update failed", ex.Message, ToastKind.Error);
            }
            finally
            {
                _updateInProgress = false;
            }
        }

        // Dialogs from the tray go over whatever Gloam window is active, else the hidden
        // tray host.
        private static Window? DialogOwner()
            => Application.Current?.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive && w.IsVisible)
                ?? Application.Current?.MainWindow;

        private bool ShowNotification(string title, string message, ToastKind kind)
        {
            if (_toastService != null)
            {
                _toastService.Show(title, message, kind);
                return true;
            }

            NotificationRequested?.Invoke(title, message);
            return true;
        }

        /// <summary>
        /// Marshals an action onto the UI thread WITHOUT blocking the caller, and no-ops if the
        /// dispatcher is gone or shutting down. The night-mode timer and the foreground-window
        /// hook fire on background threads. A synchronous Dispatcher.Invoke from there stalls the
        /// background thread whenever the UI thread is busy or in a modal loop (e.g. a window
        /// DragMove, whose OS-driven move loop does not pump the dispatcher), and an Invoke after
        /// shutdown throws on the background thread, tearing down the process. BeginInvoke avoids
        /// both: it queues and returns immediately, so the apply runs when the UI thread is free.
        /// </summary>
        private static void OnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return;
            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch (Exception ex)
            {
                // Most likely the dispatcher began shutting down between the check and the
                // BeginInvoke. Log and move on rather than letting it escape the timer/hook thread.
                Log.Error($"TrayViewModel.OnUiThread: {ex.Message}");
            }
        }

        // Debounce sequence for display-change/resume events. Windows fires
        // WM_DISPLAYCHANGE in bursts during HDR mode transitions; without debouncing,
        // each burst event queued its own RefreshMonitors+ApplyAll, and the overlapping
        // re-applies were visible as flicker.
        private int _displayEventSeq;

        public void HandleDisplayChange()
            => SafeAsync.FireAndForget(() => HandleDisplayEventAsync(1500), nameof(HandleDisplayChange));

        public void HandleResume()
            => SafeAsync.FireAndForget(() => HandleDisplayEventAsync(3000), nameof(HandleResume));

        private async Task HandleDisplayEventAsync(int settleDelayMs)
        {
            int seq = Interlocked.Increment(ref _displayEventSeq);
            await Task.Delay(settleDelayMs);
            if (seq != Volatile.Read(ref _displayEventSeq)) return; // superseded by a newer event

            try
            {
                // Windows may have reset the gamma ramps; don't let the identical-LUT
                // dedupe skip the re-apply.
                _applyService.InvalidateAppliedState();
                RefreshMonitors();
                // Must precede ApplyAll: the apply path reads the active MHC2 profile.
                _profileModeService?.ReconcileModeChanges();
                // HMONITOR handles and output facts can change across HDR/display events.
                // Re-run foreground assignment so Gameplay Lock follows the refreshed path.
                _appDetectionService.Refresh();
                // Re-evaluate the night-mode schedule for 'now' BEFORE re-applying: after a
                // suspend/resume (or a display-change burst that straddled a trigger) the
                // service's cached kelvin can be hours stale, and ApplyAll would re-assert
                // that stale warmth until the next timer tick. Feeding the persisted
                // settings back in forces an immediate state update + timer reschedule.
                _nightModeService.UpdateSettings(_settingsManager.NightMode);
                ApplyAll();
            }
            catch (Exception ex)
            {
                Log.Error($"TrayViewModel.HandleDisplayEvent: {ex.Message}");
            }
        }


        private void OnGamerPolicyChanged(GamerPolicyChange change)
        {
            GamerSignalDiagnostic? criticalDiagnostic = null;
            GamerSessionSnapshot? criticalSession = null;
            foreach (GamerSessionSnapshot session in change.ActiveSessions)
            {
                GamerSignalDiagnostic? diagnostic = session.Diagnostics.FirstOrDefault(item =>
                    item.Severity == GamerDiagnosticSeverity.Critical);
                if (diagnostic != null)
                {
                    criticalSession = session;
                    criticalDiagnostic = diagnostic;
                    break;
                }
            }

            OnUiThread(() =>
            {
                ApplyAll();
                if (change.GamerSessionChanged && criticalDiagnostic != null && criticalSession != null)
                {
                    _toastService?.Show(
                        $"{criticalSession.DisplayName}: signal check",
                        criticalDiagnostic.Message,
                        ToastKind.Warning);
                }
            });
        }

        private void OnGamerSettingsChanged()
        {
            OnUiThread(() =>
            {
                UpdateGameModeItem();

                if (_settingsManager.GamerModeEnabled)
                    _appDetectionService.Refresh();
            });
        }

        private string GamerModeLabel() => _gamerModeCoordinator.Enabled
            ? "Pause Game Mode"
            : "Resume Game Mode";

        // Pausing from the tray is hidden; the item only appears as "Resume Game Mode"
        // so a Game Mode that was paused earlier (saved in settings) can come back.
        private void UpdateGameModeItem()
        {
            if (_gameModeItem == null) return;
            _gameModeItem.Header = GamerModeLabel();
            _gameModeItem.IsShown = !_gamerModeCoordinator.Enabled;
        }

        private void ToggleGamerMode()
        {
            bool enabled = !_gamerModeCoordinator.Enabled;
            if (!_gamerModeCoordinator.TrySetEnabled(enabled))
            {
                _toastService?.Show(
                    "Game Mode was not changed",
                    "Gloam could not save settings. Your previous Game Mode state is still active.",
                    ToastKind.Error);
                return;
            }
            _toastService?.Show(
                "Game Mode",
                enabled ? "Library games switch to the Media profile again." : "Automatic switching to Media is paused.",
                enabled ? ToastKind.Success : ToastKind.Info);
        }

        private void CancelPendingForegroundPolicy()
            => _gamerModeCoordinator.CancelPendingForeground();

        public void RequestApply(
            MonitorInfo monitor,
            GammaMode mode,
            CalibrationSettings? manualCalibration = null,
            int? nightKelvinOverride = null,
            NightModeSettings? nightModeSettingsOverride = null)
            => _applyService.RequestApply(monitor, mode, manualCalibration, nightKelvinOverride, nightModeSettingsOverride);

        private void ApplyAll()
        {
            EnsureWindowsNightLightDisabled();
            _applyService.ApplyAll(TrayItems.OfType<MonitorViewModel>().Select(vm => vm.Model));
        }

        private DateTime _nightLightGuardLastCheck = DateTime.MinValue;
        private bool _nightLightToastShown;

        /// <summary>
        /// Windows Night Light layers its own warm shift through the same pipeline Gloam
        /// owns, corrupting calibrated output and night-mode colorimetry alike — whether
        /// or not Gloam's night mode is on. While Gloam manages displays it therefore
        /// keeps Night Light off (opt out via "AllowWindowsNightLight": true in
        /// settings.json). Hooked into the apply path so a scheduled evening Night Light
        /// activation is caught within a minute; the check itself is one registry read,
        /// throttled to once per 60s.
        /// </summary>
        private void EnsureWindowsNightLightDisabled()
        {
            try
            {
                if (_settingsManager.AllowWindowsNightLight) return;

                var now = DateTime.UtcNow;
                if (now - _nightLightGuardLastCheck < TimeSpan.FromSeconds(60)) return;
                _nightLightGuardLastCheck = now;

                if (WindowsNightLight.Detect() != true) return;
                if (!WindowsNightLight.TryDisable()) return;

                Log.Info("TrayViewModel: Windows Night Light was active; disabled so it cannot stack on Gloam's output.");
                if (!_nightLightToastShown)
                {
                    _nightLightToastShown = true;
                    _toastService?.Show("Windows Night Light disabled",
                        "Night Light layers a second colour shift on top of Gloam's calibrated output, " +
                        "so Gloam has turned it off and manages display colour instead.",
                        ToastKind.Info);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"TrayViewModel: Night Light guard failed: {ex.Message}");
            }
        }

        private ActionViewModel? _startupItem;
        private ActionViewModel? _gameModeItem;

        private static string StartupLabel()
            => StartupManager.IsStartupEnabled ? "✓ Start with Windows" : "Start with Windows";

        private void ToggleStartup()
        {
            StartupManager.IsStartupEnabled = !StartupManager.IsStartupEnabled;
            // Update the checkmark in place; rebuilding the menu would tear the
            // items out from under the still-open tray menu.
            if (_startupItem != null) _startupItem.Header = StartupLabel();
        }

        private void OnMonitorProfileChanged(MonitorInfo monitor, GammaMode mode)
        {
            // Persist to settings
            if (!string.IsNullOrEmpty(monitor.MonitorDevicePath))
            {
                _settingsManager.SetProfileForMonitor(monitor.MonitorDevicePath, mode);
            }
        }

        private void OpenDashboard()
        {
            var dashboard = Application.Current.Windows.OfType<DashboardWindow>()
                .FirstOrDefault();
            if (dashboard == null)
            {
                dashboard = new DashboardWindow(
                    _monitorManager,
                    _settingsManager,
                    _nightModeService,
                    _updateService,
                    RequestApply,
                    _melanopicService,
                    _applyService,
                    _gamerModeCoordinator);
                dashboard.Show();
            }
            else
            {
                if (dashboard.WindowState == WindowState.Minimized)
                    dashboard.WindowState = WindowState.Normal;
                dashboard.Show();
            }

            dashboard.Activate();
        }

        private void OpenCalibration()
            => _calibrationLaunchCoordinator.Open();

        private void ExportDiagnostics()
        {
            try
            {
                List<MonitorInfo> activeMonitors;
                lock (_activeMonitorsLock) { activeMonitors = _activeMonitors.ToList(); }

                bool includeReports = false;
                var owner = DialogOwner();
                if (owner != null)
                {
                    includeReports = ConfirmDialog.Confirm(owner,
                        "Diagnostics Export",
                        "Include saved calibration report snapshots and detailed verification CSVs in the support bundle?\n\n" +
                        "Choose Summary Only for the smallest bundle.",
                        confirmLabel: "Include Reports",
                        cancelLabel: "Summary Only");
                }

                string outputDir = Path.Combine(AppPaths.DataDir, "Diagnostics");
                string path = new DiagnosticsBundle().Create(outputDir, activeMonitors, _settingsManager, includeReports);
                Log.Info($"TrayViewModel: diagnostics bundle exported to {path}");

                string message = $"Saved to {path}";
                if (_toastService != null)
                    _toastService.Show("Diagnostics exported", message, ToastKind.Success);
                else
                    NotificationRequested?.Invoke("Diagnostics exported", message);
            }
            catch (Exception ex)
            {
                Log.Error($"TrayViewModel.ExportDiagnostics: {ex}");
                _toastService?.Show("Diagnostics failed", ex.Message, ToastKind.Error);
                NotificationRequested?.Invoke("Diagnostics failed", ex.Message);
            }
        }

        public void Dispose()
        {
            // Stops the night-mode and ramp-guard timers and unhooks the
            // foreground-window event hook.
            _profileCommandServer.Dispose();
            CancelPendingForegroundPolicy();
            _settingsManager.GamerSettingsChanged -= OnGamerSettingsChanged;
            _appDetectionService.ForegroundAppChanged -= _gamerModeCoordinator.ObserveForeground;
            _gamerModeCoordinator.PolicyChanged -= OnGamerPolicyChanged;
            _gamerModeCoordinator.Dispose();
            _applyService.Dispose();
            _nightModeService.Dispose();
            _appDetectionService.Dispose();
            GC.SuppressFinalize(this);
        }

        public void RefreshMonitors()
        {
            Log.Info("TrayViewModel: Refreshing monitors...");
            TrayItems.Clear();
            var enumerated = _monitorManager.EnumerateMonitors();
            // Publish the new list atomically: OnForegroundAppChanged may snapshot it from the
            // foreground-app hook thread at any moment. A new list reference is published in
            // one write, so readers never see a half-built list.
            lock (_activeMonitorsLock)
            {
                _activeMonitors = enumerated;
            }
            var monitors = enumerated;
            Log.Info($"TrayViewModel: Enumerated {monitors.Count} monitors.");
            TrayItems.Add(new ActionViewModel(TrayToolTipText, null, staysOpenOnClick: true));

            if (monitors.Count == 0)
            {
                TrayItems.Add(new ActionViewModel("No Monitors Found", RefreshCommand));
            }
            else
            {
                TrayItems.Add(new ActionViewModel("Open Dashboard...", DashboardCommand));
                // Calibrate Display, Trust Check and Match Displays (CalibrateCommand,
                // TrustCheckCommand, MatchDisplaysCommand) are hidden from the menu.
                _gameModeItem = new ActionViewModel(GamerModeLabel(), ToggleGamerModeCommand, staysOpenOnClick: true);
                UpdateGameModeItem();
                TrayItems.Add(_gameModeItem);
                TrayItems.Add(new ActionViewModel("───────────", null));

                int index = 1;
                foreach (var m in monitors)
                {
                    // Restore persistent state from settings file
                    var savedMode = _settingsManager.GetProfileForMonitor(m.MonitorDevicePath);
                    if (savedMode.HasValue)
                    {
                        m.CurrentGamma = savedMode.Value;
                    }
                    else if (!string.IsNullOrEmpty(m.MonitorDevicePath) &&
                        _savedConfigs.TryGetValue(m.MonitorDevicePath, out var saved))
                    {
                        // Fallback to in-memory cache
                        m.CurrentGamma = saved.CurrentGamma;
                        m.SdrWhiteLevel = saved.SdrWhiteLevel;
                    }

                    // Update saved mapping
                    if (!string.IsNullOrEmpty(m.MonitorDevicePath))
                    {
                        _savedConfigs[m.MonitorDevicePath] = m;
                    }

                    var vm = new MonitorViewModel(m, _profileManager, _dispwinRunner, index, _settingsManager);
                    // Point MonitorViewModel to use our centralized RequestApply which handles Night Mode
                    vm.OnApplyWithCalibration = (monitor, mode, calibration, nightKelvinOverride) =>
                        RequestApply(monitor, mode, calibration, nightKelvinOverride);
                    vm.GetAllMonitors = () => monitors;
                    TrayItems.Add(vm);
                    index++;
                }
            }

            // Startup toggle with checkmark
            _startupItem = new ActionViewModel(StartupLabel(), StartupCommand, staysOpenOnClick: true);
            TrayItems.Add(_startupItem);
            TrayItems.Add(new ActionViewModel("Check for Updates", CheckForUpdatesCommand));
            TrayItems.Add(new ActionViewModel("Export Diagnostics", ExportDiagnosticsCommand));
            TrayItems.Add(new ActionViewModel("Refresh", RefreshCommand, staysOpenOnClick: true));
            TrayItems.Add(new ActionViewModel("Exit", ExitCommand));
        }
    }
}
