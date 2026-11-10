using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gloam.Core
{
    /// <summary>Where the app gets its list of monitors.</summary>
    public interface IMonitorSource
    {
        List<MonitorInfo> EnumerateMonitors();
    }

    /// <summary>A display's GPU gamma ramp, as <see cref="GammaRampWriter"/> uses it.</summary>
    public interface IGammaRamp
    {
        bool TryApply(string deviceName, double[] lutR, double[] lutG, double[] lutB);
        bool TryRead(string deviceName, out ushort[] red, out ushort[] green, out ushort[] blue);
        bool TryClear(string deviceName);
    }

    /// <summary>The real ramp: <see cref="NativeGammaRamp"/>.</summary>
    public sealed class NativeGammaRampDevice : IGammaRamp
    {
        public bool TryApply(string deviceName, double[] lutR, double[] lutG, double[] lutB) =>
            NativeGammaRamp.TryApply(deviceName, lutR, lutG, lutB);

        public bool TryRead(string deviceName, out ushort[] red, out ushort[] green, out ushort[] blue) =>
            NativeGammaRamp.TryRead(deviceName, out red, out green, out blue);

        public bool TryClear(string deviceName) => NativeGammaRamp.TryClear(deviceName);
    }

    /// <summary>
    /// Simulated displays for the UI tests, chosen by the <c>GLOAM_SIMULATOR</c> environment
    /// variable: the path of a JSON file listing the monitors. The app then never touches a
    /// real display; each ramp it writes goes, as the 256-entry hardware ramp, into
    /// <see cref="RampsFile"/> beside it, where a test can read what a display would show.
    /// The monitor file is read again on every enumeration, so a test can change the displays
    /// and then trigger a refresh.
    /// </summary>
    /// <example>
    /// <code>
    /// [{ "DeviceName": "\\\\.\\DISPLAY1", "FriendlyName": "Sim One", "MonitorDevicePath": "SIM\\ONE",
    ///    "IsHdrActive": true, "SdrWhiteLevel": 240 }]
    /// </code>
    /// </example>
    public sealed class Simulator : IMonitorSource, IGammaRamp
    {
        public const string EnvironmentVariable = "GLOAM_SIMULATOR";

        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly object _lock = new();

        public Simulator(string monitorsFile)
        {
            MonitorsFile = Path.GetFullPath(monitorsFile);
        }

        public string MonitorsFile { get; }

        public string RampsFile => MonitorsFile + ".ramps.json";

        /// <summary>The simulator the environment asks for, or null for real displays.</summary>
        public static Simulator? FromEnvironment()
        {
            string? path = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(path) ? null : new Simulator(path);
        }

        public List<MonitorInfo> EnumerateMonitors()
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<MonitorInfo>>(
                    File.ReadAllText(MonitorsFile), ReadOptions) ?? [];
                for (int i = 0; i < list.Count; i++)
                {
                    // A stable, distinct handle per display: the apply service keys on it.
                    list[i].HMonitor = new IntPtr(0x5100 + i);
                    list[i].OutputId = (uint)i;
                    list[i].IsHdrCapable |= list[i].IsHdrActive;
                }
                return list;
            }
            catch (Exception ex)
            {
                Log.Error($"Simulator: could not read {MonitorsFile}: {ex.Message}");
                return [];
            }
        }

        public bool TryApply(string deviceName, double[] lutR, double[] lutG, double[] lutB)
        {
            Write(deviceName, new JsonObject
            {
                ["Red"] = ToJson(NativeGammaRamp.BuildRampChannel(lutR)),
                ["Green"] = ToJson(NativeGammaRamp.BuildRampChannel(lutG)),
                ["Blue"] = ToJson(NativeGammaRamp.BuildRampChannel(lutB)),
            });
            return true;
        }

        public bool TryRead(string deviceName, out ushort[] red, out ushort[] green, out ushort[] blue)
        {
            red = green = blue = [];
            JsonObject? ramp;
            lock (_lock) ramp = Load()[deviceName] as JsonObject;
            if (ramp == null) return false;
            red = FromJson(ramp["Red"]);
            green = FromJson(ramp["Green"]);
            blue = FromJson(ramp["Blue"]);
            return true;
        }

        public bool TryClear(string deviceName)
        {
            Write(deviceName, null);
            return true;
        }

        private void Write(string deviceName, JsonObject? ramp)
        {
            lock (_lock)
            {
                JsonObject all = Load();
                all.Remove(deviceName);
                if (ramp != null) all[deviceName] = ramp;
                string temp = RampsFile + ".tmp";
                File.WriteAllText(temp, all.ToJsonString());
                File.Move(temp, RampsFile, overwrite: true);
            }
        }

        private JsonObject Load()
        {
            try { return JsonNode.Parse(File.ReadAllText(RampsFile)) as JsonObject ?? []; }
            catch (Exception ex) when (ex is IOException or JsonException) { return []; }
        }

        private static JsonArray ToJson(ushort[] values) => new(values.Select(v => (JsonNode)v).ToArray());

        private static ushort[] FromJson(JsonNode? node) =>
            node is JsonArray array ? array.Select(v => v!.GetValue<ushort>()).ToArray() : [];
    }
}
