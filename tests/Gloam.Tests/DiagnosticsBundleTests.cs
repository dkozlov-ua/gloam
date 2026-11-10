using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Gloam.Core;
using System.Text.RegularExpressions;
using Gloam.Interop;
using Xunit;

namespace Gloam.Tests
{
    public class DiagnosticsBundleTests
    {
        [Fact]
        public void SanitizeText_ReplacesUserScopedPathsAndUserName()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string input = $"{userProfile}\\Documents\\probe.txt\n{localAppData}\\Gloam\\app.log\nuser={Environment.UserName}";

            string result = DiagnosticsBundle.SanitizeText(input);

            Assert.DoesNotContain(userProfile, result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(localAppData, result, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(Environment.UserName))
                Assert.DoesNotMatch(WholeWord(Environment.UserName), result);
            Assert.Contains("%USERPROFILE%", result);
            Assert.Contains("%LOCALAPPDATA%", result);
        }

        [Fact]
        public void SanitizeText_ReplacesAShortUserNameOnlyAsAWord()
        {
            string result = DiagnosticsBundle.SanitizeText(
                "user=me; signed in as ME.\nDocuments\\name\\some_me_file", "me");

            Assert.Equal("user=%USERNAME%; signed in as %USERNAME%.\nDocuments\\name\\some_me_file", result);
        }

        [Fact]
        public void SanitizeText_RedactsMonitorIdentifiersInLogs()
        {
            string displayPath = @"\\?\DISPLAY#ACME123#INSTANCE#UID";
            string monitorPath = @"MONITOR\ACME123\{4d36e96e-e325-11ce-bfc1-08002be10318}\0008";
            string input = $"Installing profile for {displayPath}\nGDI monitor={monitorPath}";

            string result = DiagnosticsBundle.SanitizeText(input);

            Assert.DoesNotContain(displayPath, result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(monitorPath, result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("monitor-", result);
        }

        [Fact]
        public void BuildManifest_IncludesDecodedDisplayTopologyWithoutRawMonitorPath()
        {
            string rawPath = @"\\?\DISPLAY#ACME123#INSTANCE#UID";
            var monitor = new MonitorInfo
            {
                DeviceName = @"\\.\DISPLAY7",
                FriendlyName = "Reference HDR",
                MonitorDevicePath = rawPath,
                IsHdrCapable = true,
                IsHdrActive = true,
                DxgiColorSpace = 12,
                BitsPerColor = 3,
                SdrWhiteLevel = 203,
                HdrMinNits = 0.005,
                HdrPeakNits = 1000,
                HdrMaxFullFrameNits = 650,
                AdapterLuid = new Dxgi.LUID { HighPart = 1, LowPart = 2 },
                OutputId = 4,
                MonitorBounds = new Dxgi.RECT { Left = -1920, Top = 0, Right = 0, Bottom = 1080 },
                HasDisplayConfigIds = true,
                DisplayConfigAdapterId = new Dxgi.LUID { HighPart = 3, LowPart = 4 },
                DisplayConfigSourceId = 9
            };

            string manifest = DiagnosticsBundle.BuildManifest(new[] { monitor });
            var root = JsonNode.Parse(manifest)!;
            var mon = root["Monitors"]![0]!;

            Assert.Equal(12, mon["DxgiColorSpace"]!.GetValue<int>());
            Assert.Equal("RGB_FULL_G2084_NONE_P2020", mon["DxgiColorSpaceName"]!.GetValue<string>());
            Assert.Equal(3, mon["BitsPerColor"]!.GetValue<int>());
            Assert.Equal("10 bpc", mon["BitsPerColorName"]!.GetValue<string>());
            Assert.Equal(1920, mon["Bounds"]!["Width"]!.GetValue<int>());
            Assert.Equal(1080, mon["Bounds"]!["Height"]!.GetValue<int>());
            Assert.Equal(9, mon["DisplayConfigSourceId"]!.GetValue<int>());
            Assert.NotEmpty(mon["MonitorDevicePathHash"]!.GetValue<string>());
            Assert.DoesNotContain(rawPath, manifest, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Create_IncludesSanitizedUpdaterStateWhenPresent()
        {
            string originalData = AppPaths.DataDir;
            string originalRoaming = AppPaths.RoamingDataDir;
            string dir = CreateTempDirectory();
            try
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(dir, Path.Combine(dir, "roaming"));
                Directory.CreateDirectory(AppPaths.DataDir);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                File.WriteAllText(Path.Combine(AppPaths.DataDir, "update-state.json"), """
                {
                  "IsInstalled": true,
                  "InstalledVersion": "1.0.2",
                  "LastResult": "download-failed",
                  "LastError": "Failed under LOCAL_APP_DATA_PLACEHOLDER\\Gloam"
                }
                """.Replace("LOCAL_APP_DATA_PLACEHOLDER", JsonEscape(localAppData)));

                var settings = new SettingsManager();
                string zipPath = new DiagnosticsBundle().Create(
                    Path.Combine(dir, "Diagnostics"),
                    Array.Empty<MonitorInfo>(),
                    settings);

                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.GetEntry("update-state.sanitized.json");
                Assert.NotNull(entry);
                using var reader = new StreamReader(entry!.Open());
                string text = reader.ReadToEnd();

                Assert.Contains("\"InstalledVersion\": \"1.0.2\"", text);
                Assert.Contains("%LOCALAPPDATA%", text);
                Assert.DoesNotContain(localAppData, text, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                AppPaths.UseDataDirectoriesForCurrentProcess(originalData, originalRoaming);
                DeleteDirectory(dir);
            }
        }

        [Fact]
        public void SanitizeJson_RedactsMonitorPathKeysAndUserScopedValues()
        {
            string rawMonitorPath = @"\\?\DISPLAY#ACME123#INSTANCE#UID";
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string json = $$"""
            {
              "MonitorProfiles": {
                "{{JsonEscape(rawMonitorPath)}}": {
                  "MeterCorrectionPath": "{{JsonEscape(Path.Combine(userProfile, "Corrections", "panel.ccss"))}}"
                }
              }
            }
            """;

            string sanitized = DiagnosticsBundle.SanitizeJson(json);
            var root = JsonNode.Parse(sanitized)!;
            var profileKey = root["MonitorProfiles"]!.AsObject().Select(kvp => kvp.Key).Single();

            Assert.StartsWith("monitor-", profileKey, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(rawMonitorPath, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(userProfile, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", sanitized);
        }

        [Fact]
        public void SanitizeJson_RedactsMonitorPathStringValues()
        {
            string rawMonitorPath = @"\\?\DISPLAY#ACME123#INSTANCE#UID";
            string json = $$"""
            {
              "MonitorDevicePath": "{{JsonEscape(rawMonitorPath)}}",
              "Notes": "Measured {{JsonEscape(rawMonitorPath)}} during HDR verification"
            }
            """;

            string sanitized = DiagnosticsBundle.SanitizeJson(json);

            Assert.DoesNotContain(rawMonitorPath, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DISPLAY#ACME123", sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("monitor-", sanitized);
        }

        [Fact]
        public void SanitizeJson_RedactsUserScopedObjectKeys()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string rawKey = Path.Combine(userProfile, "Documents", "probe.txt");
            string json = $$"""
            {
              "{{JsonEscape(rawKey)}}": "present"
            }
            """;

            string sanitized = DiagnosticsBundle.SanitizeJson(json);

            Assert.DoesNotContain(userProfile, sanitized, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("%USERPROFILE%", sanitized);
        }

        [Fact]
        public void ResolveThirdPartyNoticesPath_UsesApplicationBaseDirectory()
        {
            string appBase = CreateTempDirectory();
            try
            {
                string notices = Path.Combine(appBase, "THIRD_PARTY_NOTICES.txt");
                File.WriteAllText(notices, "notices");

                Assert.Equal(notices, DiagnosticsBundle.ResolveThirdPartyNoticesPath(appBase));
            }
            finally
            {
                DeleteDirectory(appBase);
            }
        }

        [Fact]
        public void ResolveThirdPartyNoticesPath_DoesNotFallBackToCurrentDirectory()
        {
            string originalCurrentDirectory = Environment.CurrentDirectory;
            string appBase = CreateTempDirectory();
            string currentDirectory = CreateTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(currentDirectory, "THIRD_PARTY_NOTICES.txt"), "wrong file");
                Environment.CurrentDirectory = currentDirectory;

                Assert.Null(DiagnosticsBundle.ResolveThirdPartyNoticesPath(appBase));
            }
            finally
            {
                Environment.CurrentDirectory = originalCurrentDirectory;
                DeleteDirectory(appBase);
                DeleteDirectory(currentDirectory);
            }
        }

        [Fact]
        public void BuildUniqueBundlePath_UsesPlainTimestampWhenAvailable()
        {
            string dir = CreateTempDirectory();
            try
            {
                var timestamp = new DateTimeOffset(2026, 6, 29, 10, 11, 12, TimeSpan.Zero);

                string path = DiagnosticsBundle.BuildUniqueBundlePath(dir, timestamp);

                Assert.Equal(Path.Combine(dir, "Gloam-Diagnostics-20260629-101112.zip"), path);
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        [Fact]
        public void BuildUniqueBundlePath_AddsSuffixWhenTimestampCollides()
        {
            string dir = CreateTempDirectory();
            try
            {
                var timestamp = new DateTimeOffset(2026, 6, 29, 10, 11, 12, TimeSpan.Zero);
                File.WriteAllText(Path.Combine(dir, "Gloam-Diagnostics-20260629-101112.zip"), "");
                File.WriteAllText(Path.Combine(dir, "Gloam-Diagnostics-20260629-101112-02.zip"), "");

                string path = DiagnosticsBundle.BuildUniqueBundlePath(dir, timestamp);

                Assert.Equal(Path.Combine(dir, "Gloam-Diagnostics-20260629-101112-03.zip"), path);
            }
            finally
            {
                DeleteDirectory(dir);
            }
        }

        private static Regex WholeWord(string word) =>
            new(@"(?<![\p{L}\p{N}_])" + Regex.Escape(word) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase);

        private static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GloamDiagnosticsBundleTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string JsonEscape(string value)
            => value.Replace(@"\", @"\\").Replace("\"", "\\\"");

        private static void DeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test temp files.
            }
        }
    }
}
