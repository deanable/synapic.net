using System.Text.Json;
using Refit;

namespace Synapic.Avalonia.Services.Daminion;

/// <summary>
/// Daminion REST endpoints used by Synapic — mechanical port of the endpoints
/// exercised by the original ``daminion_api.py`` (Login, MediaItems/Get,
/// MediaItems/GetByIds, ItemData/BatchChange, Thumbnail/Get, Preview/Get,
/// Download/Get, Settings/GetVersion).
/// </summary>
public interface IDaminionApi
{
    // ── Auth (query params per Daminion convention) ─────────────────────────
    [Post("/api/UserManager/Login")]
    Task<HttpResponseMessage> Login([Query] string userName, [Query] string password, [Query] string? catalogId = null);

    [Post("/api/UserManager/Logout")]
    Task<HttpResponseMessage> Logout();

    // ── Media items ─────────────────────────────────────────────────────────
    [Get("/api/MediaItems/Get")]
    Task<DaminionItemsResponse> GetItems(
        [Query] int index,
        [Query] int size,
        [Query(CollectionFormat.Multi)] string[]? f = null,
        [Query] string? search = null,
        [Query] string? queryLine = null,
        [Query] int? maxItemsCount = null,
        [Query] int? sortag = null,
        [Query] string? asc = null);

    [Get("/api/MediaItems/GetByIds")]
    Task<DaminionItemsResponse> GetItemsByIds([Query] string ids);

    [Get("/api/MediaItems/GetCount")]
    Task<DaminionCountResponse> GetCount(
        [Query] string? search = null,
        [Query] string? queryLine = null,
        [Query(CollectionFormat.Multi)] string[]? f = null,
        [Query] string? force = null);

    [Get("/api/MediaItems/GetAbsolutePath/{id}")]
    Task<string> GetAbsolutePath(int id);

    // ── Item data / metadata write ──────────────────────────────────────────
    [Get("/api/ItemData/GetAll/{id}")]
    Task<JsonElement> GetItemDataAll(int id);

    [Post("/api/ItemData/BatchChange")]
    Task<JsonElement> BatchChange([Body] DaminionBatchChangeRequest request);

    [Get("/api/ItemData/GetDefaultLayout")]
    Task<JsonElement[]> GetDefaultLayout();

    // ── Thumbnails & originals (kept in Avalonia per spec §11 Q2) ───────────
    [Get("/api/Thumbnail/Get/{id}")]
    Task<Stream> GetThumbnail(int id, [Query] int width, [Query] int height);

    [Get("/api/Preview/Get/{id}")]
    Task<Stream> GetPreview(int id, [Query] int width, [Query] int height);

    [Get("/api/Download/Get/{id}")]
    Task<Stream> GetOriginal(int id);

    // ── Settings ────────────────────────────────────────────────────────────
    [Get("/api/Settings/GetVersion")]
    Task<string> GetVersion();

    [Get("/api/Settings/GetLoggedUser")]
    Task<JsonElement> GetLoggedUser();

    // ── Tags / collections for Step 1 pickers ───────────────────────────────
    [Get("/api/Settings/GetTags")]
    Task<JsonElement> GetAllTags();

    /// <summary>Values of an indexed tag (saved searches, keywords…) — daminion_api.get_tag_values port.</summary>
    [Get("/api/IndexedTagValues/GetIndexedTagValues")]
    Task<JsonElement> GetIndexedTagValues(
        [Query] int indexedTagId,
        [Query] int parentValueId = -2,
        [Query] string? filter = null,
        [Query] int pageIndex = 0,
        [Query] int pageSize = 500);

    /// <summary>Shared collections list — daminion_api.collections.get_all port.</summary>
    [Get("/api/SharedCollection/GetCollections")]
    Task<JsonElement> GetCollections([Query] int index = 0, [Query] int pageSize = 100);

    [Get("/api/Collections/GetItems/{id}")]
    Task<DaminionItemsResponse> GetCollectionItems(int id, [Query] int index, [Query] int size);
}
