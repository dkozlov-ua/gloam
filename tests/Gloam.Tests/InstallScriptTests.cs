using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Drives the real install.ps1 (linked into the test output) through Windows
    /// PowerShell 5.1, the interpreter an update runs it with, against packages and
    /// installations in a temp folder. Always -NoShortcut and never -Launch, so nothing
    /// outside the temp folder is written: the uninstall path only READS the Start menu
    /// shortcut and the Run value, and removes them only when they point into the folder
    /// being uninstalled.
    /// </summary>
    public sealed class InstallScriptTests : IDisposable
    {
        private readonly string _root;
        private readonly string _log;

        public InstallScriptTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "GloamInstallScriptTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _log = Path.Combine(_root, "install.log");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static string ScriptSource => Path.Combine(AppContext.BaseDirectory, "install.ps1");

        /// <summary>A package as package.ps1 lays it out: files, VERSION, install.ps1, manifest.txt.</summary>
        private string MakePackage(string version, params (string Path, string Content)[] files)
        {
            string dir = Path.Combine(_root, "pkg-" + version + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            var all = files.Concat(new[] { ("Gloam.exe", "exe " + version) }).ToList();
            foreach (var (rel, content) in all)
            {
                string path = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }
            File.Copy(ScriptSource, Path.Combine(dir, "install.ps1"));
            File.WriteAllText(Path.Combine(dir, "VERSION"), version + "\n");
            var manifest = all.Select(f => f.Item1).Concat(new[] { "install.ps1", "VERSION" }).OrderBy(p => p, StringComparer.Ordinal);
            File.WriteAllText(Path.Combine(dir, "manifest.txt"), string.Join("\n", manifest) + "\n");
            return dir;
        }

        private string Zip(string packageDir)
        {
            string zip = packageDir + ".zip";
            ZipFile.CreateFromDirectory(packageDir, zip);
            return zip;
        }

        private static string Sha256Of(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private (int ExitCode, string Output) Run(string script, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
                psi.ArgumentList.Add(a);
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-LogFile");
            psi.ArgumentList.Add(_log);

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(120_000), "install.ps1 did not finish in 2 minutes");
            process.WaitForExit();
            return (process.ExitCode, stdout.Result + stderr.Result);
        }

        private (int ExitCode, string Output) Install(string packageDir, string target, params string[] extra)
            => Run(Path.Combine(packageDir, "install.ps1"),
                   new[] { "-InstallDir", target, "-NoShortcut" }.Concat(extra).ToArray());

        private static string[] FilesUnder(string dir)
            => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

        [Fact]
        public void Install_CopiesExactlyThePackagesFiles()
        {
            string pkg = MakePackage("1.0.0", ("Gloam.dll", "dll"), (@"argyll_cache\bin\spotread.exe", "tool"));
            string target = Path.Combine(_root, "Gloam");

            var (code, output) = Install(pkg, target);

            Assert.True(code == 0, output);
            Assert.Equal(FilesUnder(pkg), FilesUnder(target));
            Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(target, "VERSION")).Trim());
        }

        [Fact]
        public void Install_RefusesAFolderThatIsNotAnInstallation()
        {
            string pkg = MakePackage("1.0.0");
            string target = Path.Combine(_root, "Documents");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "mine.txt"), "keep");

            var (code, output) = Install(pkg, target);

            Assert.True(code == 2, output);
            Assert.Equal(new[] { "mine.txt" }, FilesUnder(target));
        }

        [Fact]
        public void Install_RefusesAManifestThatPointsOutsideThePackage()
        {
            string pkg = MakePackage("1.0.0");
            File.AppendAllText(Path.Combine(pkg, "manifest.txt"), @"..\outside.txt" + "\n");
            string target = Path.Combine(_root, "Gloam");

            var (code, output) = Install(pkg, target);

            Assert.True(code != 0, output);
            Assert.False(File.Exists(Path.Combine(target, "Gloam.exe")));
        }

        [Fact]
        public void Update_FromZip_ReplacesInPlaceAndKeepsWhatGloamWrote()
        {
            string target = Path.Combine(_root, "Gloam");
            var (first, firstOut) = Install(MakePackage("1.0.0", ("Gloam.dll", "dll 1"), (@"dropped\old.txt", "old")), target);
            Assert.True(first == 0, firstOut);
            // Written beside the exe by the app itself (ResourceExtractor), not by the installer.
            File.WriteAllText(Path.Combine(target, "extracted.icm"), "app's own");

            string zip = Zip(MakePackage("1.1.0", ("Gloam.dll", "dll 2"), ("new.txt", "new")));
            var (code, output) = Run(Path.Combine(target, "install.ps1"),
                "-Package", zip, "-Sha256", Sha256Of(zip), "-DeletePackage");

            Assert.True(code == 0, output);
            Assert.Equal("1.1.0", File.ReadAllText(Path.Combine(target, "VERSION")).Trim());
            Assert.Equal("dll 2", File.ReadAllText(Path.Combine(target, "Gloam.dll")));
            Assert.Equal("exe 1.1.0", File.ReadAllText(Path.Combine(target, "Gloam.exe")));
            Assert.True(File.Exists(Path.Combine(target, "new.txt")));
            Assert.False(Directory.Exists(Path.Combine(target, "dropped")), "a file the new release dropped is removed");
            Assert.Equal("app's own", File.ReadAllText(Path.Combine(target, "extracted.icm")));
            Assert.DoesNotContain(FilesUnder(target), f => f.Contains(".gloam-old") || f.StartsWith(".install-"));
            Assert.False(File.Exists(zip), "-DeletePackage removes the download");
        }

        [Fact]
        public void Update_HashMismatch_ChangesNothing()
        {
            string target = Path.Combine(_root, "Gloam");
            var (first, firstOut) = Install(MakePackage("1.0.0", ("Gloam.dll", "dll 1")), target);
            Assert.True(first == 0, firstOut);
            string[] before = FilesUnder(target);

            string zip = Zip(MakePackage("1.1.0", ("Gloam.dll", "dll 2")));
            var (code, output) = Run(Path.Combine(target, "install.ps1"),
                "-Package", zip, "-Sha256", new string('0', 64));

            Assert.True(code == 1, output);
            Assert.Equal(before, FilesUnder(target));
            Assert.Equal("dll 1", File.ReadAllText(Path.Combine(target, "Gloam.dll")));
        }

        [Fact]
        public void Update_RefusesToRunOutsideAnInstallation()
        {
            string pkg = MakePackage("1.0.0");
            File.Delete(Path.Combine(pkg, "manifest.txt"));
            string zip = Zip(MakePackage("1.1.0"));

            var (code, output) = Run(Path.Combine(pkg, "install.ps1"), "-Package", zip, "-Sha256", Sha256Of(zip));

            Assert.True(code == 2, output);
        }

        [Fact]
        public void Uninstall_RemovesThePackagesFilesOnly()
        {
            string target = Path.Combine(_root, "Gloam");
            var (first, firstOut) = Install(MakePackage("1.0.0", (@"argyll_cache\bin\spotread.exe", "tool")), target);
            Assert.True(first == 0, firstOut);
            File.WriteAllText(Path.Combine(target, "extracted.icm"), "app's own");

            var (code, output) = Run(Path.Combine(target, "install.ps1"), "-Uninstall", "-InstallDir", target);

            Assert.True(code == 0, output);
            Assert.Equal(new[] { "extracted.icm" }, FilesUnder(target));
        }
    }
}
