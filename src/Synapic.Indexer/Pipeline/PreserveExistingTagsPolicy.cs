using System.Collections.Concurrent;
using Synapic.Core.Services;
using Synapic.Core.Services.Processing;

namespace Synapic.Indexer.Pipeline;

/// <summary>What a file already carries, and whether that covers every selected field.</summary>
public sealed record ExistingTags(TagResult? Tags, bool FullyPopulated);

/// <summary>
/// <c>tagging.overwriteExistingTags</c>, expressed at the kernel's write seam
/// (D-11-23). With overwrite off, a selected field that already has a value is
/// left out of the tags handed to the writer, where a null/empty value means
/// "unchanged". The executor asks <see cref="InspectAsync"/> first: when every
/// selected field is filled, the item is completed without calling the model at
/// all — the same read drives both decisions, so they cannot disagree.
/// </summary>
/// <remarks>
/// Known limitation, deliberately pinned: local keywords are merge-only
/// (<see cref="MetadataPatch.FromTags"/> maps them to Merge) and the catalog
/// update only adds values. Overwrite therefore means "write the model's values",
/// never "delete what is already there".
/// </remarks>
public sealed class PreserveExistingTagsPolicy : ITagWritePolicy
{
    private readonly IMetadataWriter _writer;
    private readonly TagFieldSelection _fields;
    private readonly bool _overwrite;
    private readonly ConcurrentDictionary<string, TagResult?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public PreserveExistingTagsPolicy(IMetadataWriter writer, TagFieldSelection fields, bool overwrite)
    {
        _writer = writer;
        _fields = fields;
        _overwrite = overwrite;
    }

    public bool Overwrite => _overwrite;

    /// <summary>Reads the file's current tags once and reports coverage.</summary>
    public async Task<ExistingTags> InspectAsync(string path, CancellationToken ct)
    {
        var tags = await _writer.ReadAsync(path, ct).ConfigureAwait(false);
        _cache[path] = tags;
        return new ExistingTags(tags, IsFullyPopulated(tags));
    }

    /// <summary>A write (or a new batch) invalidates what was read before it.</summary>
    public void ClearCache() => _cache.Clear();

    public async Task<TagResult> AdjustAsync(ProcessWorkItem item, TagResult tags, CancellationToken ct)
    {
        if (_overwrite || item.LocalPath is not { } path) return tags;

        if (!_cache.TryGetValue(path, out var existing))
        {
            existing = await _writer.ReadAsync(path, ct).ConfigureAwait(false);
            _cache[path] = existing;
        }

        if (existing is null) return tags;

        return new TagResult(
            _fields.Category && !string.IsNullOrEmpty(existing.Category) ? null : tags.Category,
            _fields.Keywords && existing.Keywords.Count > 0 ? Array.Empty<string>() : tags.Keywords,
            _fields.Description && !string.IsNullOrEmpty(existing.Description) ? null : tags.Description);
    }

    private bool IsFullyPopulated(TagResult? tags)
    {
        var category = !_fields.Category || !string.IsNullOrEmpty(tags?.Category);
        var keywords = !_fields.Keywords || tags is { Keywords.Count: > 0 };
        var description = !_fields.Description || !string.IsNullOrEmpty(tags?.Description);
        return category && keywords && description;
    }
}
