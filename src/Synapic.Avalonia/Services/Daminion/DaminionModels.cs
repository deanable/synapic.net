using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synapic.Avalonia.Services.Daminion;

/// <summary>Wrapper shapes returned by /api/MediaItems/Get (response format varies by server version).</summary>
public sealed class DaminionItemsResponse
{
    [JsonPropertyName("mediaItems")]
    public DaminionItem[]? MediaItems { get; init; }

    [JsonPropertyName("items")]
    public DaminionItem[]? Items { get; init; }

    [JsonPropertyName("data")]
    public DaminionItem[]? Data { get; init; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }

    [JsonIgnore]
    public DaminionItem[] EffectiveItems =>
        MediaItems ?? Items ?? Data ?? Array.Empty<DaminionItem>();
}

public sealed class DaminionCountResponse
{
    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }

    /// <summary>Envelope payload (server 11.x wraps the count in "data": 47851).</summary>
    [JsonPropertyName("data")]
    public int? Data { get; init; }

    [JsonIgnore]
    public int EffectiveCount => Count ?? TotalCount ?? Data ?? 0;
}

/// <summary>Media item record (subset of fields Synapic consumes).</summary>
public sealed class DaminionItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("flag")]
    public JsonElement? Flag { get; init; }

    [JsonPropertyName("Keywords")]
    public string[]? Keywords { get; init; }

    [JsonPropertyName("Categories")]
    public string[]? Categories { get; init; }

    [JsonPropertyName("Description")]
    public string? Description { get; init; }

    [JsonPropertyName("Width")]
    public int? Width { get; init; }

    [JsonPropertyName("Height")]
    public int? Height { get; init; }

    [JsonPropertyName("PixelWidth")]
    public int? PixelWidth { get; init; }

    [JsonPropertyName("PixelHeight")]
    public int? PixelHeight { get; init; }

    [JsonIgnore]
    public (int W, int H)? Dimensions
    {
        get
        {
            var w = Width ?? PixelWidth;
            var h = Height ?? PixelHeight;
            return w is > 0 && h is > 0 ? (w.Value, h.Value) : null;
        }
    }
}

/// <summary>A named saved search (id = the queryLine value used for scoping).</summary>
public sealed record DaminionSavedSearch(int Id, string Name, int Count);

/// <summary>Outcome of re-reading a Daminion item's metadata after write-back.</summary>
public sealed record DaminionVerifyResult(bool Ok, string Detail);

/// <summary>A shared collection selectable as a Step 1 scope.</summary>
public sealed record DaminionCollection(int Id, string Name, string Code, int ItemCount);

/// <summary>
/// Body of POST /api/ItemData/BatchChange (daminion_api.py batch_update).
/// </summary>
public sealed class DaminionBatchChangeRequest
{
    [JsonPropertyName("ids")]
    public int[] Ids { get; init; } = Array.Empty<int>();

    [JsonPropertyName("data")]
    public DaminionTagOperation[] Data { get; init; } = Array.Empty<DaminionTagOperation>();

    [JsonPropertyName("delete")]
    public bool Delete { get; init; }

    [JsonPropertyName("excludeIds")]
    public int[]? ExcludeIds { get; init; }
}

/// <summary>
/// Body of POST /api/MediaItems/Remove (daminion_api.py <c>delete_items</c>).
/// <c>Delete</c> decides whether the <em>file on the server's disk</em> goes too:
/// false removes the catalog entry only, which is what Synapic's dedup action
/// promises ("the original files on the server are not touched").
/// </summary>
public sealed class DaminionRemoveRequest
{
    [JsonPropertyName("ids")]
    public int[] Ids { get; init; } = Array.Empty<int>();

    [JsonPropertyName("delete")]
    public bool Delete { get; init; }
}

/// <summary>
/// Envelope of POST /api/MediaItems/Remove. <c>Data</c> is a per-id status map
/// (an id the server did not remove comes back as -1), so a 200 alone never
/// proves the catalog entries are gone — the caller verifies with GetByIds.
/// </summary>
public sealed class DaminionRemoveResponse
{
    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("errorCode")]
    public int ErrorCode { get; init; }
}

/// <summary>
/// Body of POST /api/VersionControl/CheckOut and /api/VersionControl/UndoCheckOut
/// (daminion_api.py VersionControlAPI: <c>{"Ids": item_ids}</c>).
/// </summary>
public sealed class DaminionVersionIdsRequest
{
    [JsonPropertyName("Ids")]
    public int[] Ids { get; init; } = Array.Empty<int>();
}

/// <summary>One tag operation: attach/remove a tag value by id or by raw value.</summary>
public sealed class DaminionTagOperation
{
    [JsonPropertyName("guid")]
    public string Guid { get; init; } = "";

    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("remove")]
    public bool Remove { get; init; }
}
