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
    /// <summary>What the update button needs from <see cref="UpdateService"/>.</summary>
    public interface IUpdater
    {
        Version? CurrentVersion { get; }
        bool IsInstallation { get; }
        Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
        Task<string> DownloadAsync(UpdateRelease release, CancellationToken cancellationToken = default);
        void StartInstaller(string setupPath, string sha256);
    }

    /// <summary>
    /// Updates on a click, from this repository's GitHub Releases.
    ///
    /// Nothing is checked by itself. "Check for updates" in Gloam's window asks GitHub for the
    /// repository's latest release (<see cref="UpdateRepo"/>; a draft or a prerelease is never
    /// "latest") and compares its vX.Y.Z tag with the running version. A newer one is offered
    /// with its notes, and Install downloads its Gloam-X.Y.Z-setup.exe, checks it against the
    /// SHA-256 GitHub itself publishes for the asset (its "digest"; a release without one is
    /// not offered), and runs it silently. The setup (installer\Gloam.iss) waits for this
    /// process to exit, puts the new version's files into the installation's folder and starts
    /// Gloam again. The folder never moves, so the Start with Windows registration and the
    /// Start menu shortcut never need repointing. See RELEASE.md.
    ///
    /// The setup is fetched only from under <see cref="DownloadUrlPrefix"/>, whatever the
    /// answer names. It sits in the data folder, where the user's programs can write, so it is
    /// hashed again through a handle that denies writers, and started while that handle is
    /// still open: what runs is what was checked.
    ///
    /// Only an INSTALLATION updates itself: a folder the setup installed, which it marks with
    /// its uninstaller beside Gloam.exe. A dev run (F5, dotnet run) can check, and is told to
    /// install the release instead.
    ///
    /// The request sends no token, so the repository must be public. A private one answers
    /// 404, which is reported as such rather than as "there is no release".
    /// </summary>
    public class UpdateService : IUpdater
    {
        /// <summary>
        /// The repository releases come from. publish.ps1 refuses to publish anywhere else,
        /// because a mismatch would publish releases nothing finds.
        /// </summary>
        internal const string UpdateRepo = "dkozlov-ua/gloam";
        internal const string LatestReleaseUrl = "https://api.github.com/repos/" + UpdateRepo + "/releases/latest";
        internal const string DownloadUrlPrefix = "https://github.com/" + UpdateRepo + "/releases/download/";
        internal const string ReleasesPageUrl = "https://github.com/" + UpdateRepo + "/releases";

        // What the setup leaves beside Gloam.exe: the mark of an installation. Inno Setup
        // numbers it past 000 only when another app's uninstaller shares the folder, which
        // the setup's own fixed folder never does.
        internal const string UninstallerFileName = "unins000.exe";

        // Inno Setup's own command line, plus /relaunch=1, which the setup reads as "start
        // Gloam afterwards": by the time it looks, this process may already be gone, so it
        // cannot tell from the single-instance mutex that Gloam was running. /SILENT rather
        // than /VERYSILENT: the user just clicked Install, and its progress window says the
        // update is happening while the tray icon is gone.
        internal const string RelaunchSwitch = "/relaunch=1";

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
        /// True when Gloam runs from a folder the setup installed, so it can update itself.
        /// Also the gate for taking over the Start with Windows registration: a dev run must
        /// never steal it from the real installation.
        /// </summary>
        public bool IsInstallation => File.Exists(Path.Combine(_appDir, UninstallerFileName));

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

        internal static string PackageFileName(Version version) => $"Gloam-{version.ToString(3)}-setup.exe";

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
        /// Downloads the release's setup into the data folder's Updates directory and
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

        /// <summary>The setup's own log of an update, beside the download.</summary>
        internal static string SetupLogPath => Path.Combine(UpdatesDir, "Gloam-setup.log");

        /// <summary>
        /// Runs a downloaded setup silently over this installation. The setup waits for this
        /// process to exit, so the caller shuts the app down right after. Throws, starting
        /// nothing, outside an installation or when the file no longer matches
        /// <paramref name="sha256"/>.
        /// </summary>
        public void StartInstaller(string setupPath, string sha256)
        {
            if (!IsInstallation)
                throw new InvalidOperationException($"{_appDir} is not an installation; it cannot update itself.");

            // Held, denying writers, from the hash until the setup has started: nothing can
            // swap the file in between. Starting an exe only reads it, so the share allows it.
            using (var held = new FileStream(setupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string actual = Convert.ToHexString(SHA256.HashData(held)).ToLowerInvariant();
                if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{setupPath} has SHA-256 {actual}, not {sha256}; it was not run.");

                var psi = new ProcessStartInfo(setupPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(setupPath)!,
                };
                foreach (string arg in SetupArguments(SetupLogPath))
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException($"{setupPath} did not start.");
                Log.Info($"UpdateService: started {setupPath} (pid {process.Id}); its log is {SetupLogPath}.");
            }
        }

        internal static string[] SetupArguments(string logPath) =>
        [
            "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
            RelaunchSwitch,
            "/LOG=" + logPath,
        ];

        /// <summary>
        /// Deletes setups an earlier update downloaded. The setup that just updated this
        /// installation may still be running (it starts Gloam before it exits) and is left for
        /// the next start; anything that cannot be deleted is.
        /// </summary>
        public static void DeleteDownloadedSetups()
        {
            try
            {
                if (!Directory.Exists(UpdatesDir)) return;
                foreach (string file in Directory.EnumerateFiles(UpdatesDir, "Gloam-*-setup.exe*"))
                {
                    try { File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static Version? RunningVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? null : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>A release that can be installed: its setup, where it is and its SHA-256.</summary>
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
