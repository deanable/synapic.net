using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Synapic.Shared;
using Synapic.Shared.Contracts;

namespace Synapic.Avalonia.Services;

/// <summary>
/// HTTP client for the sidecar API (spec §4.2). /tag uses a 5-minute timeout
/// with one retry on 503 (model loading).
/// </summary>
public sealed class InferenceApiClient
{
    private static readonly TimeSpan TagTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// First upscale also downloads the Swin2SR weights, and a large image on
    /// CPU can run for minutes — the original app had no timeout at all, so the
    /// bar sits far above /tag's 5 minutes and cancellation still comes from ct.
    /// </summary>
    private static readonly TimeSpan UpscaleTimeout = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = SynapicJsonContext.Default,
    };

    private readonly HttpClient _http;

    public InferenceApiClient(HttpClient httpClient)
    {
        _http = httpClient;
    }

    public async Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("health", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var health = await resp.Content.ReadFromJsonAsync(SynapicJsonContext.Default.HealthResponse, ct).ConfigureAwait(false);
        return health ?? new HealthResponse();
    }

    public async Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("models/list", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var models = await resp.Content.ReadFromJsonAsync(SynapicJsonContext.Default.ModelInfoArray, ct).ConfigureAwait(false);
        return models ?? Array.Empty<ModelInfo>();
    }

    public async Task DownloadModelAsync(string modelId, string revision = "main", CancellationToken ct = default)
    {
        var body = new DownloadRequest { ModelId = modelId, Revision = revision };
        using var resp = await _http.PostAsJsonAsync("models/download", body, SynapicJsonContext.Default.DownloadRequest, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted)
            return;
        var detail = await ReadDetailAsync(resp, ct).ConfigureAwait(false);
        throw new InferenceApiException((int)resp.StatusCode, detail);
    }

    public async Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TagTimeout);

        using var resp = await _http.PostAsJsonAsync("tag", request, SynapicJsonContext.Default.TagRequest, timeoutCts.Token).ConfigureAwait(false);

        // Single retry when the model is still loading (spec §5.3).
        if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            using var retry = await _http.PostAsJsonAsync("tag", request, SynapicJsonContext.Default.TagRequest, timeoutCts.Token).ConfigureAwait(false);
            return await ReadTagResponseAsync(retry, ct).ConfigureAwait(false);
        }

        return await ReadTagResponseAsync(resp, ct).ConfigureAwait(false);
    }

    private static async Task<TagResponse> ReadTagResponseAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
        {
            var tag = await resp.Content.ReadFromJsonAsync(SynapicJsonContext.Default.TagResponse, ct).ConfigureAwait(false);
            return tag ?? new TagResponse();
        }
        var detail = await ReadDetailAsync(resp, ct).ConfigureAwait(false);
        throw new InferenceApiException((int)resp.StatusCode, detail);
    }

    /// <summary>
    /// POST /upscale: run the sidecar's Swin2SR/Lanczos upscale on one image.
    /// No 503-retry: unlike /tag the sidecar loads the SR model inline, so a
    /// slow first call is a long wait, not a transient rejection.
    /// </summary>
    public async Task<UpscaleResponse> UpscaleAsync(UpscaleRequest request, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UpscaleTimeout);

        using var resp = await _http.PostAsJsonAsync("upscale", request, SynapicJsonContext.Default.UpscaleRequest, timeoutCts.Token).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            var result = await resp.Content.ReadFromJsonAsync(SynapicJsonContext.Default.UpscaleResponse, ct).ConfigureAwait(false);
            return result ?? new UpscaleResponse();
        }
        var error = await ReadDetailAsync(resp, ct).ConfigureAwait(false);
        throw new InferenceApiException((int)resp.StatusCode, error);
    }

    /// <summary>
    /// The sidecar's built-in tag instruction, so Step 2 can seed the editable
    /// box with the shipped wording instead of shipping a copy of it.
    /// </summary>
    public async Task<PromptDefaultsDto> GetPromptDefaultsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("prompt", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var prompt = await resp.Content.ReadFromJsonAsync(SynapicJsonContext.Default.PromptDefaultsDto, ct).ConfigureAwait(false);
        return prompt ?? new PromptDefaultsDto();
    }

    public async Task<ConfigDto> GetConfigAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("config", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var config = await resp.Content.ReadFromJsonAsync(SynapicJsonContext.Default.ConfigDto, ct).ConfigureAwait(false);
        return config ?? new ConfigDto();
    }

    public async Task<ConfigDto> UpdateConfigAsync(ConfigDto config, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync("config", config, SynapicJsonContext.Default.ConfigDto, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var updated = await resp.Content.ReadFromJsonAsync(SynapicJsonContext.Default.ConfigDto, ct).ConfigureAwait(false);
        return updated ?? config;
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("shutdown", content: null, ct).ConfigureAwait(false);
        // Best effort: 404/connection-refused after exit are acceptable.
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.ToString();
            return json;
        }
        catch
        {
            return $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
        }
    }
}

/// <summary>Sidecar API error carrying the HTTP status and detail message.</summary>
public sealed class InferenceApiException : Exception
{
    public int StatusCode { get; }

    public InferenceApiException(int statusCode, string detail)
        : base($"Sidecar API error {(statusCode)}: {detail}")
    {
        StatusCode = statusCode;
    }
}
