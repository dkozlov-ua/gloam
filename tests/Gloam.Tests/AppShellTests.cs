using System.IO;
using System.Linq;
using System.Windows;
using Gloam.Core;
using Gloam.Services;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>Where the flyout opens, the profile suffix the UI tests rely on, and the display simulator.</summary>
    public sealed class AppShellTests
    {
        private static readonly Rect Monitor = new(0, 0, 1920, 1080);

        [Fact]
        public void Placement_TaskbarAtTheBottom_OpensAboveTheTrayCorner()
        {
            var work = new Rect(0, 0, 1920, 1032);
            Point anchor = FlyoutPlacement.TrayCorner(Monitor, work);

            Point at = FlyoutPlacement.Place(work, new Size(440, 700), anchor);

            Assert.Equal(new Point(1920 - 440 - FlyoutPlacement.Margin, 1032 - 700 - FlyoutPlacement.Margin), at);
        }

        [Fact]
        public void Placement_TaskbarAtTheTop_OpensBelowIt()
        {
            var work = new Rect(0, 48, 1920, 1032);
            Point at = FlyoutPlacement.Place(work, new Size(440, 700), FlyoutPlacement.TrayCorner(Monitor, work));

            Assert.Equal(new Point(1920 - 440 - FlyoutPlacement.Margin, 48 + FlyoutPlacement.Margin), at);
        }

        [Fact]
        public void Placement_TaskbarOnTheLeft_OpensInTheBottomLeftCorner()
        {
            var work = new Rect(62, 0, 1858, 1080);
            Point at = FlyoutPlacement.Place(work, new Size(440, 700), FlyoutPlacement.TrayCorner(Monitor, work));

            Assert.Equal(new Point(62 + FlyoutPlacement.Margin, 1080 - 700 - FlyoutPlacement.Margin), at);
        }

        [Fact]
        public void Placement_TaskbarOnTheRightOrHidden_UsesTheBottomRightCorner()
        {
            var right = new Rect(0, 0, 1858, 1080);
            Assert.Equal(new Point(1920, 1080), FlyoutPlacement.TrayCorner(Monitor, right));
            Assert.Equal(new Point(1920, 1080), FlyoutPlacement.TrayCorner(Monitor, Monitor));
        }

        [Fact]
        public void Placement_TallerThanTheScreen_StaysOnIt()
        {
            var work = new Rect(0, 0, 1920, 600);
            Point at = FlyoutPlacement.Place(work, new Size(440, 900), new Point(1920, 1080));

            Assert.Equal(0, at.Y);
            Assert.Equal(600 - 2 * FlyoutPlacement.Margin, FlyoutPlacement.MaxHeight(work));
            Assert.Equal(200, FlyoutPlacement.MaxHeight(new Rect(0, 0, 100, 100)));
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("e2e-12-3", "-e2e-12-3")]
        [InlineData(@"..\evil name", "-evilname")]
        public void ProfileSuffix_IsEmptyNormallyAndSafeInNames(string? name, string expected)
        {
            Assert.Equal(expected, AppProfile.FromName(name));
        }

        [Fact]
        public void Simulator_ReadsTheDisplaysAndRecordsTheirRamps()
        {
            using var data = new TempDataScope();
            string file = Path.Combine(data.Root, "monitors.json");
            File.WriteAllText(file, """
                [{ "DeviceName": "\\\\.\\DISPLAY1", "FriendlyName": "One", "MonitorDevicePath": "SIM\\ONE", "IsHdrActive": true, "SdrWhiteLevel": 240 },
                 { "DeviceName": "\\\\.\\DISPLAY2", "FriendlyName": "Two", "MonitorDevicePath": "SIM\\TWO" }]
                """);
            var simulator = new Simulator(file);

            var monitors = simulator.EnumerateMonitors();
            Assert.Equal(new[] { "One", "Two" }, monitors.Select(m => m.FriendlyName));
            Assert.True(monitors[0].IsHdrActive && monitors[0].IsHdrCapable);
            Assert.NotEqual(monitors[0].HMonitor, monitors[1].HMonitor);

            double[] lut = LutGenerator.GenerateLut(GammaMode.Gamma24, 240);
            Assert.True(simulator.TryApply(@"\\.\DISPLAY1", lut, lut, lut));
            Assert.True(simulator.TryRead(@"\\.\DISPLAY1", out var red, out _, out _));
            Assert.Equal(NativeGammaRamp.BuildRampChannel(lut), red);
            Assert.True(File.Exists(simulator.RampsFile));

            Assert.True(simulator.TryClear(@"\\.\DISPLAY1"));
            Assert.False(simulator.TryRead(@"\\.\DISPLAY1", out _, out _, out _));
        }

        [Fact]
        public void Simulator_SwitchesHdrInItsMonitorFileAndKeepsTheCapability()
        {
            using var data = new TempDataScope();
            string file = Path.Combine(data.Root, "monitors.json");
            File.WriteAllText(file, """
                [{ "DeviceName": "\\\\.\\DISPLAY1", "FriendlyName": "One", "MonitorDevicePath": "SIM\\ONE", "IsHdrActive": true },
                 { "DeviceName": "\\\\.\\DISPLAY2", "FriendlyName": "Two", "MonitorDevicePath": "SIM\\TWO" }]
                """);
            var simulator = new Simulator(file);
            var monitors = simulator.EnumerateMonitors();

            Assert.True(simulator.TrySetHdr(monitors[0], false));
            MonitorInfo off = simulator.EnumerateMonitors()[0];
            Assert.False(off.IsHdrActive);
            Assert.True(off.IsHdrCapable);

            Assert.True(simulator.TrySetHdr(off, true));
            Assert.True(simulator.EnumerateMonitors()[0].IsHdrActive);

            // An SDR-only display refuses, as Windows does.
            Assert.False(simulator.TrySetHdr(monitors[1], true));
            Assert.False(simulator.EnumerateMonitors()[1].IsHdrActive);
        }

        [Fact]
        public void Simulator_RecordsRevealedFilesInsteadOfOpeningExplorer()
        {
            using var data = new TempDataScope();
            var simulator = new Simulator(Path.Combine(data.Root, "monitors.json"));

            simulator.Reveal(@"C:\one.zip");
            simulator.Reveal(@"C:\two.zip");

            Assert.Equal(new[] { @"C:\one.zip", @"C:\two.zip" }, File.ReadAllLines(simulator.RevealsFile));
        }

        [Fact]
        public void Simulator_UnreadableFile_MeansNoDisplays()
        {
            Assert.Empty(new Simulator(Path.Combine(Path.GetTempPath(), "missing-" + System.Guid.NewGuid() + ".json")).EnumerateMonitors());
        }
    }
}
