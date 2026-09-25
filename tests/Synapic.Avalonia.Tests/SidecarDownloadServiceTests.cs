using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Synapic.Avalonia.Services;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The Download button is the way out for a machine that cannot compile the
/// sidecar, so the service has to be exact about *which* release asset it
/// takes: <c>win-x64</c> and <c>win-x64-cuda</c> are prefix-related, the CUDA
/// bundle ships as parts that must be joined in part order, and a failure must
/// never leave something detection would mistake for a finished executable.
/// Exercised against a fake release - no test may depend on GitHub being up.
/// </summary>
public class SidecarDownloadServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "synapic-download-" + Guid.NewGuid().ToString("N"));

    public SidecarDownloadServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static SidecarDownloadService Service(HttpMessageHandler handler) =>
        new(new HttpClient(handler));

    [Fact]
    public async Task Downloads_the_single_asset_into_place()
    {
        var payload = Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray();
        var github = new FakeGitHub();
        github.Release(("synapic-inference-win-x64.exe", payload));
        var destination = Path.Combine(_dir, "artifacts", "win-x64", "synapic-inference.exe");

        await Service(github).DownloadAsync("win-x64", destination, new ProgressLog());

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".download"), "the staging file must be gone");
    }

    [Fact]
    public async Task Joins_parts_in_order_whatever_order_the_release_lists_them()
    {
        var github = new FakeGitHub();
        // Deliberately backwards: the API lists assets in upload order, and the
        // reassembly helper's own contract is part-number order.
        github.Release(
            ("synapic-inference-win-x64-cuda.exe.part2", Encoding.UTF8.GetBytes("second")),
            ("synapic-inference-win-x64-cuda.exe.part1", Encoding.UTF8.GetBytes("first")));
        var destination = Path.Combine(_dir, "synapic-inference.exe");

        await Service(github).DownloadAsync("win-x64-cuda", destination, new ProgressLog());

        Assert.Equal("firstsecond", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Picking_win_x64_never_touches_the_cuda_assets()
    {
        var github = new FakeGitHub();
        github.Release(
            ("synapic-inference-win-x64.exe", Encoding.UTF8.GetBytes("cpu")),
            ("synapic-inference-win-x64-cuda.exe.part1", Encoding.UTF8.GetBytes("cuda")));
        var destination = Path.Combine(_dir, "synapic-inference.exe");

        await Service(github).DownloadAsync("win-x64", destination, new ProgressLog());

        Assert.Equal("cpu", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task A_release_without_the_rid_says_so_naming_the_release()
    {
        var github = new FakeGitHub { Tag = "v9.9.9" };
        github.Release(("synapic-inference-win-x64-cuda.exe.part1", new byte[] { 1 }));
        var destination = Path.Combine(_dir, "synapic-inference.exe");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(github).DownloadAsync("win-x64", destination, new ProgressLog()));

        Assert.Contains("win-x64", error.Message);
        Assert.Contains("v9.9.9", error.Message);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task A_failed_part_leaves_neither_the_staging_file_nor_the_destination()
    {
        var github = new FakeGitHub();
        github.Release(
            ("synapic-inference-win-x64.exe.part1", new byte[64]),
            ("synapic-inference-win-x64.exe.part2", new byte[64]));
        github.FailOn("synapic-inference-win-x64.exe.part2", status: 500);
        var destination = Path.Combine(_dir, "synapic-inference.exe");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(github).DownloadAsync("win-x64", destination, new ProgressLog()));

        Assert.Contains("HTTP 500", error.Message);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".download"));
    }

    [Fact]
    public async Task Progress_ends_at_100()
    {
        var github = new FakeGitHub();
        github.Release(("synapic-inference-win-x64.exe", new byte[200_000]));
        var destination = Path.Combine(_dir, "synapic-inference.exe");
        var log = new ProgressLog();

        await Service(github).DownloadAsync("win-x64", destination, log);

        Assert.NotEmpty(log.Reports);
        Assert.Equal(100d, log.Reports[^1].Percent);
        Assert.Equal("Installing", log.Reports[^1].Stage);
    }

    private sealed class ProgressLog : IProgress<SidecarDownloadProgress>
    {
        public List<SidecarDownloadProgress> Reports { get; } = new();

        public void Report(SidecarDownloadProgress value) => Reports.Add(value);
    }

    /// <summary>The releases API plus the asset URLs it hands out.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly List<(string Name, byte[] Bytes, int Status)> _assets = new();

        public string Tag { get; set; } = "v0.0.0";

        public void Release(params (string Name, byte[] Bytes)[] assets)
        {
            _assets.Clear();
            foreach (var (name, bytes) in assets) _assets.Add((name, bytes, 200));
        }

        public void FailOn(string name, int status)
        {
            for (var i = 0; i < _assets.Count; i++)
            {
                if (_assets[i].Name == name) _assets[i] = (name, _assets[i].Bytes, status);
            }
        }

        private static string UrlFor(string name) => $"https://example.test/assets/{name}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (url.Contains("releases/latest", StringComparison.Ordinal))
            {
                var json = JsonSerializer.Serialize(new
                {
                    tag_name = Tag,
                    assets = _assets.Select(a => new
                    {
                        name = a.Name,
                        size = a.Bytes.Length,
                        browser_download_url = UrlFor(a.Name),
                    }).ToArray(),
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }

            var index = _assets.FindIndex(a => UrlFor(a.Name) == url);
            if (index < 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var (name, bytes, status) = _assets[index];
            return Task.FromResult(status == 200
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage((HttpStatusCode)status));
        }
    }
}
