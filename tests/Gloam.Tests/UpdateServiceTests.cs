using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gloam.Core;
using Gloam.Services;
using Xunit;

namespace Gloam.Tests
{
    public sealed class UpdateServiceTests : IDisposable
    {
        private const string Prefix = UpdateService.DownloadUrlPrefix;

        private readonly string _originalDataDir;
        private readonly string _originalRoamingDir;
        private readonly string _tempDir;

        public UpdateServiceTests()
        {
            _originalDataDir = AppPaths.DataDir;
            _originalRoamingDir = AppPaths.RoamingDataDir;
            _tempDir = Path.Combine(Path.GetTempPath(), "GloamUpdateServiceTests", Guid.NewGuid().ToString("N"));
            AppPaths.UseDataDirectoriesForCurrentProcess(Path.Combine(_tempDir, "data"), Path.Combine(_tempDir, "roaming"));
        }

        public void Dispose()
        {
            AppPaths.UseDataDirectoriesForCurrentProcess(_originalDataDir, _originalRoamingDir);
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        }

        private static readonly string ValidSha = new('a', 64);

        private static string Release(string tag, params object[] assets)
            => JsonSerializer.Serialize(new { tag_name = tag, body = "Notes for " + tag, assets });

        private static object Asset(string name, string? url = null, string? digest = "", long size = 10)
            => new { name, browser_download_url = url ?? Prefix + "v0/" + name, digest, size };

        private static object Package(string version, string? sha = null, string? url = null)
            => Asset($"Gloam-{version}.zip", url ?? $"{Prefix}v{version}/Gloam-{version}.zip",
                     sha == null ? "sha256:" + ValidSha : sha);

        [Fact]
        public void Evaluate_NewerReleaseWithDigest_IsOffered()
        {
            var result = UpdateService.Evaluate(Release("v1.11.0", Package("1.11.0")), new Version(1, 10, 8));

            Assert.Equal(UpdateCheckStatus.Available, result.Status);
            var release = Assert.IsType<UpdateRelease>(result.Release);
            Assert.Equal(new Version(1, 11, 0), release.Version);
            Assert.Equal("v1.11.0", release.Tag);
            Assert.Equal($"{Prefix}v1.11.0/Gloam-1.11.0.zip", release.DownloadUrl);
            Assert.Equal(ValidSha, release.Sha256);
            Assert.Equal("Notes for v1.11.0", release.Notes);
        }

        [Fact]
        public void Evaluate_DigestIsNormalisedToLowerCase()
        {
            var result = UpdateService.Evaluate(
                Release("v2.0.0", Package("2.0.0", sha: "sha256:" + new string('B', 64))), new Version(1, 0, 0));

            Assert.Equal(new string('b', 64), result.Release!.Sha256);
        }

        [Theory]
        [InlineData("v1.10.8")]
        [InlineData("v1.9.99")]
        [InlineData("v0.0.1")]
        public void Evaluate_SameOrOlderRelease_IsUpToDate(string tag)
        {
            var result = UpdateService.Evaluate(Release(tag, Package(tag[1..])), new Version(1, 10, 8));

            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
            Assert.Null(result.Release);
            Assert.Equal(tag, result.Message);
        }

        [Fact]
        public void Evaluate_OlderReleaseWithoutPackage_IsStillUpToDate()
        {
            // What the latest release carries only matters when it would be installed.
            var result = UpdateService.Evaluate(Release("v1.0.0"), new Version(1, 10, 8));

            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        }

        [Fact]
        public void Evaluate_VersionsCompareNumerically()
        {
            var result = UpdateService.Evaluate(Release("v1.10.0", Package("1.10.0")), new Version(1, 9, 3));

            Assert.Equal(UpdateCheckStatus.Available, result.Status);
        }

        [Theory]
        [InlineData("1.11.0")]
        [InlineData("v1.11")]
        [InlineData("v1.11.0-beta")]
        [InlineData("release-1.11.0")]
        public void Evaluate_TagThatIsNotVxyz_Fails(string tag)
        {
            var result = UpdateService.Evaluate(Release(tag, Package("1.11.0")), new Version(1, 10, 8));

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Null(result.Release);
        }

        [Fact]
        public void Evaluate_ReleaseWithoutDigest_IsNotOffered()
        {
            var result = UpdateService.Evaluate(
                Release("v1.11.0", Asset("Gloam-1.11.0.zip", $"{Prefix}v1.11.0/Gloam-1.11.0.zip", digest: null)),
                new Version(1, 10, 8));

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Contains("SHA-256", result.Message);
        }

        [Fact]
        public void Evaluate_MalformedDigest_IsNotOffered()
        {
            var result = UpdateService.Evaluate(
                Release("v1.11.0", Package("1.11.0", sha: "sha1:" + new string('a', 40))), new Version(1, 10, 8));

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        }

        [Theory]
        [InlineData("https://github.com/someone-else/gloam/releases/download/v1.11.0/Gloam-1.11.0.zip")]
        [InlineData("https://example.com/Gloam-1.11.0.zip")]
        [InlineData("http://github.com/dkozlov-ua/gloam/releases/download/v1.11.0/Gloam-1.11.0.zip")]
        public void Evaluate_PackageFromElsewhere_IsNotOffered(string url)
        {
            var result = UpdateService.Evaluate(Release("v1.11.0", Package("1.11.0", url: url)), new Version(1, 10, 8));

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        }

        [Fact]
        public void Evaluate_OnlyTheVersionsOwnZipCounts()
        {
            // The Velopack-era assets and a zip of another version are not the package.
            var result = UpdateService.Evaluate(
                Release("v1.11.0",
                    Asset("GloamApp-win-Portable.zip", digest: "sha256:" + ValidSha),
                    Package("1.10.9")),
                new Version(1, 10, 8));

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Contains("Gloam-1.11.0.zip", result.Message);
        }

        [Fact]
        public void Evaluate_UnreadableAnswer_Fails()
        {
            Assert.Equal(UpdateCheckStatus.Failed, UpdateService.Evaluate("<html>", new Version(1, 0, 0)).Status);
            Assert.Equal(UpdateCheckStatus.Failed, UpdateService.Evaluate("[]", new Version(1, 0, 0)).Status);
        }

        [Fact]
        public async Task CheckAsync_AsksForTheRepositorysLatestRelease()
        {
            var handler = new FakeHandler(_ => Json(Release("v1.11.0", Package("1.11.0"))));
            var service = Service(handler);

            var result = await service.CheckAsync();

            Assert.Equal(UpdateCheckStatus.Available, result.Status);
            var request = Assert.Single(handler.Requests);
            Assert.Equal(UpdateService.LatestReleaseUrl, request.ToString());
        }

        [Fact]
        public async Task CheckAsync_NotFound_SaysPrivateOrNoRelease()
        {
            var service = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

            var result = await service.CheckAsync();

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Contains("private", result.Message);
        }

        [Fact]
        public async Task CheckAsync_NetworkFailure_IsAFailedCheckNotAnException()
        {
            var service = Service(new FakeHandler(_ => throw new HttpRequestException("no route")));

            var result = await service.CheckAsync();

            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Contains("no route", result.Message);
        }

        [Fact]
        public async Task DownloadAsync_MatchingHash_ReturnsTheCheckedPackage()
        {
            byte[] payload = Encoding.UTF8.GetBytes("package bytes");
            var release = ReleaseFor(payload);
            var service = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));

            string path = await service.DownloadAsync(release);

            Assert.Equal(Path.Combine(AppPaths.DataDir, "Updates", "Gloam-1.11.0.zip"), path);
            Assert.Equal(payload, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(UpdateService.UpdatesDir));
        }

        [Fact]
        public async Task DownloadAsync_HashMismatch_ThrowsAndLeavesNothing()
        {
            var release = ReleaseFor(Encoding.UTF8.GetBytes("what was published"));
            var service = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("something else"))
            }));

            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release));

            Assert.Empty(Directory.GetFiles(UpdateService.UpdatesDir));
        }

        [Fact]
        public async Task DownloadAsync_RemovesWhatAnEarlierAttemptLeft()
        {
            Directory.CreateDirectory(UpdateService.UpdatesDir);
            string stale = Path.Combine(UpdateService.UpdatesDir, "Gloam-1.10.9.zip");
            File.WriteAllText(stale, "old");
            byte[] payload = Encoding.UTF8.GetBytes("new");
            var service = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));

            await service.DownloadAsync(ReleaseFor(payload));

            Assert.False(File.Exists(stale));
        }

        [Fact]
        public async Task DownloadAsync_RefusesAUrlOutsideTheRepository()
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var release = ReleaseFor(Array.Empty<byte>()) with { DownloadUrl = "https://example.com/Gloam-1.11.0.zip" };

            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler).DownloadAsync(release));

            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void IsInstallation_NeedsManifestAndInstallerBesideTheExe()
        {
            string dir = Path.Combine(_tempDir, "app");
            Directory.CreateDirectory(dir);
            var service = new UpdateService(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException())), dir, null);

            Assert.False(service.IsInstallation);
            File.WriteAllText(Path.Combine(dir, "manifest.txt"), "");
            Assert.False(service.IsInstallation);
            File.WriteAllText(Path.Combine(dir, "install.ps1"), "");
            Assert.True(service.IsInstallation);
        }

        [Fact]
        public void StartInstaller_RefusesOutsideAnInstallation()
        {
            string dir = Path.Combine(_tempDir, "checkout");
            Directory.CreateDirectory(dir);
            var service = new UpdateService(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException())), dir, null);

            Assert.Throws<InvalidOperationException>(() => service.StartInstaller("x.zip", ValidSha));
        }

        [Fact]
        public void InstallerArguments_RunTheInstallationsOwnScriptOnTheCheckedPackage()
        {
            string[] args = UpdateService.InstallerArguments(@"C:\Apps\Gloam", @"C:\Data\Updates\Gloam-1.11.0.zip", ValidSha, 4242);

            Assert.Equal(@"C:\Apps\Gloam\install.ps1", args[Array.IndexOf(args, "-File") + 1]);
            Assert.Equal(@"C:\Data\Updates\Gloam-1.11.0.zip", args[Array.IndexOf(args, "-Package") + 1]);
            Assert.Equal(ValidSha, args[Array.IndexOf(args, "-Sha256") + 1]);
            Assert.Equal("4242", args[Array.IndexOf(args, "-WaitPid") + 1]);
            Assert.Contains("-Launch", args);
        }

        [Fact]
        public void DisplayVersion_IsThreePart()
        {
            var service = new UpdateService(new HttpClient(new FakeHandler(_ => throw new InvalidOperationException())), _tempDir, new Version(1, 10, 8));

            Assert.Equal("v1.10.8", service.DisplayVersion);
        }

        private UpdateService Service(FakeHandler handler)
            => new(UpdateService.CreateClient(handler), _tempDir, new Version(1, 10, 8));

        private static UpdateRelease ReleaseFor(byte[] payload)
            => new(new Version(1, 11, 0), "v1.11.0", "",
                   $"{Prefix}v1.11.0/Gloam-1.11.0.zip",
                   Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                   payload.Length);

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

            public List<Uri> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri!);
                return Task.FromResult(_respond(request));
            }
        }
    }
}
