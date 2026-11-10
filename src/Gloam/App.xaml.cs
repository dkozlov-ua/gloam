using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using Gloam.Core;
using Gloam.Services;
using Gloam.ViewModels;
using Microsoft.Win32;

namespace Gloam
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "WPF owns the Application's lifetime; OnExit disposes them.")]
    public partial class App : Application
    {
        private GammaApplyService? _applyService;
        private MainViewModel? _viewModel;
        private MainWindow? _window;
        private TrayIcon? _tray;
        private ProfileCommandServer? _profileCommandServer;
        private RegisteredWaitHandle? _showWait;

        /// <summary>
        /// Builds the services every mode shares: settings, the displays (real, or the
        /// <see cref="Simulator"/> the environment names), the apply pipeline, the HDR switch,
        /// and how a saved file is shown (Explorer, or the simulator's record of it).
        /// </summary>
        internal static (SettingsManager Settings, IMonitorSource Monitors, GammaApplyService Apply, HdrModeSwitcher Hdr, Action<string> Reveal) CreateServices()
        {
            var settings = new SettingsManager();
            Simulator? simulator = Simulator.FromEnvironment();
            if (simulator != null) Log.Info($"App: simulated displays from {simulator.MonitorsFile}.");
            IMonitorSource monitors = simulator ?? (IMonitorSource)new MonitorManager();
            var apply = new GammaApplyService(new GammaRampWriter(simulator), settings);
            Action<string> reveal = simulator != null
                ? simulator.Reveal
                : path => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            var hdr = new HdrModeSwitcher(simulator ?? (IHdrSwitch)new NativeHdrSwitch());
            return (settings, monitors, apply, hdr, reveal);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // One-time rebrand migration: move %LocalAppData%\HDRGammaController to Gloam.
            // Must run before Log.Initialize, which would otherwise create the new folder and
            // block the move.
            var migrationMessages = AppPaths.MigrateLegacyData();

            // Enable the file sink first: a WinExe's console output is discarded, so
            // without this no diagnostics from the tray app survive anywhere.
            Log.Initialize();

            try
            {
                Log.Info($"App.OnStartup: Starting {AppProfile.Suffix}...");
                foreach (var message in migrationMessages)
                    Log.Info(message);
                base.OnStartup(e);

                var (settings, monitors, apply, hdr, reveal) = CreateServices();
                _applyService = apply;

                var updater = new UpdateService();
                StartupManager.EnableByDefaultForFreshInstall(settings, updater.IsInstallation);
                // Self-heal a stale HKCU Run value (another path, or an old command line) so
                // logon never starts an old binary. Installations only: a dev run must not
                // steal the registration.
                StartupManager.RepairIfStale(updater.IsInstallation);
                UpdateService.DeleteDownloadedSetups();

                // A `--profile-set` launch that started the app persists its choice before the
                // displays are first applied, so they never show the previous profile first.
                if (Program.StartupProfileSelection is { } startupSelection &&
                    !settings.TrySetProfileSelection(startupSelection))
                    Log.Error($"App.OnStartup: could not save profile selection {startupSelection}.");

                _viewModel = new MainViewModel(
                    settings, monitors, apply, updater, StartupManager.Instance,
                    post: action => Dispatcher.BeginInvoke(action),
                    exit: Shutdown,
                    reveal: reveal,
                    hdr: hdr);
                _window = new MainWindow(_viewModel);
                MainWindow = _window;

                _tray = new TrayIcon(
                    toggleWindow: _window.Toggle,
                    showWindow: _window.ShowFlyout,
                    selectProfile: selection => _viewModel.SelectProfile(selection),
                    exit: Shutdown);
                _viewModel.ProfileChanged += () => _tray.Update(_viewModel.Selection);

                _viewModel.Start();
                _tray.Update(_viewModel.Selection);

                // Windows may reset the gamma ramps and change the displays on a display
                // change or resume; re-apply once the burst settles.
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;

                // `Gloam.exe --profile-set ...` from a second launch arrives here. It is opened
                // only now, after every display has its profile, so a launch that started this
                // tray hears back once the profile is really on screen.
                _profileCommandServer = new ProfileCommandServer(selection => _viewModel.SelectProfile(selection));
                _profileCommandServer.Start();

                // A second plain launch (the Start menu shortcut, say) opens this window.
                if (Program.ShowSignal is { } signal)
                {
                    _showWait = ThreadPool.RegisterWaitForSingleObject(signal,
                        (_, _) => Dispatcher.BeginInvoke(() => _window.ShowFlyout()),
                        null, Timeout.Infinite, executeOnlyOnce: false);
                }

                if (!Program.StartsInBackground)
                    _window.ShowFlyout();

                WarnIfLegacyInstallPresent(settings);
                Log.Info("App.OnStartup: started.");
            }
            catch (Exception ex)
            {
                // Log goes to LocalAppData — the old startup_log.txt in the working
                // directory was unwritable when installed under Program Files.
                Log.Error("CRITICAL STARTUP ERROR: " + ex);
                Shutdown(-1);
            }
        }

        // The pre-Velopack v1.0.0 installer put the app here. If that build still exists it
        // can be resurrected by stale shortcuts/registrations, and its old settings parser
        // destructively resets settings.json on load failure — so warn until it is removed.
        private const string LegacyInstallExePath = @"C:\Program Files\HDR-Gamma-Adjust\Gloam.exe";

        /// <summary>
        /// Detects a lingering legacy install and warns once with a tray notification.
        /// Deletion is NOT attempted: it lives under Program Files and needs elevation.
        /// </summary>
        private void WarnIfLegacyInstallPresent(SettingsManager settings)
        {
            try
            {
                if (!System.IO.File.Exists(LegacyInstallExePath)) return;

                Log.Error($"App: legacy install detected at {LegacyInstallExePath}. " +
                          "It should be removed manually; its old settings parser can corrupt settings.json if launched.");
                if (settings.LegacyInstallWarningShown) return;

                _tray?.Notify("Old Gloam install found",
                    @"An outdated Gloam install remains at C:\Program Files\HDR-Gamma-Adjust. " +
                    "Please uninstall or delete it — running it can corrupt Gloam's settings.");
                settings.MarkLegacyInstallWarningShown();
            }
            catch (Exception ex)
            {
                Log.Info($"App: legacy install check failed: {ex.Message}");
            }
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
            SafeAsync.FireAndForget(() => _viewModel!.HandleDisplayEventAsync(TimeSpan.FromMilliseconds(1500)),
                nameof(OnDisplaySettingsChanged));

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            SafeAsync.FireAndForget(() => _viewModel!.HandleDisplayEventAsync(TimeSpan.FromMilliseconds(3000)),
                nameof(OnPowerModeChanged));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _showWait?.Unregister(null);
            _profileCommandServer?.Dispose();
            _viewModel?.Dispose();
            _applyService?.Dispose();
            _tray?.Dispose();
            _window?.CloseForExit();
            Log.Info("App: exited.");
            base.OnExit(e);
        }

        // Once-per-session guard so a pathological handler that keeps throwing doesn't open a
        // fresh dialog each time the previous one closes (a dialog-spam loop).
        private bool _crashDialogShown;

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("CRITICAL RUNTIME ERROR: " + e.Exception);
            e.Handled = true;
            if (_crashDialogShown) return;
            _crashDialogShown = true;

            try
            {
                MessageBox.Show(
                    $"{e.Exception.Message}\n\nGloam keeps running. The details are in {AppPaths.DataDir}\\app.log.",
                    "Gloam: something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception dialogEx)
            {
                Log.Error($"App: crash dialog failed to show: {dialogEx}");
            }
        }
    }
}
