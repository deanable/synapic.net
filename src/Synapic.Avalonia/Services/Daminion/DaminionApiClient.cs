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

    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly string _tempDirectory;
    private readonly Dictionary<string, string> _tagGuidMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _tagIdMap = new(StringComparer.OrdinalIgnoreCase);

    private readonly IDaminionApi _api;
    private bool _authenticated;
    private readonly object _stateLock = new();

    public DaminionApiClient(string baseUrl, string username, string password, double rateLimitSeconds = 0.1,
        string? tempDirectory = null)
    {
        _baseUrl = NormalizeBaseUrl(baseUrl);
        _username = username;
        _password = password;
        _tempDirectory = tempDirectory ?? DefaultTempDirectory;

        // One owned client (the Refit proxy) for this object's lifetime. A
        // throwaway per call would (a) leak an undisposed SocketsHttpHandler
        // pool each time and (b) drop the login cookie, which is what replays
        // the server session onto every call. The long timeout is deliberate:
        // original-file downloads over slow LAN links otherwise die at
        // HttpClient's 100 s default ("A task was canceled").
        var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        _api = RestService.For<IDaminionApi>(new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromMinutes(15),
        }, BuildSettings());
    }

    /// <summary>
    /// Accept scheme-less host input ("damserver.local", "192.168.1.10:8080")
    /// by defaulting to http://, trimming whitespace/trailing slashes, and
    /// validating the result. Without this, <see cref="Uri"/> throws
    /// "Invalid URI: The format of the URI could not be determined." at
    /// connect time for the most natural user input.
    /// </summary>
    public static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (trimmed.Length == 0)
            throw new ArgumentException("Daminion server URL is empty", nameof(baseUrl));
        if (!trimmed.Contains("://"))
            trimmed = "http://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new ArgumentException(
                $"'{baseUrl}' is not a valid Daminion server URL (use e.g. http://damserver.local or damserver.local:8080)",
                nameof(baseUrl));
        return trimmed.TrimEnd('/');
    }

    private static RefitSettings BuildSettings() => new()
    {
        ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    // ── On-disk downloads (streamed, never cached) ────────────────────────

    /// <summary>
    /// Where originals, previews and thumbnails land while they are being
    /// fetched: one file at a time, deleted by the caller as soon as it has
    /// been hashed, read or rendered. This is <em>not</em> a cache — Synapic
    /// keeps no downloaded image between scans, and previews are decoded
    /// straight into memory.
    /// </summary>
    public static string DefaultTempDirectory => Path.Combine(Path.GetTempPath(), "synapic_daminion");

    /// <summary>This client's download directory (overridable for tests).</summary>
    public string TempDirectory => _tempDirectory;

    /// <summary>Downloads older than this are considered leftovers of a crash
    /// and are swept. The HTTP client times out after 15 minutes, so nothing
    /// still being written can ever be this old — a second app instance's
    /// in-flight download is safe.</summary>
    public static readonly TimeSpan StaleDownloadAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Delete downloads a crash or a power cut left behind. Called at startup
    /// and before every dedup scan, because a scan is the one flow that pulls
    /// whole originals (a leak there is megabytes per item, not kilobytes).
    /// Returns how many files and how many bytes went away, for the log.
    /// </summary>
    public static (int Files, long Bytes) CleanupStaleDownloads(
        TimeSpan? olderThan = null, string? directory = null)
    {
        var dir = directory ?? DefaultTempDirectory;
        if (!Directory.Exists(dir)) return (0, 0);

        var cutoff = DateTime.UtcNow - (olderThan ?? StaleDownloadAge);
        var files = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > cutoff) continue; // still in use / fresh
                    var length = info.Length;
                    info.Delete();
                    files++;
                    bytes += length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Locked by a running scan — leave it for the next sweep.
                    SynapicLog.Debug(nameof(DaminionApiClient), $"Stale download kept (in use): {file}");
                }
            }
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DaminionApiClient), $"Could not sweep '{dir}': {e.Message}");
        }
        return (files, bytes);
    }

    /// <summary>Current occupancy of the download directory — reported after a
    /// scan so a non-zero value (a leak) is visible in the log.</summary>
    public static (int Files, long Bytes) TempDirectoryUsage(string? directory = null)
    {
        var dir = directory ?? DefaultTempDirectory;
        if (!Directory.Exists(dir)) return (0, 0);
        var files = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try { var info = new FileInfo(file); files++; bytes += info.Length; }
                catch (Exception) { /* racing delete */ }
            }
        }
        catch (Exception) { /* directory vanished mid-sweep */ }
        return (files, bytes);
    }

    public bool IsAuthenticated
    {
        get { lock (_stateLock) return _authenticated; }
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
            using var resp = await api.Login(_username, _password, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                throw new DaminionAuthenticationException($"Authentication failed for '{_username}'");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new DaminionRateLimitException("Rate limited by Daminion server");
            if (!resp.IsSuccessStatusCode)
                throw new DaminionNetworkException($"Login failed with HTTP {(int)resp.StatusCode}");

            lock (_stateLock) _authenticated = true;

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

    /// <summary>The one owned Refit proxy; server-session state lives in its cookie container.</summary>
    private IDaminionApi GetApi() => _api;

    /// <summary>
    /// Run one authenticated API call, recovering from an expired server
    /// session: the cookie expires while a long batch runs, and a 401/403 used
    /// to fail every remaining item. On one of those statuses, re-authenticate
    /// once and replay the call against the refreshed session. Other failures
    /// propagate untouched.
    /// </summary>
    private async Task<T> WithSessionRecoveryAsync<T>(Func<IDaminionApi, Task<T>> call, CancellationToken ct)
    {
        var api = GetApi();
        try
        {
            return await call(api).ConfigureAwait(false);
        }
        catch (ApiException e) when ((int)e.StatusCode is 401 or 403)
        {
            SynapicLog.Warning(nameof(DaminionApiClient),
                $"Daminion session expired (HTTP {(int)e.StatusCode}) — re-authenticating and retrying once");
            await ReAuthenticateAsync(ct).ConfigureAwait(false);
            return await call(GetApi()).ConfigureAwait(false);
        }
    }

    /// <summary>Log out + log back in (LoadTagSchemaAsync refresh comes along).</summary>
    private async Task ReAuthenticateAsync(CancellationToken ct)
    {
        lock (_stateLock) _authenticated = false;
        await AuthenticateAsync(ct).ConfigureAwait(false);
    }

    private async Task LoadTagSchemaAsync(CancellationToken ct)
    {
        try
        {
            var layout = await GetApi().GetDefaultLayout(ct).ConfigureAwait(false);
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
            var tags = await GetApi().GetAllTags(ct).ConfigureAwait(false);
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
    private async Task<int> GetTagIdAsync(string tagName, int fallback, CancellationToken ct = default)
    {
        lock (_tagIdMap)
        {
            if (_tagIdMap.TryGetValue(tagName, out var id)) return id;
            if (_tagIdMap.TryGetValue(tagName.ToLowerInvariant(), out id)) return id;
        }
        try
        {
            var tags = await GetApi().GetAllTags(ct).ConfigureAwait(false);
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
    /// empty or partial batch signals the end. Session-expiry recovery (one
    /// re-auth + replay on 401/403) lives on the public path — the fetch is
    /// the longest call in a batch, so a session that dies mid-run would
    /// otherwise fail every remaining item.
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
        => await WithSessionRecoveryAsync(api => GetItemsFilteredCoreAsync(
            api,
            scope, savedSearchId, collectionId, searchTerm,
            untaggedFields, statusFilter, maxItems, startIndex, ct),
            ct).ConfigureAwait(false);

    /// <summary>Core fetch; issue calls through GetItemsFilteredAsync for 401/403 recovery.</summary>
    private async Task<DaminionItem[]> GetItemsFilteredCoreAsync(
        IDaminionApi api,
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
                    var query = QuoteSearchTerm(searchTerm!);
                    if (filters.Length > 0) query += " " + string.Join(" ", filters);
                    items = (await api.GetItems(startIndex, batch, f: null, search: query, maxItemsCount: 100000, ct: ct).ConfigureAwait(false)).EffectiveItems;
                    break;
                }
                case "all":
                {
                    var query = filters.Length > 0 ? string.Join(" ", filters) : "*";
                    items = (await api.GetItems(startIndex, batch, f: null, search: query, maxItemsCount: 100000, ct: ct).ConfigureAwait(false)).EffectiveItems;
                    break;
                }
                case "collection" when collectionId is not null:
                {
                    // Python parity: /api/SharedCollection/GetItems?id=… —
                    // the /api/Collections/GetItems/{id} variant 404s on
                    // server 11.0.0.3906 (damserver.local).
                    items = (await api.GetSharedCollectionItems(collectionId.Value, startIndex, batch, ct).ConfigureAwait(false)).EffectiveItems;
                    if (statusFilter != "all" || (untaggedFields?.Length ?? 0) > 0)
                        items = items.Where(i => PassesFilters(i, statusFilter, untaggedFields)).ToArray();
                    break;
                }
                case "saved_search" when savedSearchId is not null:
                {
                    // Structured query: "tagId,valueId" + "tagId,any" operators,
                    // mirroring the original saved-search lookup ("Saved Searches" tag).
                    var tagId = await GetTagIdAsync("saved searches", fallback: SavedSearchesTagFallbackId, ct).ConfigureAwait(false);
                    var f = new[] { $"{tagId},any" };
                    items = (await api.GetItems(startIndex, batch, f: f, queryLine: $"{tagId},{savedSearchId}", maxItemsCount: 100000, ct: ct).ConfigureAwait(false)).EffectiveItems;
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
        catch (ApiException e) when ((int)e.StatusCode is 401 or 403)
        {
            throw; // session-expiry recovery lives in WithSessionRecoveryAsync
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

    /// <summary>
    /// Quote a search term for Daminion's phrase syntax, doubling embedded
    /// double quotes so a term containing '"' stays a single phrase instead of
    /// terminating the quoted query early.
    /// </summary>
    private static string QuoteSearchTerm(string term) => $"\"{term.Replace("\"", "\"\"")}\"";

    private static bool PassesFilters(DaminionItem item, string statusFilter, string[]? untaggedFields)
    {
        var sf = (statusFilter ?? "all").ToLowerInvariant();
        if (sf != "all")
        {
            // A missing flag cannot be verified against the requested status —
            // exclude rather than let the filter silently pass it.
            if (item.Flag is not { } flag) return false;
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
        => await DownloadToTempAsync(itemId, $"thumb_{width}x{height}", api => api.GetThumbnail(itemId, width, height, ct), ct).ConfigureAwait(false);

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
        return await DownloadToTempAsync(itemId, $"preview_{width}x{targetH}", api => api.GetPreview(itemId, width, targetH, ct), ct).ConfigureAwait(false);
    }

    public async Task<string?> DownloadOriginalAsync(int itemId, CancellationToken ct = default)
        => await DownloadToTempAsync(itemId, "original", api => api.GetOriginal(itemId, ct), ct).ConfigureAwait(false);

    private async Task<string?> DownloadToTempAsync(
        int itemId, string kind, Func<IDaminionApi, Task<Stream>> fetch, CancellationToken ct)
    {
        string? tempFile = null;
        try
        {
            // Session-expiry recovery: a large download is exactly the call a
            // late batch item trips with a stale session cookie.
            using var stream = await WithSessionRecoveryAsync(fetch, ct).ConfigureAwait(false);
            if (stream is null)
            {
                SynapicLog.Warning(nameof(DaminionApiClient),
                    $"No response body for {kind} of item {itemId} — skipping");
                return null;
            }

            Directory.CreateDirectory(_tempDirectory);
            tempFile = Path.Combine(_tempDirectory, $"{itemId}_{kind}");

            await using (var fs = File.Create(tempFile))
            {
                await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            SynapicLog.Debug(nameof(DaminionApiClient),
                $"Downloaded {kind} for item {itemId} ({new FileInfo(tempFile).Length:N0} bytes) → {tempFile}");
            return tempFile;
        }
        catch (Exception e)
        {
            // A truncated download would otherwise sit in the temp folder for
            // ever: the caller only ever sees null and has no file to delete.
            if (tempFile is not null)
            {
                try { File.Delete(tempFile); }
                catch (Exception) { /* best effort — the sweep catches it later */ }
            }
            SynapicLog.Error(nameof(DaminionApiClient), $"Failed to download {kind} for item {itemId}: {e.Message}");
            return null;
        }
    }

    public async Task<(int W, int H)?> GetItemDimensionsAsync(int itemId, CancellationToken ct = default)
    {
        try
        {
            var ids = itemId.ToString();
            var resp = await GetApi().GetItemsByIds(ids, ct).ConfigureAwait(false);
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
    /// Names come straight from those tag values, so they match the Daminion
    /// client. The structured-query sweep below is only a last resort for
    /// builds that cannot enumerate the tag at all: it recovers value ids but
    /// not names, so entries there are synthesized as "Saved Search #id".
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
        var tagId = await GetTagIdAsync("saved searches", fallback: SavedSearchesTagFallbackId, ct).ConfigureAwait(false);
        var json = await GetApi().GetIndexedTagValues(tagId, ct: ct).ConfigureAwait(false);
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
    /// Last-resort discovery for builds whose indexed-tag enumeration fails:
    /// probe candidate value ids with structured queries (queryLine=tagId,N
    /// + operators) — the same mechanism the saved-search FETCH scope uses —
    /// and keep the ids that match at least one item. Enumeration cannot yield
    /// names here, so entries fall back to "Saved Search #id". Capped at a
    /// fixed probe range.
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
                maxItemsCount: 100000,
                ct: ct).ConfigureAwait(false);

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
        var json = await GetApi().GetCollections(ct: ct).ConfigureAwait(false);
        var result = new List<DaminionCollection>();
        foreach (var coll in UnwrapCollection(json, "collections", "items", "data"))
        {
            var id = GetInt(coll, "id") ?? 0;
            // Python parity: the shared-collection list has been seen keyed by
            // name, title, or the public access code — use whichever is present
            // rather than dropping the picker entry's label.
            var name = GetString(coll, "name", "title", "accessCode", "caption") ?? "";
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
                var flagTagId = await GetTagIdAsync("flag", fallback: 41, ct).ConfigureAwait(false);
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
                searchParts.Add(QuoteSearchTerm(searchTerm!));

            var combinedSearch = searchParts.Count > 0 ? string.Join(" ", searchParts) : null;

            // Scope-specific structured clauses.
            if (scope == "saved_search" && savedSearchId is not null)
            {
                var tagId = await GetTagIdAsync("saved searches", fallback: SavedSearchesTagFallbackId, ct).ConfigureAwait(false);
                qParts.Add($"{tagId},{savedSearchId}");
                fParts.Add($"{tagId},any");
            }
            else if (scope == "collection" && collectionId is not null)
            {
                var tagId = await GetCollectionTagIdAsync(ct).ConfigureAwait(false);
                qParts.Add($"{tagId},{collectionId}");
                fParts.Add($"{tagId},any");
            }

            var queryLine = qParts.Count > 0 ? string.Join(";", qParts) : null;
            var operators = fParts.Count > 0 ? string.Join(";", fParts) : null;

            var count = (await GetApi().GetCount(
                search: combinedSearch,
                queryLine: queryLine,
                f: operators,
                force: "false",
                ct: ct).ConfigureAwait(false)).EffectiveCount;

            // Sanity check (Python parity): a count that matches the whole
            // catalog size despite active filters/scope is suspect — re-derive
            // the total from a 1-item search whose totalCount the API reports
            // accurately (up to maxItemsCount).
            if ((combinedSearch is not null || queryLine is not null) && count > 0)
            {
                var totalCatalog = (await GetApi().GetCount(ct: ct).ConfigureAwait(false)).EffectiveCount;
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
                        maxItemsCount: 100000,
                        ct: ct).ConfigureAwait(false)).TotalCount ?? 0;
                }
            }

            // Structured fallback when a text search returned zero (Python
            // parity): match the keyword tag value id instead.
            if (count <= 0 && scope == "search" && !string.IsNullOrWhiteSpace(searchTerm))
            {
                var kwId = await GetTagIdAsync("keywords", fallback: 0, ct).ConfigureAwait(false);
                if (kwId > 0)
                {
                    var valueId = await FindTagValueIdAsync(kwId, searchTerm).ConfigureAwait(false);
                    if (valueId is { } vid)
                    {
                        count = (await GetApi().GetCount(
                            queryLine: $"{kwId},{vid}",
                            f: $"{kwId},any",
                            ct: ct).ConfigureAwait(false)).EffectiveCount;
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
    private async Task<int> GetCollectionTagIdAsync(CancellationToken ct = default)
    {
        var id = await GetTagIdAsync("shared collections", fallback: 0, ct).ConfigureAwait(false);
        if (id > 0) return id;
        id = await GetTagIdAsync("collections", fallback: 0, ct).ConfigureAwait(false);
        return id > 0 ? id : 46;
    }

    /// <summary>Split a ';'-joined operator string for the multi-value f= parameter.</summary>
    private static string[]? ParseQueryOperators(string? operators) =>
        string.IsNullOrEmpty(operators) ? null : operators.Split(';');

    /// <summary>
    /// Find a tag value's id by exact text (daminion_api.find_tag_values port,
    /// including the /api/IndexedTagValues fallback route some builds serve).
    /// </summary>
    private async Task<int?> FindTagValueIdAsync(int tagId, string filterText, CancellationToken ct = default)
    {
        JsonElement json;
        try
        {
            json = await GetApi().GetIndexedTagValues(tagId, filter: filterText, pageSize: 100, ct: ct).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // Fallback route (daminion_api.py parity): some builds only expose
            // the bare /api/IndexedTagValues endpoint.
            json = await GetApi().GetIndexedTagValuesFallback(tagId, filter: filterText, pageSize: 100, ct: ct).ConfigureAwait(false);
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
            await WithSessionRecoveryAsync(api => api.BatchChange(new DaminionBatchChangeRequest
            {
                Ids = new[] { itemId },
                Data = operations.ToArray(),
                Delete = false,
            }, ct), ct).ConfigureAwait(false);
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
            await WithSessionRecoveryAsync(api => api.BatchChange(new DaminionBatchChangeRequest
            {
                Ids = new[] { itemId },
                Data = keywords.Select(k => new DaminionTagOperation { Guid = guid, Value = k, Remove = true }).ToArray(),
                Delete = false,
            }, ct), ct).ConfigureAwait(false);
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
    /// Remove catalog entries via POST /api/MediaItems/Remove — the call the
    /// original Python client makes (<c>daminion_api.py delete_items</c>). The
    /// files on the server's disk are not touched (delete:false), only the
    /// catalog entries go.
    ///
    /// History: this used to post to /api/ItemData/BatchChange with delete:true.
    /// That route only writes tag data — it answers <c>{success:true}</c> and
    /// changes nothing, so dedup reported "Deleted N item(s)" while the very
    /// next scan found the same duplicates. The response is therefore logged
    /// and the ids are re-read with GetByIds before anything reports success.
    /// Returns false when the server does not actually drop them.
    /// </summary>
    public async Task<bool> DeleteItemsAsync(IReadOnlyCollection<int> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return true;

        var ids = itemIds.ToArray();
        try
        {
            var resp = await WithSessionRecoveryAsync(api => api.RemoveMediaItems(new DaminionRemoveRequest
            {
                Ids = ids,
                Delete = false, // catalog entry only — never the file on disk
            }, ct), ct).ConfigureAwait(false);

            SynapicLog.Info(nameof(DaminionApiClient),
                $"POST /api/MediaItems/Remove ids=[{string.Join(",", ids)}] delete=false → " +
                $"success={resp.Success} errorCode={resp.ErrorCode} data={TruncateJson(resp.Data)}" +
                (string.IsNullOrEmpty(resp.Error) ? "" : $" error={resp.Error}"));

            if (!resp.Success)
            {
                SynapicLog.Error(nameof(DaminionApiClient),
                    $"MediaItems/Remove refused {ids.Length} id(s) (errorCode {resp.ErrorCode})");
                return false;
            }

            // Belt and braces: a 200 from this server proves nothing on its own,
            // so ask for the ids back and report honestly if they are still there.
            var remaining = await CountItemsByIdsAsync(ids, ct).ConfigureAwait(false);
            if (remaining == 0)
            {
                SynapicLog.Info(nameof(DaminionApiClient), $"Removed {ids.Length} item(s) from the catalog (verified: GetByIds returns none of them)");
                return true;
            }

            // Servers that index asynchronously can answer GetByIds from a stale
            // snapshot right after the write — settle, then ask once more.
            await Task.Delay(750, ct).ConfigureAwait(false);
            remaining = await CountItemsByIdsAsync(ids, ct).ConfigureAwait(false);
            if (remaining == 0)
            {
                SynapicLog.Info(nameof(DaminionApiClient), $"Removed {ids.Length} item(s) from the catalog (verified after a 750 ms settle)");
                return true;
            }

            SynapicLog.Warning(nameof(DaminionApiClient),
                $"MediaItems/Remove left {remaining} of {ids.Length} item(s) in the catalog — " +
                "the server accepted the call but did not drop them (permissions or server build?)");
            return false;
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(DaminionApiClient), $"Failed to remove {ids.Length} item(s): {e.Message}");
            return false;
        }
    }

    /// <summary>How many of <paramref name="ids"/> the catalog still resolves —
    /// the post-delete check behind <see cref="DeleteItemsAsync"/>.</summary>
    private async Task<int> CountItemsByIdsAsync(int[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return 0;
        // Session recovery like every other call: a stale cookie here would
        // turn a successful delete into a reported failure.
        var resp = await WithSessionRecoveryAsync(
            api => api.GetItemsByIds(string.Join(",", ids), ct), ct).ConfigureAwait(false);
        return resp.EffectiveItems.Length;
    }

    /// <summary>Compact, bounded rendering of a Remove response payload for the log.</summary>
    private static string TruncateJson(JsonElement? data)
    {
        if (data is not { } element) return "null";
        var text = element.ToString();
        return text.Length <= 300 ? text : text[..300] + "…";
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
            var payload = await WithSessionRecoveryAsync(
                api => api.GetItemDataAll(itemId, ct), ct).ConfigureAwait(false);
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
        lock (_stateLock) _authenticated = false;
    }

    /// <summary>
    /// The GUID of the catalog this session is bound to, or null when the
    /// server cannot report one. The catalog is selected by the server URL, so
    /// this is how the UI names the one the login actually landed on. The
    /// response shape is not documented, so it is read defensively (a bare
    /// string, or an object wrapping the GUID) and the raw payload is logged
    /// when unreadable.
    /// </summary>
    public async Task<string?> GetCatalogGuidAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await GetApi().GetCatalogGuid().ConfigureAwait(false);
            var guid = ExtractCatalogGuid(json);

            if (guid is null)
                SynapicLog.Warning(nameof(DaminionApiClient),
                    $"Catalog guid: no usable value in response (raw: {Truncate(json.ToString(), 200)})");
            else
                SynapicLog.Info(nameof(DaminionApiClient), $"Catalog guid: {guid}");

            return guid;
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(DaminionApiClient), $"Catalog guid lookup failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pull the catalog GUID out of a <c>Settings/GetCatalogGuid</c> payload.
    /// The shape is undocumented and differs between server builds, so three
    /// forms are accepted: a bare string, an object carrying the GUID under a
    /// well-known key, and the standard envelope
    /// <c>{"data":"&lt;guid&gt;","error":null,"success":true,"errorCode":0}</c>
    /// that Daminion 11 answers with - the last one used to be rejected and
    /// logged as "no usable value" on every connect.
    /// </summary>
    public static string? ExtractCatalogGuid(JsonElement json)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.String:
                return NonEmpty(json.GetString());

            case JsonValueKind.Object:
                var direct = GetString(
                    json, "guid", "Guid", "catalogGuid", "CatalogGuid",
                    "catalogGUID", "value", "Value", "result", "Result");
                if (direct is not null) return direct;

                foreach (var key in new[] { "data", "Data" })
                {
                    if (!json.TryGetProperty(key, out var data)) continue;
                    if (data.ValueKind == JsonValueKind.String) return NonEmpty(data.GetString());
                    if (data.ValueKind == JsonValueKind.Object)
                    {
                        var nested = ExtractCatalogGuid(data);
                        if (nested is not null) return nested;
                    }
                }
                return null;

            default:
                return null;
        }
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
