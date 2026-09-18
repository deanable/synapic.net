using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Synapic.Avalonia.Services;

/// <summary>Result of an update check against GitHub Releases.</summary>
public sealed record UpdateCheckResult(bool UpdateAvailable, string? LatestVersion, string? DownloadUrl, string? Notes);

/// <summary>
/// Auto-update check via the GitHub Releases API (spec P5.3, port of
/// src/utils/version_check.py semantics). Non-blocking, never throws.
/// </summary>
public sealed class UpdateCheckService
{
    private const string RepoApiUrl = "https://api.github.com/repos/deanable/Synapic.NET/releases/latest";

    private readonly HttpClient _http;

    public UpdateCheckService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Synapic-UpdateCheck");
    }

    /// <summary>Compare against a semver-ish current version ("1.0.0").</summary>
    public async Task<UpdateCheckResult?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var release = await _http.GetFromJsonAsync<GitHubRelease>(RepoApiUrl, ct).ConfigureAwait(false);
            if (release?.TagName is null) return null;

            var latest = release.TagName.TrimStart('v', 'V');
            if (!IsNewer(latest, currentVersion))
                return new UpdateCheckResult(false, latest, null, null);

            var assetUrl = release.Assets?
                .FirstOrDefault(a => a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
                    || a.Name?.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase) == true
                    || a.Name?.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) == true)
                ?.BrowserDownloadUrl;

            return new UpdateCheckResult(true, latest, assetUrl ?? release.HtmlUrl, release.Body);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(UpdateCheckService), $"Update check failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Numeric semver comparison ("1.2.10" > "1.2.9").</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var v1 = Parse(candidate);
        var v2 = Parse(current);
        for (var i = 0; i < 3; i++)
        {
            if (v1[i] != v2[i]) return v1[i] > v2[i];
        }
        return false;
    }

    private static int[] Parse(string version)
    {
        var parts = version.Split('-')[0].Split('.');
        var result = new[] { 0, 0, 0 };
        for (var i = 0; i < Math.Min(3, parts.Length); i++)
        {
            if (int.TryParse(parts[i], out var n)) result[i] = n;
        }
        return result;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}
