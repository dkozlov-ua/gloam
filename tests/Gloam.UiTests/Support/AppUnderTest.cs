using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Gloam.Core;
using Microsoft.Win32;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // one app on the desktop at a time

namespace Gloam.UiTests.Support;

/// <summary>
/// Gloam.exe started as its own profile (GLOAM_PROFILE: its own data folder, single-instance
/// names and Start with Windows value) with simulated displays (GLOAM_SIMULATOR), whose ramps
/// it records in a file instead of writing to a GPU. Everything is removed afterwards.
/// </summary>
internal sealed class AppUnderTest : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static int _count;

    public static string Exe { get; } = Path.Combine(AppContext.BaseDirectory, "Gloam.exe");

    public string Profile { get; } = $"e2e-{Environment.ProcessId}-{Interlocked.Increment(ref _count)}";
    public string Work { get; }
    public string MonitorsFile => Path.Combine(Work, "monitors.json");
    public string RampsFile => MonitorsFile + ".ramps.json";
    public string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gloam-" + Profile);
    public string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public string LogFile => Path.Combine(DataFolder, "app.log");
    public string RunValueName => "Gloam-" + Profile;

    /// <summary>The simulated displays: an HDR one and an SDR one.</summary>
    public static readonly (string Device, string Name, string Path, bool Hdr, double White)[] Displays =
    [
        (@"\\.\DISPLAY1", "Sim One", @"SIM\ONE", true, 240),
        (@"\\.\DISPLAY2", "Sim Two", @"SIM\TWO", false, 80),
    ];

    public Process? Process { get; private set; }
    public int Pid => Process!.Id;

    public AppUnderTest()
    {
        Work = Path.Combine(Path.GetTempPath(), "Gloam.UiTests", Profile);
        Directory.CreateDirectory(Work);
        var monitors = new JsonArray();
        foreach (var d in Displays)
        {
            monitors.Add(new JsonObject
            {
                ["DeviceName"] = d.Device, ["FriendlyName"] = d.Name, ["MonitorDevicePath"] = d.Path,
                ["IsHdrActive"] = d.Hdr, ["SdrWhiteLevel"] = d.White,
            });
        }
        File.WriteAllText(MonitorsFile, monitors.ToJsonString());
    }

    /// <summary>Writes settings.json for the profile before the app starts.</summary>
    public void Seed(string json)
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(SettingsFile, json);
    }

    public ProcessStartInfo StartInfo(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment[AppProfile.EnvironmentVariable] = Profile;
        psi.Environment[Simulator.EnvironmentVariable] = MonitorsFile;
        return psi;
    }

    /// <summary>Starts the app and waits for its window (unless it starts in the background).</summary>
    public AutomationElement? Start(params string[] args)
    {
        Process = Process.Start(StartInfo(args))!;
        Ui.Until(() => Log().Contains("App.OnStartup: started."), "the app to start");
        return args.Contains("--background") ? null : Window();
    }

    /// <summary>Runs Gloam.exe with these arguments to the end, e.g. --profile-set; returns its exit code.</summary>
    public int Run(params string[] args)
    {
        using var process = Process.Start(StartInfo(args))!;
        if (!process.WaitForExit(TimeSpan.FromSeconds(45))) throw new TimeoutException($"Gloam.exe {string.Join(' ', args)} didn't end");
        return process.ExitCode;
    }

    /// <summary>The flyout of this profile's running Gloam, whichever process started it.</summary>
    public AutomationElement Window(TimeSpan? timeout = null) =>
        Ui.WaitFor(TryWindow, "the Gloam window", timeout);

    public AutomationElement? TryWindow()
    {
        foreach (int pid in RunningPids())
        {
            var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, pid),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "Flyout")));
            if (window != null && !window.Current.IsOffscreen) return window;
        }
        return null;
    }

    /// <summary>Gloam processes of this test's build (a tray a --profile-set launch started included).</summary>
    public IReadOnlyList<int> RunningPids()
    {
        var pids = new List<int>();
        foreach (var process in Process.GetProcessesByName("Gloam"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase))
                        pids.Add(process.Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // exited, or not ours to inspect
                }
            }
        }
        return pids;
    }

    public JsonNode Settings() => Ui.WaitFor(() =>
    {
        try { return JsonNode.Parse(ReadShared(SettingsFile)); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; } // being replaced
    }, "settings.json");

    public string Log() => File.Exists(LogFile) ? ReadShared(LogFile) : "";

    /// <summary>The red channel of the ramp the simulator holds for a display, or null.</summary>
    public ushort[]? Ramp(string device)
    {
        try
        {
            if (JsonNode.Parse(ReadShared(RampsFile))?[device]?["Red"] is not JsonArray red) return null;
            return red.Select(v => v!.GetValue<ushort>()).ToArray();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Waits until display <paramref name="index"/> shows the ramp for these settings.</summary>
    public void ShowsRamp(int index, GammaMode mode, double brightness = 100, double shadow = 0, bool linear = false)
    {
        var d = Displays[index];
        var adjustments = new DisplayAdjustments { Brightness = brightness, ShadowDetailStrength = shadow, UseLinearBrightness = linear };
        ushort[] expected = NativeGammaRamp.BuildRampChannel(LutGenerator.GenerateLut(mode, d.White, adjustments, d.Hdr).R);
        Ui.Until(() => Ramp(d.Device) is { } actual && actual.SequenceEqual(expected),
            $"{d.Name} to show {mode} at {brightness}% (shadow {shadow}, linear {linear})");
    }

    public string? RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValueName) as string;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    public void Dispose()
    {
        foreach (int pid in RunningPids())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already gone
            }
        }
        Process?.Dispose();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        foreach (string dir in new[] { DataFolder, Work })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
