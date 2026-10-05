using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The always-on startup check behind the setup panel's "Download instead of
/// building" offer: it reads the same recent-releases feed the download path
/// reads (so `nightly` and versioned tags are peers, newest wins), compares
/// the release's publish time against the executable on disk, and degrades to
/// "no update" on any failure — an offline start must be quiet and must never
/// throw. No test may depend on GitHub being up.
/// </summary>
public class SidecarUpdateCheckTests
{
    /// <summary>Local executable timestamp helpers: a file that exists only to
    /// carry a modification time.</summary>
    private static string LocalExe(DateTime lastWriteUtc)
    {
        var path = Path.Combine(Path.GetTempPath(), $"synapic-update-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, new byte[] { 1 });
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static FakeReleases GitHub(params (string Tag, string PublishedAt, string[] Assets)[] releases)
        => new(releases);

    [Fact]
    public async Task A_fresher_nightly_release_is_offered_over_an_older_tag()
    {
        var github = GitHub(
            ("v0.2.1", "2026-09-25T17:15:40Z", new[] { "SHA256SUMS.txt", "synapic-inference-win-x64.exe" }),
            ("nightly", "2026-10-05T07:29:16Z", new[] { "SHA256SUMS.txt", "synapic-inference-win-x64.exe" }));
        var local = LocalExe(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var info = await new SidecarDownloadService(new HttpClient(github))
            .CheckForUpdateAsync("win-x64", local);

        Assert.NotNull(info);
        Assert.Equal("nightly", info!.TagName);
        Assert.False(info.LocalMissing);
        Assert.True(info.TotalBytes > 0);
    }

    [Fact]
    public async Task A_local_build_newer_than_the_release_is_not_offered()
    {
        var github = GitHub(
            ("nightly", "2026-10-05T07:29:16Z", new[] { "SHA256SUMS.txt", "synapic-inference-win-x64.exe" }));
        var local = LocalExe(new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));   // built after the release

        var info = await new SidecarDownloadService(new HttpClient(github))
            .CheckForUpdateAsync("win-x64", local);

        Assert.Null(info);
    }

    [Fact]
    public async Task A_missing_local_executable_is_reported_as_available()
    {
        var github = GitHub(
            ("v0.2.1", "2026-09-25T17:15:40Z", new[] { "SHA256SUMS.txt", "synapic-inference-win-x64.exe" }));

        var info = await new SidecarDownloadService(new HttpClient(github))
            .CheckForUpdateAsync("win-x64", localExePath: null);

        Assert.NotNull(info);
        Assert.True(info!.LocalMissing);
        Assert.Equal("v0.2.1", info.TagName);
    }

    [Fact]
    public async Task A_rid_is_only_offered_from_a_release_that_actually_carries_it()
    {
        // nightly is newer but publishes CPU assets only; the CUDA bundle
        // ships with versioned releases - the CUDA row must still hear about
        // the newest release that has its parts.
        var github = GitHub(
            ("nightly", "2026-10-05T07:29:16Z", new[] { "SHA256SUMS.txt", "synapic-inference-win-x64.exe" }),
            ("v0.2.1", "2026-09-25T17:15:40Z", new[] { "SHA256SUMS.txt", "synapic-inference-win-x64-cuda.exe.part1" }));
        var local = LocalExe(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        var info = await new SidecarDownloadService(new HttpClient(github))
            .CheckForUpdateAsync("win-x64-cuda", local);

        Assert.NotNull(info);
        Assert.Equal("v0.2.1", info!.TagName);
    }

    [Fact]
    public async Task A_release_without_a_checksum_manifest_is_never_offered()
    {
        var github = GitHub(
            ("nightly", "2026-10-05T07:29:16Z", new[] { "synapic-inference-win-x64.exe" }));
        var local = LocalExe(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        var info = await new SidecarDownloadService(new HttpClient(github))
            .CheckForUpdateAsync("win-x64", local);

        Assert.Null(info);   // unverifiable bytes are not an offer
    }

    [Fact]
    public async Task An_unreachable_github_degrades_to_no_update_instead_of_throwing()
    {
        var info = await new SidecarDownloadService(new HttpClient(new OfflineHandler()))
            .CheckForUpdateAsync("win-x64", localExePath: null);

        Assert.Null(info);
    }

    [Fact]
    public async Task The_download_path_reads_the_same_release_feed()
    {
        // End-to-end through the newest-wins rule: the download must take the
        // nightly asset when nightly is the fresher manifest release, because
        // that is what the check offered.
        var github = GitHub(
            ("v0.2.1", "2026-09-25T17:15:40Z", new[] { "SHA256SUMS.txt", "synapic-inference-win-x64.exe" }),
            ("nightly", "2026-10-05T07:29:16Z", new[] { "SHA256SUMS.txt", "synapic-inference-win-x64.exe" }));
        github.SetAssetBytes("synapic-inference-win-x64.exe", Encoding.UTF8.GetBytes("fresh"));
        var destination = Path.Combine(Path.GetTempPath(), $"synapic-dl-{Guid.NewGuid():N}.exe");

        try
        {
            await new SidecarDownloadService(new HttpClient(github))
                .DownloadAsync("win-x64", destination, new ProgressLog());
            Assert.Equal("fresh", await File.ReadAllTextAsync(destination));
            Assert.Equal("nightly", github.LastListedTag);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    private sealed class ProgressLog : IProgress<SidecarDownloadProgress>
    {
        public void Report(SidecarDownloadProgress value) { }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }

    /// <summary>The releases list endpoint plus asset URLs shaped
    /// <c>/assets/&lt;tag&gt;/&lt;name&gt;</c>, so a test can see *which* release's
    /// bytes the download path actually took.</summary>
    private sealed class FakeReleases : HttpMessageHandler
    {
        private readonly (string Tag, string PublishedAt, string[] Assets)[] _releases;
        private readonly Dictionary<string, byte[]> _assetBytes = new();

        /// <summary>The tag whose assets were last requested (list + sums + bytes).</summary>
        public string? LastListedTag { get; private set; }

        public FakeReleases((string Tag, string PublishedAt, string[] Assets)[] releases) => _releases = releases;

        public void SetAssetBytes(string name, byte[] bytes) => _assetBytes[name] = bytes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (url.Contains("/releases?per_page", StringComparison.Ordinal))
            {
                LastListedTag = _releases.Length == 0 ? null : _releases[^1].Tag;
                var json = JsonSerializer.Serialize(_releases.Select(r => new
                {
                    tag_name = r.Tag,
                    published_at = r.PublishedAt,
                    assets = r.Assets.Select(name => new
                    {
                        name,
                        size = 4,
                        browser_download_url = $"https://example.test/assets/{r.Tag}/{name}",
                    }).ToArray(),
                }));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }

            var marker = url.IndexOf("/assets/", StringComparison.Ordinal);
            if (marker >= 0)
            {
                var rest = url[(marker + "/assets/".Length)..];
                var slash = rest.IndexOf('/');
                if (slash > 0)
                {
                    var tag = rest[..slash];
                    var name = rest[(slash + 1)..];
                    LastListedTag = tag;

                    if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        var manifest = string.Join("\n", _assetBytes.Select(kv =>
                            $"{Convert.ToHexString(SHA256.HashData(kv.Value)).ToLowerInvariant()}  {kv.Key}"));
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(manifest, Encoding.UTF8, "text/plain"),
                        });
                    }

                    if (_assetBytes.TryGetValue(name, out var bytes))
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(bytes),
                        });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
