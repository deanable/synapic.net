using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synapic.Avalonia.Services;

/// <summary>Progress of a sidecar download, reported as bytes land.</summary>
public sealed record SidecarDownloadProgress(double Percent, string Stage);

/// <summary>
/// Fetches the prebuilt sidecar for a RID from the GitHub release instead of
/// compiling it from source. The release already publishes the standalone
/// executable - split into parts when it is over GitHub's 2 GiB asset cap - so
/// a machine with no Python/PyInstaller toolchain gets the same bytes the Build
/// button would have produced, and an installed app can pick up the variant it
/// did not ship with.
/// </summary>
public interface ISidecarDownloadService
{
    /// <summary>
    /// Downloads every asset the latest release published for
    /// <paramref name="rid"/> and writes it to <paramref name="destinationPath"/>
    /// (parts are joined in order). Throws a message meant for the log panel
    /// when the release carries no such asset.
    /// </summary>
    Task DownloadAsync(
        string rid,
        string destinationPath,
        IProgress<SidecarDownloadProgress> progress,
        CancellationToken ct = default);
}

public sealed class SidecarDownloadService : ISidecarDownloadService
{
    private const string AssetPrefix = "synapic-inference-";
    private const int BufferSize = 81920;

    private readonly HttpClient _http;

    public SidecarDownloadService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        // GitHub answers anonymous API calls only when a User-Agent is present.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Synapic-SidecarDownload");
    }

    public async Task DownloadAsync(
        string rid,
        string destinationPath,
        IProgress<SidecarDownloadProgress> progress,
        CancellationToken ct = default)
    {
        // SHA256SUMS.txt ships with every release (release.yml generates it);
        // every downloaded (part) asset below is verified against it before the
        // move puts the executable where detection finds it. The file is not
        // optional: HTTPS protects the pipe, not the bytes, and this download
        // becomes code the app executes.
        var (assets, shaSums) = await ResolveAssetsAsync(rid, ct).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Staged next to the destination and moved into place at the very end,
        // so a half-downloaded executable can never be one detection finds.
        var stagingPath = destinationPath + ".download";
        var total = assets.Sum(a => a.Size);
        var buffer = new byte[BufferSize];
        long written = 0;

        try
        {
            await using (var staging = new FileStream(
                stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                for (var i = 0; i < assets.Count; i++)
                {
                    var asset = assets[i];
                    var stage = assets.Count == 1
                        ? $"Downloading {asset.Name}"
                        : $"Downloading part {i + 1} of {assets.Count}";

                    using var response = await _http
                        .GetAsync(asset.Url!, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"Downloading {asset.Name} failed: HTTP {(int)response.StatusCode}.");
                    }

                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var lastPercent = -1;
                    await using var remote = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    int read;
                    while ((read = await remote.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await staging.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        hasher.AppendData(buffer, 0, read);
                        written += read;

                        // The progress bar posts one message per report, so only
                        // report when the whole-number percent actually moves.
                        var percent = Percent(written, total);
                        if ((int)percent != lastPercent)
                        {
                            lastPercent = (int)percent;
                            progress.Report(new(percent, stage));
                        }
                    }

                    // Fail before the move on any integrity problem: the
                    // destination stays exactly as it was.
                    VerifySha256Hex(shaSums, asset.Name!, hasher.GetHashAndReset());
                }
            }

            progress.Report(new(100, "Installing"));
            File.Move(stagingPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }
    }

    /// <summary>Assets of the latest release that belong to this RID (join order) plus the release manifest text.</summary>
    private async Task<(IReadOnlyList<GitHubAsset> Assets, string ShaSums)> ResolveAssetsAsync(string rid, CancellationToken ct)
    {
        GitHubRelease? release;
        try
        {
            release = await _http
                .GetFromJsonAsync<GitHubRelease>(UpdateCheckService.LatestReleaseApiUrl, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException($"Could not read the latest release from GitHub: {e.Message}", e);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"The GitHub release response was not readable: {e.Message}", e);
        }

        var wanted = AssetPrefix + rid;
        var matches = (release?.Assets ?? Array.Empty<GitHubAsset>())
            .Where(a => a.Name is not null && a.Url is not null && IsAssetFor(a.Name, wanted))
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"The latest release ({release?.TagName ?? "an unknown tag"}) has no prebuilt sidecar for '{rid}'. " +
                "Build it instead, or see the release page for the platforms it does cover.");
        }

        // A release carries either one executable or its parts, never both; if it
        // ever carries both, the whole file wins over joining it to its parts.
        var whole = matches.FirstOrDefault(a => !IsPart(a.Name!));
        IReadOnlyList<GitHubAsset> assets = whole is not null
            ? new[] { whole }
            : matches.OrderBy(a => PartNumber(a.Name!)).ToList();

        var shaAsset = (release?.Assets ?? Array.Empty<GitHubAsset>())
            .FirstOrDefault(a => "SHA256SUMS.txt".Equals(a.Name, StringComparison.OrdinalIgnoreCase) && a.Url is not null);
        if (shaAsset is null)
            throw new InvalidOperationException(
                $"The latest release ({release?.TagName ?? "an unknown tag"}) does not publish SHA256SUMS.txt — " +
                "the download cannot be verified, so it is refused. Build the sidecar locally instead (Build Server), " +
                "or pick a release that ships checksums.");

        return (assets, await _http.GetStringAsync(shaAsset.Url!, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Verify one downloaded asset's SHA-256 against the release manifest
    /// (sha256sum format: "<hex>[ *]<name>"). Throws with the expectation and
    /// actual values on mismatch, or a manifest-entry-missing message.
    /// </summary>
    internal static void VerifySha256Hex(string shaSums, string assetName, byte[] actualHash)
    {
        var expected = FindExpectedSha256(shaSums, assetName)
            ?? throw new InvalidOperationException(
                $"SHA256SUMS.txt has no entry for '{assetName}' — refusing to install an unverified sidecar.");
        var actual = Convert.ToHexString(actualHash).ToLowerInvariant();
        if (actual != expected)
            throw new InvalidOperationException(
                $"Checksum mismatch for {assetName}: expected {expected}, got {actual}. " +
                "The download or the release is corrupted — retry, or rebuild the sidecar locally (Build Server).");
    }

    /// <summary>The expected lowercase hex SHA-256 of an asset from SHA256SUMS.txt; null when absent.</summary>
    internal static string? FindExpectedSha256(string shaSums, string assetName)
    {
        foreach (var rawLine in shaSums.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var hash = line[..space].Trim();
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) continue;
            var name = line[(space + 1)..].TrimStart('*').Trim();
            if (name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// True for <c>synapic-inference-&lt;rid&gt;</c> and its parts. The RIDs are
    /// prefix-related - <c>win-x64</c> and <c>win-x64-cuda</c> - so matching is
    /// on the whole extension boundary, never on the RID alone.
    /// </summary>
    private static bool IsAssetFor(string name, string wanted) =>
        name == wanted || name == wanted + ".exe"
        || name.StartsWith(wanted + ".part", StringComparison.Ordinal)
        || name.StartsWith(wanted + ".exe.part", StringComparison.Ordinal);

    private static bool IsPart(string name) => name.Contains(".part", StringComparison.Ordinal);

    private static int PartNumber(string name)
    {
        var marker = name.LastIndexOf(".part", StringComparison.Ordinal);
        return marker >= 0 && int.TryParse(name[(marker + 5)..], out var number) ? number : 0;
    }

    private static double Percent(long written, long total) =>
        total > 0 ? Math.Min(100d, written * 100d / total) : 0d;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(SidecarDownloadService), $"Could not remove the partial download {path}: {e.Message}");
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? Url { get; init; }
    }
}
