using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gloam.Core;
using Gloam.Services;

namespace Gloam.Tests
{
    /// <summary>A gamma ramp that keeps what was written, per display, like the hardware would.</summary>
    internal sealed class FakeGammaRamp : IGammaRamp
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (ushort[] R, ushort[] G, ushort[] B)> _ramps = new();

        public int Writes { get; private set; }

        /// <summary>When false, every write is rejected, like a driver refusing the ramp.</summary>
        public bool Accepts { get; set; } = true;

        public bool TryApply(string deviceName, double[] lutR, double[] lutG, double[] lutB)
        {
            lock (_lock)
            {
                if (!Accepts) return false;
                Writes++;
                _ramps[deviceName] = (NativeGammaRamp.BuildRampChannel(lutR),
                    NativeGammaRamp.BuildRampChannel(lutG), NativeGammaRamp.BuildRampChannel(lutB));
                return true;
            }
        }

        public bool TryRead(string deviceName, out ushort[] red, out ushort[] green, out ushort[] blue)
        {
            lock (_lock)
            {
                if (_ramps.TryGetValue(deviceName, out var ramp))
                {
                    (red, green, blue) = (ramp.R.ToArray(), ramp.G.ToArray(), ramp.B.ToArray());
                    return true;
                }
            }
            red = green = blue = [];
            return false;
        }

        public bool TryClear(string deviceName)
        {
            lock (_lock) return _ramps.Remove(deviceName);
        }

        /// <summary>Something else (a game, a driver) overwrites the ramp.</summary>
        public void Overwrite(string deviceName)
        {
            var identity = Enumerable.Range(0, 256).Select(i => (ushort)(i * 257)).ToArray();
            lock (_lock) _ramps[deviceName] = (identity, identity.ToArray(), identity.ToArray());
        }

        /// <summary>The red channel now on <paramref name="deviceName"/>, or null.</summary>
        public ushort[]? Ramp(string deviceName)
        {
            lock (_lock) return _ramps.TryGetValue(deviceName, out var ramp) ? ramp.R.ToArray() : null;
        }

        /// <summary>The ramp a LUT for these settings would put on the display.</summary>
        public static ushort[] Expected(GammaMode mode, double white, DisplayAdjustments adjustments, bool hdr) =>
            NativeGammaRamp.BuildRampChannel(LutGenerator.GenerateLut(mode, white, adjustments, hdr).R);
    }

    internal sealed class FakeMonitorSource : IMonitorSource
    {
        public List<MonitorInfo> Monitors { get; set; } = [];

        public List<MonitorInfo> EnumerateMonitors() =>
            Monitors.Select(m => new MonitorInfo
            {
                DeviceName = m.DeviceName,
                FriendlyName = m.FriendlyName,
                MonitorDevicePath = m.MonitorDevicePath,
                IsHdrActive = m.IsHdrActive,
                SdrWhiteLevel = m.SdrWhiteLevel,
                HMonitor = m.HMonitor,
                CurrentGamma = m.CurrentGamma,
            }).ToList();

        public static MonitorInfo Monitor(int n, bool hdr = true, double white = 240) => new()
        {
            DeviceName = $@"\\.\DISPLAY{n}",
            FriendlyName = $"Panel {n}",
            MonitorDevicePath = $@"SIM\{n}",
            IsHdrActive = hdr,
            SdrWhiteLevel = white,
            HMonitor = new IntPtr(100 + n),
        };
    }

    internal sealed class FakeUpdater : IUpdater
    {
        public Version? CurrentVersion { get; set; } = new(1, 2, 3);
        public bool IsInstallation { get; set; } = true;
        public Func<UpdateCheckResult> Check { get; set; } = () => new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, "v1.2.3");
        public Func<UpdateRelease, string> Download { get; set; } = release => @"C:\setup.exe";
        public List<(string Path, string Sha256)> Installed { get; } = [];
        public Exception? InstallFailure { get; set; }

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Check());

        public Task<string> DownloadAsync(UpdateRelease release, CancellationToken cancellationToken = default) =>
            Task.FromResult(Download(release));

        public void StartInstaller(string setupPath, string sha256)
        {
            if (InstallFailure != null) throw InstallFailure;
            Installed.Add((setupPath, sha256));
        }

        public static UpdateRelease Release(string version = "1.3.0", string notes = "Better things.") =>
            new(Version.Parse(version), "v" + version, notes,
                $"https://github.com/dkozlov-ua/gloam/releases/download/v{version}/Gloam-{version}-setup.exe",
                new string('a', 64), 1000);
    }

    internal sealed class FakeStartupRegistration : IStartupRegistration
    {
        public bool IsEnabled { get; set; }
        public bool Fails { get; set; }

        public bool TrySetEnabled(bool enabled)
        {
            if (Fails) return false;
            IsEnabled = enabled;
            return true;
        }
    }
}
