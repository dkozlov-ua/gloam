using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gloam.Core;

namespace Gloam.Services
{
    /// <summary>
    /// Updates on a click, from this repository's GitHub Releases.
    ///
    /// Nothing is checked by itself. "Check for Updates" in the tray asks GitHub for the
    /// repository's latest release (<see cref="UpdateRepo"/>; a draft or a prerelease is never
    /// "latest") and compares its vX.Y.Z tag with the running version. A newer one is offered
    /// with its notes, and Install downloads its Gloam-X.Y.Z.zip, checks it against the SHA-256
    /// GitHub itself publishes for the asset (its "digest"; a release without one is not
    /// offered), and hands it to install.ps1 BESIDE Gloam.exe. That script waits for this
    /// process to exit, puts the new version's files into this same folder and starts Gloam
    /// again. The folder never moves, so the Start with Windows registration and the Start
    /// menu shortcut never need repointing. See install.ps1 and RELEASE.md.
    ///
    /// The package is fetched only from under <see cref="DownloadUrlPrefix"/>, whatever the
    /// answer names, and install.ps1 checks the hash again on a copy of its own, because the
    /// download sits in the data folder, where the user's programs can write.
    ///
    /// Only an INSTALLATION updates itself: a folder with the install.ps1 and manifest.txt a
    /// package carries. A dev run (F5, dotnet run) can check, and is told to install the
    /// release instead.
    ///
    /// The request sends no token, so the repository must be public. A private one answers
    /// 404, which is reported as such rather than as "there is no release".
    /// </summary>
    public class UpdateService
    {
        /// <summary>
        /// The repository releases come from. publish.ps1 refuses to publish anywhere else,
        /// because a mismatch would publish releases nothing finds.
        /// </summary>
        internal const string UpdateRepo = "dkozlov-ua/gloam";
        internal const string LatestReleaseUrl = "https://api.github.com/repos/" + UpdateRepo + "/releases/latest";
        internal const string DownloadUrlPrefix = "https://github.com/" + UpdateRepo + "/releases/download/";
        internal const string ReleasesPageUrl = "https://github.com/" + UpdateRepo + "/releases";

        // The files package.ps1 puts beside Gloam.exe that make a folder an installation.
        internal const string ManifestFileName = "manifest.txt";
        internal const string InstallerFileName = "install.ps1";

        private static readonly HttpClient SharedClient = CreateClient(new HttpClientHandler());

        private readonly HttpClient _client;
        private readonly string _appDir;

        public UpdateService()
            : this(SharedClient, AppContext.BaseDirectory, RunningVersion())
        {
        }

        internal UpdateService(HttpClient client, string appDir, Version? currentVersion)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _appDir = appDir ?? throw new ArgumentNullException(nameof(appDir));
            CurrentVersion = currentVersion;
        }

        /// <summary>The running version (X.Y.Z), or null when the assembly carries none.</summary>
        public Version? CurrentVersion { get; }

        public string DisplayVersion => "v" + (CurrentVersion?.ToString(3) ?? "?");

        /// <summary>
        /// True when Gloam runs from a folder install.ps1 owns, so it can update itself. Also
        /// the gate for taking over the Start with Windows registration: a dev run must never
        /// steal it from the real installation.
        /// </summary>
        public bool IsInstallation =>
            File.Exists(Path.Combine(_appDir, ManifestFileName)) &&
            File.Exists(Path.Combine(_appDir, InstallerFileName));

        internal static HttpClient CreateClient(HttpMessageHandler handler)
        {
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            // GitHub's API refuses requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Gloam-UpdateCheck");
            return client;
        }

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            string json;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await _client.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return UpdateCheckResult.Failed(
                        $"GitHub has no published release of {UpdateRepo}, or the repository is private " +
                        "(the check sends no token).");
                }
                if (!response.IsSuccessStatusCode)
                {
                    return UpdateCheckResult.Failed(
                        $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                }
                json = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                return UpdateCheckResult.Failed($"Could not reach GitHub: {ex.Message}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return UpdateCheckResult.Failed("GitHub did not answer in time.");
            }

            var result = Evaluate(json, CurrentVersion);
            Log.Info($"UpdateService: check: {result.Status} (running {DisplayVersion}" +
                     (result.Release != null ? $", offering {result.Release.Tag}" : "") +
                     (result.Message != null ? $"; {result.Message}" : "") + ")");
            return result;
        }

        /// <summary>
        /// What a latest-release answer means for <paramref name="current"/>. Pure, so the
        /// rules (the tag's shape, the asset's name, origin and digest) are unit-testable.
        /// </summary>
        internal static UpdateCheckResult Evaluate(string json, Version? current)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? tag = GetString(root, "tag_name");
                Version? version = ParseTag(tag);
                if (version == null)
                    return UpdateCheckResult.Failed($"The latest release's tag '{tag}' is not vX.Y.Z.");

                // What the latest release carries only matters when it would be installed.
                if (current != null && version <= current)
                    return UpdateCheckResult.UpToDate(tag!);

                return FindPackage(root, tag!, version);
            }
            catch (JsonException ex)
            {
                return UpdateCheckResult.Failed($"The latest release could not be read: {ex.Message}");
            }
        }

        private static readonly Regex TagPattern = new(@"^v(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant);
        private static readonly Regex DigestPattern = new(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant);

        internal static Version? ParseTag(string? tag)
        {
            if (tag == null) return null;
            var m = TagPattern.Match(tag);
            if (!m.Success) return null;
            try
            {
                return new Version(
                    int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        internal static string PackageFileName(Version version) => $"Gloam-{version.ToString(3)}.zip";

        private static UpdateCheckResult FindPackage(JsonElement root, string tag, Version version)
        {
            string expectedName = PackageFileName(version);
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!string.Equals(GetString(asset, "name"), expectedName, StringComparison.Ordinal))
                        continue;

                    string? url = GetString(asset, "browser_download_url");
                    if (url == null || !url.StartsWith(DownloadUrlPrefix, StringComparison.Ordinal))
                        return UpdateCheckResult.Failed($"{tag}'s {expectedName} is not served from {DownloadUrlPrefix}.");

                    var digest = DigestPattern.Match(GetString(asset, "digest") ?? "");
                    if (!digest.Success)
                    {
                        return UpdateCheckResult.Failed(
                            $"GitHub publishes no SHA-256 for {tag}'s {expectedName}, so it cannot be checked.");
                    }

                    long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                    return UpdateCheckResult.Available(new UpdateRelease(
                        version,
                        tag,
                        (GetString(root, "body") ?? "").Trim(),
                        url,
                        digest.Groups[1].Value.ToLowerInvariant(),
                        size));
                }
            }

            return UpdateCheckResult.Failed($"{tag} has no {expectedName}.");
        }

        private static string? GetString(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>
        /// Downloads the release's package into the data folder's Updates directory and
        /// checks it against the release's SHA-256. Returns the path of the checked file;
        /// throws when the download fails or does not match.
        /// </summary>
        public async Task<string> DownloadAsync(UpdateRelease release, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(release);
            if (!release.DownloadUrl.StartsWith(DownloadUrlPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to download from {release.DownloadUrl}.");

            // One package at a time: whatever an earlier attempt left is no longer wanted.
            string dir = UpdatesDir;
            if (Directory.Exists(dir))
            {
                foreach (string stale in Directory.EnumerateFiles(dir))
                {
                    try { File.Delete(stale); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, PackageFileName(release.Version));
            string partial = path + ".partial";
            try
            {
                using (var response = await _client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                    await source.CopyToAsync(target, cancellationToken);
                }

                string actual;
                await using (var check = File.OpenRead(partial))
                {
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken)).ToLowerInvariant();
                }
                if (!string.Equals(actual, release.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The downloaded {PackageFileName(release.Version)} has SHA-256 {actual}, not {release.Sha256}.");
                }

                File.Move(partial, path, overwrite: true);
                Log.Info($"UpdateService: downloaded {release.Tag} to {path} (sha256 {actual}).");
                return path;
            }
            catch
            {
                try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        internal static string UpdatesDir => Path.Combine(AppPaths.DataDir, "Updates");

        /// <summary>
        /// Starts this installation's install.ps1 on a checked package. It waits for this
        /// process to exit, so the caller shuts the app down right after.
        /// </summary>
        public void StartInstaller(string packagePath, string sha256)
        {
            if (!IsInstallation)
                throw new InvalidOperationException($"{_appDir} is not an installation; it cannot update itself.");

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _appDir,
            };
            foreach (string arg in InstallerArguments(_appDir, packagePath, sha256, Environment.ProcessId))
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("powershell.exe did not start.");
            Log.Info($"UpdateService: started {InstallerFileName} (pid {process.Id}) for {packagePath}.");
        }

        internal static string[] InstallerArguments(string appDir, string packagePath, string sha256, int waitPid) =>
        [
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
            "-File", Path.Combine(appDir, InstallerFileName),
            "-Package", packagePath,
            "-Sha256", sha256,
            "-WaitPid", waitPid.ToString(CultureInfo.InvariantCulture),
            "-Launch",
            "-DeletePackage",
        ];

        private static Version? RunningVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? null : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>A release that can be installed: its package, where it is and its SHA-256.</summary>
    public sealed record UpdateRelease(
        Version Version,
        string Tag,
        string Notes,
        string DownloadUrl,
        string Sha256,
        long Size);

    public enum UpdateCheckStatus
    {
        UpToDate,
        Available,
        Failed,
    }

    /// <summary>
    /// A check's outcome. <see cref="Release"/> is set only when one is Available;
    /// <see cref="Message"/> is the latest tag when UpToDate, and why when Failed.
    /// </summary>
    public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateRelease? Release, string? Message)
    {
        internal static UpdateCheckResult UpToDate(string latestTag) => new(UpdateCheckStatus.UpToDate, null, latestTag);
        internal static UpdateCheckResult Available(UpdateRelease release) => new(UpdateCheckStatus.Available, release, null);
        internal static UpdateCheckResult Failed(string message) => new(UpdateCheckStatus.Failed, null, message);
    }
}
