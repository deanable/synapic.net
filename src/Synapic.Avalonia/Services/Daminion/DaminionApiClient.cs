using System.Net;
using System.Text.Json;
using Refit;

namespace Synapic.Avalonia.Services.Daminion;

/// <summary>Typed Daminion errors (ported from daminion_api.py exception hierarchy).</summary>
public abstract class DaminionException : Exception
{
    protected DaminionException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class DaminionAuthenticationException : DaminionException
{
    public DaminionAuthenticationException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class DaminionNetworkException : DaminionException
{
    public DaminionNetworkException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class DaminionRateLimitException : DaminionException
{
    public DaminionRateLimitException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// High-level Daminion client — port of ``daminion_client.py``: authentication,
/// filtered item fetch with pagination contract (one ≤500-item batch per call;
/// the caller advances the start index), metadata write via BatchChange tag
/// operations, and thumbnail/preview/original download to temp files.
/// </summary>
public sealed class DaminionApiClient
{
    public const int PageSize = 500;

    private readonly Func<IDaminionApi> _apiFactory;
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly string? _catalogId;
    private readonly SemaphoreSlim _rateLimit;
    private readonly Dictionary<string, string> _tagGuidMap = new(StringComparer.OrdinalIgnoreCase);

    private IDaminionApi? _api;
    private bool _authenticated;

    public DaminionApiClient(string baseUrl, string username, string password, string? catalogId = null, double rateLimitSeconds = 0.1)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;
        _catalogId = catalogId;
        _rateLimit = new SemaphoreSlim(1, 1);
        _apiFactory = () => RestService.For<IDaminionApi>(_baseUrl, BuildSettings());
    }

    private static RefitSettings BuildSettings() => new()
    {
        ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    public bool IsAuthenticated
    {
        get { lock (_tagGuidMap) return _authenticated; }
    }

    public string BaseUrl => _baseUrl;

    // ── Auth ────────────────────────────────────────────────────────────────

    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        var api = GetApi();
        try
        {
            using var resp = await api.Login(_username, _password, _catalogId).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                throw new DaminionAuthenticationException($"Authentication failed for '{_username}'");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new DaminionRateLimitException("Rate limited by Daminion server");
            if (!resp.IsSuccessStatusCode)
                throw new DaminionNetworkException($"Login failed with HTTP {(int)resp.StatusCode}");

            lock (_tagGuidMap)
            {
                _authenticated = true;
                _api = api;
            }

            await LoadTagSchemaAsync(ct).ConfigureAwait(false);
            SynapicLog.Info(nameof(DaminionApiClient), $"Successfully authenticated to {_baseUrl}");
        }
        catch (DaminionException)
        {
            throw;
        }
        catch (ApiException e)
        {
            throw new DaminionNetworkException($"Login failed: {e.Message}", e);
        }
        catch (HttpRequestException e)
        {
            throw new DaminionNetworkException($"Cannot reach Daminion server at {_baseUrl}: {e.Message}", e);
        }
    }

    private IDaminionApi GetApi()
    {
        lock (_tagGuidMap)
        {
            if (_api is not null && _authenticated) return _api;
        }
        return _apiFactory();
    }

    private async Task LoadTagSchemaAsync(CancellationToken ct)
    {
        try
        {
            var layout = await GetApi().GetDefaultLayout().ConfigureAwait(false);
            lock (_tagGuidMap)
            {
                _tagGuidMap.Clear();
                foreach (var element in layout)
                {
                    var name = element.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var guid = element.TryGetProperty("guid", out var g) ? g.GetString() : null;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(guid))
                        _tagGuidMap[name] = guid;
                }
            }
            SynapicLog.Info(nameof(DaminionApiClient), $"Loaded tag schema: {_tagGuidMap.Count} tags mapped");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DaminionApiClient), $"Failed to load tag schema (metadata writes may degrade): {e.Message}");
        }
    }

    private string? GetTagGuid(params string[] names)
    {
        lock (_tagGuidMap)
        {
            foreach (var name in names)
            {
                if (_tagGuidMap.TryGetValue(name, out var guid)) return guid;
                if (_tagGuidMap.TryGetValue(name.ToLowerInvariant(), out guid)) return guid;
            }
        }
        return null;
    }

    // ── Item fetch (single batch per call — caller paginates) ───────────────

    /// <summary>
    /// Retrieve one batch (≤ <see cref="PageSize"/>) of filtered items.
    /// Contract mirrors the original: at most one batch per call; callers
    /// advance <paramref name="startIndex"/> by the returned count until an
    /// empty or partial batch signals the end.
    /// </summary>
    public async Task<DaminionItem[]> GetItemsFilteredAsync(
        string scope = "all",
        int? savedSearchId = null,
        int? collectionId = null,
        string? searchTerm = null,
        string[]? untaggedFields = null,
        string statusFilter = "all",
        int maxItems = 500,
        int startIndex = 0,
        CancellationToken ct = default)
    {
        try
        {
            var filters = BuildFilterClauses(statusFilter, untaggedFields);
            var batch = maxItems > 0 && maxItems < PageSize ? maxItems : PageSize;
            DaminionItem[] items;

            switch (scope)
            {
                case "search" when !string.IsNullOrWhiteSpace(searchTerm):
                {
                    var query = $"\"{searchTerm}\"";
                    if (filters.Length > 0) query += " " + string.Join(" ", filters);
                    items = (await GetApi().GetItems(startIndex, batch, f: null, search: query, maxItemsCount: 100000).ConfigureAwait(false)).EffectiveItems;
                    break;
                }
                case "all":
                {
                    var query = filters.Length > 0 ? string.Join(" ", filters) : "*";
                    items = (await GetApi().GetItems(startIndex, batch, f: null, search: query, maxItemsCount: 100000).ConfigureAwait(false)).EffectiveItems;
                    break;
                }
                case "collection" when collectionId is not null:
                {
                    items = (await GetApi().GetCollectionItems(collectionId.Value, startIndex, batch).ConfigureAwait(false)).EffectiveItems;
                    if (statusFilter != "all" || (untaggedFields?.Length ?? 0) > 0)
                        items = items.Where(i => PassesFilters(i, statusFilter, untaggedFields)).ToArray();
                    break;
                }
                case "saved_search" when savedSearchId is not null:
                {
                    // Structured query: "tagId,valueId" + "tagId,any" operators,
                    // mirroring the original saved-search lookup (tag id 39).
                    var tagId = 39;
                    var f = new[] { $"{tagId},any" };
                    items = (await GetApi().GetItems(startIndex, batch, f: f, queryLine: $"{tagId},{savedSearchId}", maxItemsCount: 100000).ConfigureAwait(false)).EffectiveItems;
                    if (statusFilter != "all" || (untaggedFields?.Length ?? 0) > 0)
                        items = items.Where(i => PassesFilters(i, statusFilter, untaggedFields)).ToArray();
                    break;
                }
                default:
                    throw new DaminionNetworkException($"Unsupported scope '{scope}' or missing scope id");
            }

            return maxItems > 0 ? items.Take(maxItems).ToArray() : items;
        }
        catch (DaminionException)
        {
            throw;
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(DaminionApiClient), $"Failed to retrieve filtered items: {e.Message}");
            throw new DaminionNetworkException($"Failed to retrieve filtered items: {e.Message}", e);
        }
    }

    private static string[] BuildFilterClauses(string statusFilter, string[]? untaggedFields)
    {
        var parts = new List<string>();
        switch ((statusFilter ?? "all").ToLowerInvariant())
        {
            case "approved": parts.Add("flag:flagged"); break;
            case "rejected": parts.Add("flag:rejected"); break;
            case "unassigned": parts.Add("flag:unflagged"); break;
        }
        foreach (var field in untaggedFields ?? Array.Empty<string>())
        {
            var name = field.Equals("category", StringComparison.OrdinalIgnoreCase) ? "Categories" : field;
            parts.Add($"{name}:none");
        }
        return parts.ToArray();
    }

    private static bool PassesFilters(DaminionItem item, string statusFilter, string[]? untaggedFields)
    {
        var sf = (statusFilter ?? "all").ToLowerInvariant();
        if (sf != "all" && item.Flag is { } flag)
        {
            var fv = flag.ToString().ToLowerInvariant();
            if (sf == "approved" && fv is not ("1" or "approved" or "flagged")) return false;
            if (sf == "rejected" && fv is not ("2" or "rejected")) return false;
            if (sf == "unassigned" && fv is not ("0" or "unassigned" or "unflagged")) return false;
        }

        if (untaggedFields is not null && untaggedFields.Any(f => f.Trim().Equals("keywords", StringComparison.OrdinalIgnoreCase)))
        {
            if (item.Keywords is { Length: > 0 }) return false;
        }
        if (untaggedFields is not null && untaggedFields.Any(f => f.Trim().Equals("categories", StringComparison.OrdinalIgnoreCase)))
        {
            if (item.Categories is { Length: > 0 }) return false;
        }
        return true;
    }

    // ── Image download (temp files; caller cleans up) ───────────────────────

    public async Task<string?> DownloadThumbnailAsync(int itemId, int width = 200, int height = 200, CancellationToken ct = default)
        => await DownloadToTempAsync(itemId, $"thumb_{width}x{height}", () => GetApi().GetThumbnail(itemId, width, height), ct).ConfigureAwait(false);

    public async Task<string?> DownloadPreviewAsync(int itemId, int width = 1000, int? height = null, CancellationToken ct = default)
    {
        int targetH;
        if (height is { } explicitH)
        {
            targetH = explicitH;
        }
        else
        {
            var dims = await GetItemDimensionsAsync(itemId, ct).ConfigureAwait(false);
            targetH = dims is { } d && d.H > 0 ? (int)(d.H * (double)width / d.W) : width;
        }
        return await DownloadToTempAsync(itemId, $"preview_{width}x{targetH}", () => GetApi().GetPreview(itemId, width, targetH), ct).ConfigureAwait(false);
    }

    public async Task<string?> DownloadOriginalAsync(int itemId, CancellationToken ct = default)
        => await DownloadToTempAsync(itemId, "original", () => GetApi().GetOriginal(itemId), ct).ConfigureAwait(false);

    private async Task<string?> DownloadToTempAsync(int itemId, string kind, Func<Task<Stream>> fetch, CancellationToken ct)
    {
        try
        {
            using var stream = await fetch().ConfigureAwait(false);
            if (stream is null) return null;

            var tempDir = Path.Combine(Path.GetTempPath(), "synapic_daminion");
            Directory.CreateDirectory(tempDir);
            var tempFile = Path.Combine(tempDir, $"{itemId}_{kind}");

            await using var fs = File.Create(tempFile);
            await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
            return tempFile;
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(DaminionApiClient), $"Failed to download {kind} for item {itemId}: {e.Message}");
            return null;
        }
    }

    public async Task<(int W, int H)?> GetItemDimensionsAsync(int itemId, CancellationToken ct = default)
    {
        try
        {
            var ids = itemId.ToString();
            var resp = await GetApi().GetItemsByIds(ids).ConfigureAwait(false);
            var item = resp.EffectiveItems.FirstOrDefault();
            return item?.Dimensions;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── Metadata write (daminion_client.update_item_metadata port) ──────────

    /// <summary>
    /// Write category/keywords/description to a Daminion item via
    /// /api/ItemData/BatchChange tag operations (value-id resolution not yet
    /// cached; falls back to raw values which Daminion resolves server-side).
    /// </summary>
    public async Task<bool> UpdateItemMetadataAsync(
        int itemId,
        string? category = null,
        IReadOnlyList<string>? keywords = null,
        string? description = null,
        CancellationToken ct = default)
    {
        var operations = new List<DaminionTagOperation>();

        if (!string.IsNullOrEmpty(category))
        {
            var guid = GetTagGuid("category", "categories", "Categories");
            if (guid is not null)
                operations.Add(new DaminionTagOperation { Guid = guid, Value = category, Remove = false });
        }

        if (keywords is { Count: > 0 })
        {
            var guid = GetTagGuid("keywords", "Keywords");
            if (guid is not null)
            {
                foreach (var keyword in keywords)
                    operations.Add(new DaminionTagOperation { Guid = guid, Value = keyword, Remove = false });
            }
        }

        if (!string.IsNullOrEmpty(description))
        {
            var guid = GetTagGuid("description", "caption", "Description");
            if (guid is not null)
                operations.Add(new DaminionTagOperation { Guid = guid, Value = description, Remove = false });
        }

        if (operations.Count == 0)
        {
            SynapicLog.Warning(nameof(DaminionApiClient), $"No metadata operations for item {itemId} (tag schema missing?)");
            return false;
        }

        try
        {
            await GetApi().BatchChange(new DaminionBatchChangeRequest
            {
                Ids = new[] { itemId },
                Data = operations.ToArray(),
                Delete = false,
            }).ConfigureAwait(false);
            SynapicLog.Info(nameof(DaminionApiClient), $"Updated metadata for item {itemId}");
            return true;
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(DaminionApiClient), $"Failed to update metadata for item {itemId}: {e.Message}");
            return false;
        }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await GetApi().Logout().ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
        lock (_tagGuidMap)
        {
            _authenticated = false;
            _api = null;
        }
    }
}
