using System;
using System.Diagnostics;
using Gloam.Core;
using Microsoft.Win32;

namespace Gloam.Services
{
    /// <summary>The Start with Windows switch, as the window uses it.</summary>
    public interface IStartupRegistration
    {
        bool IsEnabled { get; }
        bool TrySetEnabled(bool enabled);
    }

    /// <summary>
    /// Start with Windows: an HKCU Run value that starts Gloam.exe in the tray
    /// (<see cref="BackgroundArgument"/>), without opening its window.
    /// </summary>
    public sealed class StartupManager : IStartupRegistration
    {
        // installer\Gloam.iss deletes this value on uninstall: InstallerScriptTests pins the two.
        internal const string AppName = "Gloam";
        internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>Starts Gloam in the tray only: what logon and an update's relaunch use.</summary>
        public const string BackgroundArgument = "--background";

        /// <summary>The Run value's name: <see cref="AppName"/>, per <see cref="AppProfile"/>.</summary>
        internal static string RunValueName => AppName + AppProfile.Suffix;

        public static StartupManager Instance { get; } = new();

        public bool IsEnabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
                    return key?.GetValue(RunValueName) != null;
                }
                catch (Exception ex)
                {
                    Log.Info($"StartupManager: could not read the Run value: {ex.Message}");
                    return false;
                }
            }
        }

        public bool TrySetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
                if (key == null) return false;

                if (enabled)
                {
                    string value = RunValue(GetExePath());
                    key.SetValue(RunValueName, value);
                    Log.Info($"StartupManager: Enabled startup: {value}");
                }
                else
                {
                    key.DeleteValue(RunValueName, false);
                    Log.Info("StartupManager: Disabled startup");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Info($"StartupManager: Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>The Run value for <paramref name="exePath"/>.</summary>
        internal static string RunValue(string exePath) => $"\"{exePath}\" {BackgroundArgument}";

        /// <summary>
        /// Turns Start with Windows on for a fresh installation: once, and only when there was
        /// no settings file yet, so a user who turned it off is never overruled.
        /// </summary>
        public static void EnableByDefaultForFreshInstall(SettingsManager settings, bool isInstalled)
        {
            if (settings == null) return;
            if (!isInstalled) return;
            if (settings.LoadedExistingSettingsFile) return;
            if (settings.StartupDefaultApplied) return;

            if (Instance.TrySetEnabled(true))
            {
                settings.MarkStartupDefaultApplied();
                Log.Info("StartupManager: Applied fresh-install default startup registration.");
            }
        }

        /// <summary>
        /// Self-heal for a stale Run value: if the registered startup command is not the one
        /// this executable would write (another path, or an older command line that opened the
        /// window at logon), rewrite it. Only an INSTALLATION may do this — a dev run must never
        /// steal the startup registration from the real install.
        /// </summary>
        public static void RepairIfStale(bool isInstalled)
        {
            if (!isInstalled) return;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
                if (key == null) return;

                var registered = key.GetValue(RunValueName) as string;
                if (registered == null) return; // startup not enabled; nothing to repair

                string exePath = GetExePath();
                if (!NeedsRepair(registered, exePath)) return;

                string current = RunValue(exePath);
                key.SetValue(RunValueName, current);
                Log.Info($"StartupManager: Repaired stale startup registration {registered} -> {current}");
            }
            catch (Exception ex)
            {
                Log.Info($"StartupManager: Startup repair error: {ex.Message}");
            }
        }

        /// <summary>
        /// True when a registered Run value is not what <see cref="TrySetEnabled"/> would write
        /// for <paramref name="exePath"/>: another executable, or an older command line (before
        /// <see cref="BackgroundArgument"/>, logon opened the window). Case-insensitive.
        /// </summary>
        internal static bool NeedsRepair(string? registeredValue, string exePath) =>
            registeredValue != null &&
            !string.Equals(registeredValue.Trim(), RunValue(exePath), StringComparison.OrdinalIgnoreCase);

        private static string GetExePath()
        {
            return Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("The executable path is unknown.");
        }
    }
}
