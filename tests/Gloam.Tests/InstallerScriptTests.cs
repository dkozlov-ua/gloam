using System;
using System.IO;
using Gloam.Services;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// installer\Gloam.iss names things the app defines - the single-instance mutex it waits
    /// on, the switch the update passes it, the Start with Windows value it removes - in Inno
    /// Setup's Pascal, where nothing checks them against the C#. These pin the two together,
    /// so a rename on one side cannot silently break an update or an uninstall.
    /// </summary>
    public sealed class InstallerScriptTests
    {
        private static readonly string Script =
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gloam.iss"));

        [Fact]
        public void WaitsOnTheAppsSingleInstanceMutex()
        {
            Assert.Contains($"InstanceMutex = '{Program.SingleInstanceMutexName}';", Script);
        }

        [Fact]
        public void ReadsTheRelaunchSwitchTheUpdatePasses()
        {
            // "/relaunch=1" is Inno Setup's {param:relaunch}.
            string name = UpdateService.RelaunchSwitch.TrimStart('/').Split('=')[0];
            Assert.Equal("/relaunch=1", UpdateService.RelaunchSwitch);
            Assert.Contains($"{{param:{name}|0}}') = '1'", Script);
        }

        [Fact]
        public void RemovesTheStartWithWindowsValueTheAppWrites()
        {
            Assert.Contains($"RunKey = '{StartupManager.RegistryPath}';", Script);
            Assert.Contains($"RunValue = '{StartupManager.AppName}';", Script);
        }

        [Fact]
        public void InstallsPerUserIntoTheFixedFolder()
        {
            // The Run value and the Start menu shortcut point into this folder, and every
            // update must land in the same one.
            Assert.Contains("PrivilegesRequired=lowest", Script);
            Assert.Contains(@"DefaultDirName={autopf}\Gloam", Script);
            Assert.Contains("DisableDirPage=yes", Script);
        }

        [Fact]
        public void NamesTheSetupTheUpdateLooksFor()
        {
            Assert.Equal("Gloam-1.2.3-setup.exe", UpdateService.PackageFileName(new Version(1, 2, 3)));
            Assert.Contains("OutputBaseFilename=Gloam-{#Version}-setup", Script);
        }

        [Fact]
        public void IsAscii()
        {
            // ISCC reads a BOM-less script in the ANSI codepage.
            Assert.DoesNotContain(Script, c => c > 127);
        }
    }
}
