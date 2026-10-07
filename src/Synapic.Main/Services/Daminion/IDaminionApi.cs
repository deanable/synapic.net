using System.Text.Json;
using Refit;

namespace Synapic.Avalonia.Services.Daminion;

/// <summary>
/// Daminion REST endpoints used by Synapic — mechanical port of the endpoints
/// exercised by the original ``daminion_api.py`` (Login, MediaItems/Get,
/// MediaItems/GetByIds, ItemData/BatchChange, Thumbnail/Get, Preview/Get,
/// Download/Get, Settings/GetVersion).
/// Every method takes an optional <see cref="CancellationToken"/> as its last
/// parameter (Refit sends it nowhere; it aborts the underlying HTTP call), so
/// aborting a batch cannot leave a request running — some original-file
/// downloads are minutes long.
/// </summary>
public interface IDaminionApi
{
    // ── Auth (query params per Daminion convention) ─────────────────────────
    // The catalog is selected by the server URL, so no catalog id is sent.
    [Post("/api/UserManager/Login")]
    Task<HttpResponseMessage> Login([Query] string userName, [Query] string password, CancellationToken ct = default);

    [Post("/api/UserManager/Logout")]
    Task<HttpResponseMessage> Logout(CancellationToken ct = default);

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
        [Query] string? asc = null,
        CancellationToken ct = default);

    [Get("/api/MediaItems/GetByIds")]
    Task<DaminionItemsResponse> GetItemsByIds([Query] string ids, CancellationToken ct = default);

    [Get("/api/MediaItems/GetCount")]
    Task<DaminionCountResponse> GetCount(
        [Query] string? search = null,
        [Query] string? queryLine = null,
        [Query] string? f = null,
        [Query] string? force = null,
        CancellationToken ct = default);

    [Get("/api/MediaItems/GetAbsolutePath/{id}")]
    Task<string> GetAbsolutePath(int id, CancellationToken ct = default);

    // ── Item data / metadata write ──────────────────────────────────────────
    [Get("/api/ItemData/GetAll/{id}")]
    Task<JsonElement> GetItemDataAll(int id, CancellationToken ct = default);

    [Post("/api/ItemData/BatchChange")]
    Task<JsonElement> BatchChange([Body] DaminionBatchChangeRequest request, CancellationToken ct = default);

    /// <summary>Remove catalog entries (daminion_api.py <c>delete_items</c>):
    /// <c>{ids:[…], delete:false}</c> drops the catalog entry only, <c>delete:true</c>
    /// also deletes the file on the server's disk. This is *not* BatchChange —
    /// that route is tag-data only and answers <c>success:true</c> while changing
    /// nothing, which is what made dedup's "Delete from catalog" a silent no-op.</summary>
    [Post("/api/MediaItems/Remove")]
    Task<DaminionRemoveResponse> RemoveMediaItems([Body] DaminionRemoveRequest request, CancellationToken ct = default);

    /// <summary>Wrapped layout payload (server 11.x nests entries under properties[].properties[]).</summary>
    [Get("/api/ItemData/GetDefaultLayout")]
    Task<JsonElement> GetDefaultLayout(CancellationToken ct = default);

    // ── Version control (checkout → new file version → check-in) ───────────
    // Port of daminion_api.py VersionControlAPI: CheckOut/UndoCheckOut take
    // {"Ids":[…]}; CheckIn is multipart (id + optional comment + file). The
    // upscale flow checks an item out, uploads the enhanced file as the new
    // version, and undoes the checkout when the upload fails. HttpResponseMessage
    // return types keep Refit from throwing on non-2xx so the caller decides —
    // same convention as Login.

    [Post("/api/VersionControl/CheckOut")]
    Task<HttpResponseMessage> CheckOutItems([Body] DaminionVersionIdsRequest request, CancellationToken ct = default);

    [Post("/api/VersionControl/UndoCheckOut")]
    Task<HttpResponseMessage> UndoCheckOutItems([Body] DaminionVersionIdsRequest request, CancellationToken ct = default);

    /// <summary>Check in a new file version without a comment (fallback for
    /// servers that reject the comment field — the original client tries the
    /// commented form first, then drops it and retries).</summary>
    [Multipart]
    [Post("/api/VersionControl/CheckIn")]
    Task<HttpResponseMessage> CheckInItem(
        [AliasAs("id")] int itemId,
        [AliasAs("file")] StreamPart file,
        CancellationToken ct = default);

    /// <summary>Check in a new file version with a check-in comment.</summary>
    [Multipart]
    [Post("/api/VersionControl/CheckIn")]
    Task<HttpResponseMessage> CheckInItemWithComment(
        [AliasAs("id")] int itemId,
        [AliasAs("comment")] string comment,
        [AliasAs("file")] StreamPart file,
        CancellationToken ct = default);

    // ── Thumbnails & originals (kept in Avalonia per spec §11 Q2) ───────────
    [Get("/api/Thumbnail/Get/{id}")]
    Task<Stream> GetThumbnail(int id, [Query] int width, [Query] int height, CancellationToken ct = default);

    [Get("/api/Preview/Get/{id}")]
    Task<Stream> GetPreview(int id, [Query] int width, [Query] int height, CancellationToken ct = default);

    [Get("/api/Download/Get/{id}")]
    Task<Stream> GetOriginal(int id, CancellationToken ct = default);

    // ── Settings ────────────────────────────────────────────────────────────
    [Get("/api/Settings/GetVersion")]
    Task<string> GetVersion(CancellationToken ct = default);

    [Get("/api/Settings/GetLoggedUser")]
    Task<JsonElement> GetLoggedUser(CancellationToken ct = default);

    /// <summary>
    /// GUID of the catalog this session is bound to (port of
    /// daminion_api.settings.get_catalog_guid). This is the only catalog fact
    /// the server exposes: Daminion has no endpoint that enumerates the
    /// catalogs available on a server, so the UI reports the catalog it
    /// actually landed on instead of offering a picklist.
    /// </summary>
    [Get("/api/Settings/GetCatalogGuid")]
    Task<JsonElement> GetCatalogGuid(CancellationToken ct = default);

    // ── Tags / collections for Step 1 pickers ───────────────────────────────
    [Get("/api/Settings/GetTags")]
    Task<JsonElement> GetAllTags(CancellationToken ct = default);

    /// <summary>Values of an indexed tag (saved searches, keywords…) — daminion_api.get_tag_values port.</summary>
    /// <remarks>
    /// Daminion routes this action on its full parameter set: every one of
    /// indexedTagId / parentValueId / filter / pageIndex / pageSize must be
    /// present or the server answers 404. ``filter`` therefore defaults to an
    /// empty string instead of null — Refit drops null query parameters, and
    /// the omission is what made saved-search enumeration 404 and degrade to
    /// synthesized "Saved Search #id" entries.
    /// </remarks>
    [Get("/api/IndexedTagValues/GetIndexedTagValues")]
    Task<JsonElement> GetIndexedTagValues(
        [Query] int indexedTagId,
        [Query] int parentValueId = -2,
        [Query] string filter = "",
        [Query] int pageIndex = 0,
        [Query] int pageSize = 500,
        CancellationToken ct = default);

    /// <summary>Bare /api/IndexedTagValues route (daminion_api.py fallback for builds lacking the GetIndexedTagValues action).</summary>
    /// <remarks>Same full-parameter-set routing rule as <see cref="GetIndexedTagValues"/>.</remarks>
    [Get("/api/IndexedTagValues")]
    Task<JsonElement> GetIndexedTagValuesFallback(
        [Query] int indexedTagId,
        [Query] int parentValueId = -2,
        [Query] string filter = "",
        [Query] int pageIndex = 0,
        [Query] int pageSize = 500,
        CancellationToken ct = default);

    /// <summary>Shared collections list — daminion_api.collections.get_all port.</summary>
    [Get("/api/SharedCollection/GetCollections")]
    Task<JsonElement> GetCollections([Query] int index = 0, [Query] int pageSize = 100, CancellationToken ct = default);

    /// <summary>
    /// Items of a shared collection — daminion_api.collections.get_items port.
    /// The original routes through /api/SharedCollection/GetItems?id=… (the
    /// /api/Collections/GetItems/{id} variant 404s on server 11.0.0.3906).
    /// </summary>
    [Get("/api/SharedCollection/GetItems")]
    Task<DaminionItemsResponse> GetSharedCollectionItems([Query] int id, [Query] int index, [Query] int pageSize, CancellationToken ct = default);
}
