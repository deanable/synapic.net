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

    /// <summary>
    /// Tag id used when "Saved Searches" cannot be resolved by name (observed
    /// id on Daminion Server 11.0.0.3906; the earlier 39 was stale).
    /// </summary>
    public const int SavedSearchesTagFallbackId = 40;

    private readonly Func<IDaminionApi> _apiFactory;
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly string? _catalogId;
    private readonly SemaphoreSlim _rateLimit;
    private readonly Dictionary<string, string> _tagGuidMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _tagIdMap = new(StringComparer.OrdinalIgnoreCase);

    private IDaminionApi? _api;
    private bool _authenticated;

    public DaminionApiClient(string baseUrl, string username, string password, string? catalogId = null, double rateLimitSeconds = 0.1)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;
        _catalogId = catalogId;
        _rateLimit = new SemaphoreSlim(1, 1);
        // Own handler with an explicit cookie container (session persistence)
        // and a long timeout: original-file downloads over slow LAN links
        // otherwise die at HttpClient's 100 s default ("A task was canceled").
        _apiFactory = () => RestService.For<IDaminionApi>(CreateHttpClient(), BuildSettings());
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromMinutes(15),
        };
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

    /// <summary>Diagnostics: number of tag GUIDs mapped from GetDefaultLayout.</summary>
    public int MappedTagGuidCount
    {
        get { lock (_tagGuidMap) return _tagGuidMap.Count; }
    }

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
                foreach (var (name, guid) in ExtractLayoutTagPairs(layout))
                    _tagGuidMap[name] = guid;
            }
            SynapicLog.Info(nameof(DaminionApiClient), $"Loaded tag schema: {_tagGuidMap.Count} tags mapped");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DaminionApiClient), $"Failed to load tag schema (metadata writes may degrade): {e.Message}");
        }

        // Tag *ids* drive the saved-search scope and counts (Settings/GetTags).
        try
        {
            var tags = await GetApi().GetAllTags().ConfigureAwait(false);
            var entries = UnwrapCollection(tags, "tags", "items", "data");
            lock (_tagIdMap)
            {
                _tagIdMap.Clear();
                foreach (var element in entries)
                {
                    var name = element.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var id = element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                        ? idEl.GetInt32() : (int?)null;
                    if (!string.IsNullOrEmpty(name) && id is > 0)
                        _tagIdMap[name] = id.Value;
                }
            }
            SynapicLog.Info(nameof(DaminionApiClient), $"Loaded tag ids: {_tagIdMap.Count} tags");
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DaminionApiClient), $"Failed to load tag ids (saved searches fall back to id {SavedSearchesTagFallbackId}): {e.Message}");
        }
    }

    /// <summary>
    /// Extract (name, guid) pairs from a GetDefaultLayout payload. Server 11.x
    /// wraps entries in nested "properties" arrays and keys them
    /// propertyName/propertyGuid; older builds returned a flat list keyed
    /// name/guid. Walk the structure recursively and accept both conventions.
    /// </summary>
    public static IEnumerable<(string Name, string Guid)> ExtractLayoutTagPairs(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    foreach (var pair in ExtractLayoutTagPairs(child))
                        yield return pair;
                break;
            case JsonValueKind.Object:
            {
                var name = GetStringProperty(element, "name") ?? GetStringProperty(element, "propertyName");
                var guid = GetStringProperty(element, "guid") ?? GetStringProperty(element, "propertyGuid");
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(guid))
                    yield return (name, guid!);
                foreach (var child in element.EnumerateObject())
                    if (child.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                        foreach (var pair in ExtractLayoutTagPairs(child.Value))
                            yield return pair;
                break;
            }
        }
    }

    private static string? GetStringProperty(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Resolve a tag's numeric id by name ("saved searches", "flag"…), with a fallback.</summary>
    /// <remarks>
    /// Fallback 40 observed on Daminion Server 11.0.0.3906 (damserver.local);
    /// the previous 39 was stale for that build.
    /// </remarks>
    private async Task<int> GetTagIdAsync(string tagName, int fallback)
    {
        lock (_tagIdMap)
        {
            if (_tagIdMap.TryGetValue(tagName, out var id)) return id;
            if (_tagIdMap.TryGetValue(tagName.ToLowerInvariant(), out id)) return id;
        }
        try
        {
            var tags = await GetApi().GetAllTags().ConfigureAwait(false);
            foreach (var element in UnwrapCollection(tags, "tags", "items", "data"))
            {
                var name = element.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name) || !name.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                {
                    var id = idEl.GetInt32();
                    lock (_tagIdMap) _tagIdMap[name] = id;
                    return id;
                }
            }
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DaminionApiClient), $"Tag id lookup for '{tagName}' failed: {e.Message}");
        }
        return tagName.Equals("saved searches", StringComparison.OrdinalIgnoreCase) ? SavedSearchesTagFallbackId : fallback;
    }

    /// <summary>Unwrap a JSON payload that may be an array or wrapped in one of several container keys.</summary>
    private static JsonElement[] UnwrapCollection(JsonElement json, params string[] containerKeys)
    {
        if (json.ValueKind == JsonValueKind.Array)
            return json.EnumerateArray().ToArray();
        if (json.ValueKind != JsonValueKind.Object)
            return Array.Empty<JsonElement>();
        foreach (var key in containerKeys)
        {
            if (json.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Array)
                return inner.EnumerateArray().ToArray();
        }
        return Array.Empty<JsonElement>();
    }

    private static int? GetInt(JsonElement e, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (e.ValueKind != JsonValueKind.Object) break;
            if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
        }
        return null;
    }

    private static string? GetString(JsonElement e, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (e.ValueKind != JsonValueKind.Object) break;
            if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
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
                    // Python parity: /api/SharedCollection/GetItems?id=… —
                    // the /api/Collections/GetItems/{id} variant 404s on
                    // server 11.0.0.3906 (damserver.local).
                    items = (await GetApi().GetSharedCollectionItems(collectionId.Value, startIndex, batch).ConfigureAwait(false)).EffectiveItems;
                    if (statusFilter != "all" || (untaggedFields?.Length ?? 0) > 0)
                        items = items.Where(i => PassesFilters(i, statusFilter, untaggedFields)).ToArray();
                    break;
                }
                case "saved_search" when savedSearchId is not null:
                {
                    // Structured query: "tagId,valueId" + "tagId,any" operators,
                    // mirroring the original saved-search lookup ("Saved Searches" tag).
                    var tagId = await GetTagIdAsync("saved searches", fallback: 39).ConfigureAwait(false);
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
            var name = NormalizeUntaggedField(field);
            parts.Add($"{name}:none");
        }
        return parts.ToArray();
    }

    /// <summary>Daminion's search query uses the plural display names (daminion_client normalization port).</summary>
    private static string NormalizeUntaggedField(string field) => field.Trim().ToLowerInvariant() switch
    {
        "category" or "categories" => "Categories",
        "keyword" or "keywords" => "Keywords",
        "description" => "Description",
        _ => field,
    };

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

        if (untaggedFields is not null &&
            untaggedFields.Any(f => f.Trim().ToLowerInvariant() is "keywords" or "keyword"))
        {
            if (item.Keywords is { Length: > 0 }) return false;
        }
        if (untaggedFields is not null &&
            untaggedFields.Any(f => f.Trim().ToLowerInvariant() is "categories" or "category"))
        {
            if (item.Categories is { Length: > 0 }) return false;
        }
        if (untaggedFields is not null &&
            untaggedFields.Any(f => f.Trim().Equals("description", StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(item.Description)) return false;
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

    // ── Saved searches / collections / item counts (Step 1 pickers) ────────

    /// <summary>
    /// Saved searches via the "Saved Searches" indexed tag's values
    /// (daminion_client.get_saved_searches port — no dedicated endpoint).
    /// Some server builds (observed on 11.0.0.3906) break the
    /// IndexedTagValues enumeration route; there we fall back to discovering
    /// existing searches with structured per-id queries, which the same
    /// build honors. Names are unavailable in that path, so entries are
    /// synthesized as "Saved Search #id".
    /// </summary>
    public async Task<IReadOnlyList<DaminionSavedSearch>> GetSavedSearchesAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetSavedSearchesCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DaminionApiClient),
                $"Saved-search enumeration unavailable on this server ({e.Message}) — trying structured-query discovery");
        }

        try
        {
            return await DiscoverSavedSearchesByQuerySweepAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DaminionApiClient),
                $"Saved-search discovery failed ({e.Message}) — continuing without the saved-search scope");
            return Array.Empty<DaminionSavedSearch>();
        }
    }

    private async Task<IReadOnlyList<DaminionSavedSearch>> GetSavedSearchesCoreAsync(CancellationToken ct)
    {
        var tagId = await GetTagIdAsync("saved searches", fallback: SavedSearchesTagFallbackId).ConfigureAwait(false);
        var json = await GetApi().GetIndexedTagValues(tagId).ConfigureAwait(false);
        var result = new List<DaminionSavedSearch>();
        foreach (var value in UnwrapCollection(json, "values", "items", "data"))
        {
            var id = GetInt(value, "id", "valueId") ?? 0;
            var text = GetString(value, "text", "value", "name") ?? "";
            if (id > 0 && text.Length > 0)
                result.Add(new DaminionSavedSearch(id, text, GetInt(value, "count") ?? 0));
        }
        SynapicLog.Info(nameof(DaminionApiClient), $"Retrieved {result.Count} saved searches (tag id {tagId})");
        return result;
    }

    /// <summary>
    /// Fallback for servers whose IndexedTagValues enumeration is broken:
    /// probe candidate value ids with structured queries (queryLine=tagId,N
    /// + operators) — the same mechanism the saved-search FETCH scope uses —
    /// and keep the ids that match at least one item. Capped at a fixed
    /// probe range; names cannot be resolved on such builds.
    /// </summary>
    private const int SavedSearchDiscoveryMaxId = 50;

    private async Task<IReadOnlyList<DaminionSavedSearch>> DiscoverSavedSearchesByQuerySweepAsync(CancellationToken ct)
    {
        var tagId = await GetTagIdAsync("saved searches", fallback: SavedSearchesTagFallbackId).ConfigureAwait(false);
        var found = new List<DaminionSavedSearch>();

        for (var candidateId = 1; candidateId <= SavedSearchDiscoveryMaxId; candidateId++)
        {
            ct.ThrowIfCancellationRequested();
            var resp = await GetApi().GetItems(
                index: 0,
                size: 1,
                f: new[] { $"{tagId},any" },
                queryLine: $"{tagId},{candidateId}",
                maxItemsCount: 100000).ConfigureAwait(false);

            var count = resp.TotalCount ?? resp.EffectiveItems.Length;
            if (count > 0)
                found.Add(new DaminionSavedSearch(candidateId, $"Saved Search #{candidateId}", count));
        }

        SynapicLog.Info(nameof(DaminionApiClient),
            $"Saved-search discovery: {found.Count} searches found via query sweep (tag id {tagId}, ids {string.Join(",", found.Select(s => s.Id))})");
        return found;
    }

    /// <summary>Shared collections list (daminion_client.get_shared_collections port).</summary>
    public async Task<IReadOnlyList<DaminionCollection>> GetSharedCollectionsAsync(CancellationToken ct = default)
    {
        var json = await GetApi().GetCollections().ConfigureAwait(false);
        var result = new List<DaminionCollection>();
        foreach (var coll in UnwrapCollection(json, "collections", "items", "data"))
        {
            var id = GetInt(coll, "id") ?? 0;
            var name = GetString(coll, "name", "title") ?? "";
            if (id > 0)
                result.Add(new DaminionCollection(
                    id, name, GetString(coll, "code") ?? "", GetInt(coll, "itemCount", "count") ?? 0));
        }
        SynapicLog.Info(nameof(DaminionApiClient), $"Retrieved {result.Count} shared collections");
        return result;
    }

    /// <summary>
    /// Count items matching the Step 1 filters (daminion_client.get_filtered_item_count port).
    /// Returns -1 on failure (Python parity — callers must not treat it as a real count).
    /// </summary>
    /// <remarks>
    /// Mirrors the original's structure: status filters become structured
    /// flag-tag clauses (approved=2, rejected=3, unassigned=1 — NOT the
    /// flag:flagged text queries the /tag fetch path uses); untagged fields
    /// and the search term form a text query; collection scope resolves the
    /// "shared collections" tag (fallback 46); a count that equals the whole
    /// catalog despite active filters is re-derived from a 1-item search; a
    /// zero text-search count falls back to the keyword value id.
    /// </remarks>
    public async Task<int> GetFilteredItemCountAsync(
        string scope = "all",
        int? savedSearchId = null,
        int? collectionId = null,
        string? searchTerm = null,
        string[]? untaggedFields = null,
        string statusFilter = "all",
        CancellationToken ct = default)
    {
        try
        {
            var qParts = new List<string>();
            var fParts = new List<string>();
            var searchParts = new List<string>();

            // Status filter via the flag tag (structured query; Python parity).
            var sf = (statusFilter ?? "all").ToLowerInvariant();
            if (sf != "all")
            {
                var flagTagId = await GetTagIdAsync("flag", fallback: 41).ConfigureAwait(false);
                var flagValue = sf switch
                {
                    "approved" => 2,
                    "rejected" => 3,
                    "unassigned" => 1,
                    _ => (int?)null,
                };
                if (flagValue is { } fv)
                {
                    qParts.Add($"{flagTagId},{fv}");
                    fParts.Add($"{flagTagId},any");
                }
            }

            // Untagged fields: text-based 'Tag:none' clauses.
            foreach (var field in untaggedFields ?? Array.Empty<string>())
                searchParts.Add($"{NormalizeUntaggedField(field)}:none");

            // Keyword search term (quoted phrase).
            if (scope == "search" && !string.IsNullOrWhiteSpace(searchTerm))
                searchParts.Add($"\"{searchTerm}\"");

            var combinedSearch = searchParts.Count > 0 ? string.Join(" ", searchParts) : null;

            // Scope-specific structured clauses.
            if (scope == "saved_search" && savedSearchId is not null)
            {
                var tagId = await GetTagIdAsync("saved searches", fallback: SavedSearchesTagFallbackId).ConfigureAwait(false);
                qParts.Add($"{tagId},{savedSearchId}");
                fParts.Add($"{tagId},any");
            }
            else if (scope == "collection" && collectionId is not null)
            {
                var tagId = await GetCollectionTagIdAsync().ConfigureAwait(false);
                qParts.Add($"{tagId},{collectionId}");
                fParts.Add($"{tagId},any");
            }

            var queryLine = qParts.Count > 0 ? string.Join(";", qParts) : null;
            var operators = fParts.Count > 0 ? string.Join(";", fParts) : null;

            var count = (await GetApi().GetCount(
                search: combinedSearch,
                queryLine: queryLine,
                f: operators,
                force: "false").ConfigureAwait(false)).EffectiveCount;

            // Sanity check (Python parity): a count that matches the whole
            // catalog size despite active filters/scope is suspect — re-derive
            // the total from a 1-item search whose totalCount the API reports
            // accurately (up to maxItemsCount).
            if ((combinedSearch is not null || queryLine is not null) && count > 0)
            {
                var totalCatalog = (await GetApi().GetCount().ConfigureAwait(false)).EffectiveCount;
                if (count >= totalCatalog)
                {
                    SynapicLog.Warning(nameof(DaminionApiClient),
                        $"Count {count} matches total catalog size despite filters — falling back to a 1-item search");
                    count = (await GetApi().GetItems(
                        index: 0,
                        size: 1,
                        f: ParseQueryOperators(operators),
                        search: combinedSearch,
                        queryLine: queryLine,
                        maxItemsCount: 100000).ConfigureAwait(false)).TotalCount ?? 0;
                }
            }

            // Structured fallback when a text search returned zero (Python
            // parity): match the keyword tag value id instead.
            if (count <= 0 && scope == "search" && !string.IsNullOrWhiteSpace(searchTerm))
            {
                var kwId = await GetTagIdAsync("keywords", fallback: 0).ConfigureAwait(false);
                if (kwId > 0)
                {
                    var valueId = await FindTagValueIdAsync(kwId, searchTerm).ConfigureAwait(false);
                    if (valueId is { } vid)
                    {
                        count = (await GetApi().GetCount(
                            queryLine: $"{kwId},{vid}",
                            f: $"{kwId},any").ConfigureAwait(false)).EffectiveCount;
                    }
                }
            }

            SynapicLog.Info(nameof(DaminionApiClient),
                $"Item count: {count} | scope: {scope} | query: '{combinedSearch}' | queryLine: '{queryLine}'");
            return count;
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(DaminionApiClient), $"Failed to get filtered count: {e.Message}");
            return -1; // Python parity: failure sentinel, not a real count.
        }
    }

    /// <summary>Resolve the "shared collections" tag id (fallback 46, Python parity).</summary>
    private async Task<int> GetCollectionTagIdAsync()
    {
        var id = await GetTagIdAsync("shared collections", fallback: 0).ConfigureAwait(false);
        if (id > 0) return id;
        id = await GetTagIdAsync("collections", fallback: 0).ConfigureAwait(false);
        return id > 0 ? id : 46;
    }

    /// <summary>Split a ';'-joined operator string for the multi-value f= parameter.</summary>
    private static string[]? ParseQueryOperators(string? operators) =>
        string.IsNullOrEmpty(operators) ? null : operators.Split(';');

    /// <summary>
    /// Find a tag value's id by exact text (daminion_api.find_tag_values port,
    /// including the /api/IndexedTagValues fallback route some builds serve).
    /// </summary>
    private async Task<int?> FindTagValueIdAsync(int tagId, string filterText)
    {
        JsonElement json;
        try
        {
            json = await GetApi().GetIndexedTagValues(tagId, filter: filterText, pageSize: 100).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // Fallback route (daminion_api.py parity): some builds only expose
            // the bare /api/IndexedTagValues endpoint.
            json = await GetApi().GetIndexedTagValuesFallback(tagId, filter: filterText, pageSize: 100).ConfigureAwait(false);
        }

        foreach (var value in UnwrapCollection(json, "values", "items", "data"))
        {
            var text = GetString(value, "text", "value", "name", "title") ?? "";
            if (text.Equals(filterText, StringComparison.OrdinalIgnoreCase))
                return GetInt(value, "id", "valueId");
        }
        return null;
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

    /// <summary>Remove keywords from an item (BatchChange Remove=true) — used to undo test writes and by dedup tagging.</summary>
    public async Task<bool> RemoveKeywordsAsync(int itemId, IEnumerable<string> keywords, CancellationToken ct = default)
    {
        var guid = GetTagGuid("keywords", "Keywords");
        if (guid is null)
        {
            SynapicLog.Warning(nameof(DaminionApiClient), $"Cannot remove keywords from item {itemId} (keywords tag schema missing)");
            return false;
        }

        try
        {
            await GetApi().BatchChange(new DaminionBatchChangeRequest
            {
                Ids = new[] { itemId },
                Data = keywords.Select(k => new DaminionTagOperation { Guid = guid, Value = k, Remove = true }).ToArray(),
                Delete = false,
            }).ConfigureAwait(false);
            SynapicLog.Info(nameof(DaminionApiClient), $"Removed keywords from item {itemId}");
            return true;
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(DaminionApiClient), $"Failed to remove keywords from item {itemId}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Re-read an item via /api/ItemData/GetAll/{id} and check the expected
    /// tags landed (Step 4 “Verify Daminion writes”). Field matching is
    /// tolerant: tag keys are located case-insensitively anywhere in the
    /// payload since Daminion's layout varies between versions.
    /// </summary>
    public async Task<DaminionVerifyResult> VerifyItemMetadataAsync(
        int itemId,
        string? category = null,
        IReadOnlyList<string>? keywords = null,
        string? description = null,
        CancellationToken ct = default)
    {
        try
        {
            var payload = await GetApi().GetItemDataAll(itemId).ConfigureAwait(false);
            var found = new List<(string Key, string Value)>();
            CollectKeyValuePairs(payload, found);

            var missing = new List<string>();

            if (!string.IsNullOrEmpty(category) &&
                !found.Any(kv => kv.Key.Contains("categor", StringComparison.OrdinalIgnoreCase) &&
                                 kv.Value.Contains(category, StringComparison.OrdinalIgnoreCase)))
            {
                missing.Add($"category '{category}'");
            }

            if (keywords is { Count: > 0 })
            {
                var stored = found
                    .Where(kv => kv.Key.Contains("keyword", StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var absent = keywords.Where(k => !stored.Contains(k)).ToList();
                if (absent.Count > 0)
                    missing.Add($"keywords {string.Join(", ", absent.Select(k => $"'{k}'"))}");
            }

            if (!string.IsNullOrEmpty(description) &&
                !found.Any(kv =>
                    (kv.Key.Contains("description", StringComparison.OrdinalIgnoreCase) ||
                     kv.Key.Contains("caption", StringComparison.OrdinalIgnoreCase)) &&
                    kv.Value.Contains(description[..Math.Min(description.Length, 40)], StringComparison.OrdinalIgnoreCase)))
            {
                missing.Add("description");
            }

            return missing.Count == 0
                ? new DaminionVerifyResult(true, $"Item {itemId} verified")
                : new DaminionVerifyResult(false, $"Item {itemId}: not found — {string.Join("; ", missing)}");
        }
        catch (Exception e)
        {
            return new DaminionVerifyResult(false, $"Item {itemId}: verify failed — {e.Message}");
        }
    }

    private static void CollectKeyValuePairs(JsonElement node, List<(string Key, string Value)> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        found.Add((property.Name, property.Value.ToString()));
                    CollectKeyValuePairs(property.Value, found);
                }
                break;
            case JsonValueKind.Array:
                foreach (var element in node.EnumerateArray())
                    CollectKeyValuePairs(element, found);
                break;
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
