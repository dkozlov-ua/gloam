using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Gloam.Core;
using Gloam.Services;

namespace Gloam
{
    /// <summary>
    /// Process entry point. The smoke test, the <c>--profile-set</c> forwarding and the
    /// single-instance guard all decide before WPF starts whether there is an app to run at
    /// all, so the entry point is here (wired via &lt;StartupObject&gt; in the csproj) rather
    /// than the Main that App.xaml would otherwise generate.
    /// </summary>
    public static class Program
    {
        // Held for the whole process lifetime so a second instance cannot start. Kept in a
        // static field so the GC does not collect it (and release the mutex) while we run.
        private static Mutex? _singleInstanceMutex;

        // installer\Gloam.iss waits on this name for Gloam to exit before it replaces the
        // files: InstallerScriptTests pins the two. A named AppProfile appends its suffix.
        internal const string SingleInstanceMutexName = "Local\\Gloam-SingleInstance";

        // Set by a second plain launch: the running tray opens its window.
        private static string ShowSignalName => $"Local\\Gloam{AppProfile.Suffix}-Show";

        /// <summary>The running tray's "open the window" signal; null if it could not be made.</summary>
        internal static EventWaitHandle? ShowSignal { get; private set; }

        /// <summary>
        /// True when this launch starts in the tray without opening the window: at logon
        /// (<see cref="StartupManager.BackgroundArgument"/>), after an update, or for a
        /// <c>--profile-set</c> launch's tray.
        /// </summary>
        internal static bool StartsInBackground { get; private set; }

        /// <summary>
        /// The <c>--profile-set</c> choice handed to a tray that a script's launch started.
        /// App.OnStartup persists it before the displays are first applied.
        /// </summary>
        internal static DisplayProfileSelection? StartupProfileSelection { get; private set; }

        [STAThread]
        public static int Main(string[] args)
        {
            if (HasArgument(args, "--smoke"))
                return RunLaunchSmoke(args);

            ProfileCommand profileCommand = ProfileCommandLine.Parse(args);
            if (profileCommand.Error != null)
            {
                try { Log.Error("Command line: " + profileCommand.Error); } catch { }
                return ProfileCommandLine.ExitInvalidArguments;
            }

            // Single-instance guard. Two trays would fight over the GPU gamma ramp and clobber
            // settings.json, so a second instance (manual launch, "start with Windows" race, or
            // an update's relaunch while the old process is still exiting) must bow out. The
            // "Local\" prefix scopes the mutex to the current login session, which is what we
            // want for a per-user tray app.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName + AppProfile.Suffix, out bool createdNew);
            if (!createdNew)
            {
                if (profileCommand.Selection is { } selection)
                    return ForwardProfileCommand(selection);

                // A plain second launch, such as the Start menu shortcut: open the window of
                // the Gloam that is already running.
                if (!HasArgument(args, StartupManager.BackgroundArgument))
                {
                    try
                    {
                        using var running = EventWaitHandle.OpenExisting(ShowSignalName);
                        running.Set();
                    }
                    catch (Exception ex) { try { Log.Info($"Could not ask the running Gloam to open: {ex.Message}"); } catch { } }
                }
                try { Log.Info("Another instance is already running; exiting."); } catch { }
                return 0;
            }

            // A script's `--profile-set` launch waits for this process to exit, so it must not
            // become the tray itself: it would never exit, and a caller with a timeout stalls.
            // It starts a separate tray instead and reports what that tray confirms.
            if (profileCommand.Selection is { } coldSelection && !ProfileCommandLine.IsTrayLaunch(args))
                return StartTrayAndForward(coldSelection);

            StartupProfileSelection = profileCommand.Selection;
            StartsInBackground = HasArgument(args, StartupManager.BackgroundArgument) || ProfileCommandLine.IsTrayLaunch(args);
            ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);

            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }

        /// <summary>
        /// Hands <c>--profile-set</c> to the running instance. The timeout covers a script
        /// that launches Gloam and immediately sets a profile while the first instance is
        /// still starting up.
        /// </summary>
        private static int ForwardProfileCommand(DisplayProfileSelection selection)
        {
            ProfileCommandSendResult result = ProfileCommandChannel.Send(selection, TimeSpan.FromSeconds(10));
            try { Log.Info($"Forwarded --profile-set {ProfileCommandLine.ToArgument(selection)}: {result}."); } catch { }
            return ToExitCode(result);
        }

        /// <summary>
        /// Starts the tray for a <c>--profile-set</c> launch that found no running instance,
        /// then forwards the selection to it like a second launch would. The new tray opens its
        /// pipe only after startup has applied every monitor's profile, so the wait is longer
        /// than a running instance needs but stays under the 30 seconds a launcher's prepare
        /// step may allow.
        /// </summary>
        private static int StartTrayAndForward(DisplayProfileSelection selection)
        {
            // The new tray has to take the single-instance mutex, so let go of it first. If
            // another launch grabs it in between, the started tray forwards to that one.
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;

            using Process? tray = StartTray(selection);
            if (tray == null) return ProfileCommandLine.ExitInstanceUnreachable;

            ProfileCommandSendResult result = ProfileCommandChannel.SendToStartingInstance(
                selection, TimeSpan.FromSeconds(20), () => tray.HasExited);
            try { Log.Info($"Started the tray (pid {tray.Id}) for --profile-set {ProfileCommandLine.ToArgument(selection)}: {result}."); } catch { }
            return ToExitCode(result);
        }

        private static Process? StartTray(DisplayProfileSelection selection)
        {
            try
            {
                string exePath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("The executable path is unknown.");
                // Shell execute so the tray inherits none of this process's handles. A caller
                // that reads our redirected output until it closes would otherwise wait on the
                // tray, which keeps those pipe handles open for as long as it runs.
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                foreach (string arg in ProfileCommandLine.TrayLaunchArguments(selection))
                    psi.ArgumentList.Add(arg);
                return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            }
            catch (Exception ex)
            {
                try { Log.Error($"--profile-set {ProfileCommandLine.ToArgument(selection)}: could not start the tray: {ex.Message}"); } catch { }
                return null;
            }
        }

        private static int ToExitCode(ProfileCommandSendResult result) => result switch
        {
            ProfileCommandSendResult.Applied => ProfileCommandLine.ExitApplied,
            ProfileCommandSendResult.NotSaved => ProfileCommandLine.ExitNotSaved,
            _ => ProfileCommandLine.ExitInstanceUnreachable
        };

        private static bool HasArgument(string[] args, string name)
            => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        private static int RunLaunchSmoke(string[] args)
        {
            try
            {
                string dataDir = GetRequiredOption(args, "--data-dir");
                AppPaths.UseDataDirectoriesForCurrentProcess(
                    dataDir,
                    Path.Combine(dataDir, "Roaming"));
                Directory.CreateDirectory(AppPaths.DataDir);

                string logPath = Path.Combine(AppPaths.DataDir, "app.log");
                Log.Initialize(logPath);
                Log.Info("Launch smoke: starting.");
                Log.Info($"Launch smoke: data dir = {AppPaths.DataDir}");

                var (settings, monitors, apply, _, _) = App.CreateServices();
                using (apply)
                {
                    _ = settings.ProfileSelection;
                    Log.Info($"Launch smoke: {monitors.EnumerateMonitors().Count} display(s) enumerated.");
                }

                Log.Info("Launch smoke: service composition completed.");
                Log.Info("Launch smoke: completed.");

                if (!File.Exists(logPath))
                    throw new IOException($"Launch smoke log was not created at '{logPath}'.");

                return 0;
            }
            catch (Exception ex)
            {
                try { Log.Error("Launch smoke failed: " + ex); } catch { }
                return 2;
            }
        }

        private static string GetRequiredOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            throw new ArgumentException($"{name} is required for --smoke.");
        }
    }
}
