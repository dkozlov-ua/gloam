using System;
using System.IO;
using System.Linq;
using System.Threading;
using Gloam.Core;
using Gloam.Services;
using Microsoft.Extensions.DependencyInjection;

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

        /// <summary>
        /// A <c>--profile-set</c> choice given to the launch that becomes the running instance.
        /// App.OnStartup persists it before the tray's coordinator reads the selection.
        /// </summary>
        internal static DisplayProfileSelection? StartupProfileSelection { get; private set; }

        [STAThread]
        public static int Main(string[] args)
        {
            if (IsLaunchSmoke(args))
                return RunLaunchSmoke(args);

            ProfileCommand profileCommand = ProfileCommandLine.Parse(args);
            if (profileCommand.Error != null)
            {
                try { Log.Error("Command line: " + profileCommand.Error); } catch { }
                return ProfileCommandLine.ExitInvalidArguments;
            }

            // Single-instance guard. Two trays would fight over the GPU gamma ramp and clobber
            // settings.json, so a second instance (manual launch, "start with Windows" race, or
            // an update's relaunch while the old process is still exiting) must bow out. The "Local\" prefix scopes the mutex to
            // the current login session, which is what we want for a per-user tray app.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: "Local\\Gloam-SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                if (profileCommand.Selection is { } selection)
                    return ForwardProfileCommand(selection);
                try { Log.Info("Another instance is already running; exiting."); } catch { }
                return 0;
            }

            StartupProfileSelection = profileCommand.Selection;
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
            return result switch
            {
                ProfileCommandSendResult.Applied => ProfileCommandLine.ExitApplied,
                ProfileCommandSendResult.NotSaved => ProfileCommandLine.ExitNotSaved,
                _ => ProfileCommandLine.ExitInstanceUnreachable
            };
        }

        private static bool IsLaunchSmoke(string[] args)
            => args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase));

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

                int extracted = ResourceExtractor.ExtractIcmProfiles();
                Log.Info($"Launch smoke: ICM resource extraction checked ({extracted} updated).");

                using var services = App.ConfigureServices();
                _ = services.GetRequiredService<SettingsManager>();
                _ = services.GetRequiredService<NightModeService>();
                _ = services.GetRequiredService<ProfileManager>();

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
