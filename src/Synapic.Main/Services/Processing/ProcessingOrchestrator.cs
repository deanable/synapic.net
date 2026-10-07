using Synapic.Main.Services.Daminion;
using Synapic.Shared.Contracts;

namespace Synapic.Main.Services.Processing;

/// <summary>One processed item's outcome (session.results entry port).</summary>
public sealed record ProcessItemResult(
    string FileName,
    string Status,
    string Tags,
    string? Category,
    string[] Keywords,
    string? Description,
    Dictionary<string, double>? Probabilities,
    ScoringResultDto? Scoring,
    int? DaminionId = null)
{
    /// <summary>Grid-friendly joined keywords for the Step 4 DataGrid column.</summary>
    public string KeywordsCsv => string.Join(", ", Keywords);
}

/// <summary>Lightweight re-export of the shared scoring DTO for result entries.</summary>
public sealed record ScoringResultDto(string Tier, bool Calibrated);

/// <summary>Aggregate progress (count + ETA, port of progress_callback semantics).</summary>
public sealed record ProcessProgress(
    int Processed,
    int Failed,
    int Total,
    double Percent,
    TimeSpan? Eta,
    string CurrentFile,
    TimeSpan? PerItem = null)
{
    /// <summary>Human-readable duration for the ETA lines shared by every
    /// batch-style step (port of step3_process.py._format_duration).</summary>
    public static string FormatDuration(TimeSpan? value)
    {
        var seconds = value is { } v ? Math.Max((long)v.TotalSeconds, 0) : 0;
        var days = seconds / 86400;
        var hours = seconds % 86400 / 3600;
        var minutes = seconds % 3600 / 60;
        var secs = seconds % 60;
        if (days > 0) return $"~{days}d {hours}h";
        if (hours > 0) return $"~{hours}h {minutes}m";
        return $"~{minutes}m {secs}s";
    }
}

/// <summary>
/// Which of the model's returned fields are written to the item. The LFM
/// multimodal prompt always yields category, keywords and description together
/// (port of the original Step 2 checkboxes); this selects the permutation that
/// actually gets tagged. All three are on by default.
/// </summary>
public sealed record TagFieldSelection(bool Category = true, bool Keywords = true, bool Description = true)
{
    public static TagFieldSelection All { get; } = new();

    /// <summary>True when nothing is dropped: all three returned fields are written.</summary>
    public bool IsAll => Category && Keywords && Description;

    /// <summary>
    /// The fields a run will write, in words ("keywords only", "categories and
    /// keywords"). One multimodal call always returns description, category and
    /// keywords; the permutation the app keeps, so it is also the answer
    /// to "why did only keywords get tagged?".
    /// </summary>
    public string Summary
    {
        get
        {
            var names = new List<string>(3);
            if (Keywords) names.Add("keywords");
            if (Category) names.Add("categories");
            if (Description) names.Add("description");
            return names.Count switch
            {
                0 => "nothing (no tag field is selected)",
                1 => names[0] + " only",
                2 => $"{names[0]} and {names[1]}",
                _ => $"{names[0]}, {names[1]} and {names[2]}",
            };
        }
    }
}

/// <summary>How images are fetched for inference (spec §5.2 Step 1).</summary>
public sealed class DatasourceSelection
{
    public bool IsDaminion { get; init; }
    public string LocalPath { get; init; } = "";
    public bool LocalRecursive { get; init; }
    public DaminionApiClient? DaminionClient { get; init; }
    public string Scope { get; init; } = "all";
    public int? SavedSearchId { get; init; }
    public int? CollectionId { get; init; }
    public string? SearchTerm { get; init; }
    public string[]? UntaggedFields { get; init; }
    public string StatusFilter { get; init; } = "all";
    public int MaxItems { get; init; }

    /// <summary>
    /// Ignore <see cref="MaxItems"/> and keep paging until the server returns
    /// an empty batch. Some Daminion endpoints cap a single response (the
    /// web client uses them for infinite scrolling), so a short page is not
    /// treated as the end of the result set.
    /// </summary>
    public bool ProcessAll { get; init; }
    public bool AutoPaginate { get; init; } = true;
    public int ResizeScale { get; init; } = 100;
    public bool UseThumbnailOverride { get; init; }
}

/// <summary>
/// Tagging workflow — the route's <see cref="IWorkflowItemHandler"/> over the
/// shared <see cref="WorkflowRunner"/> kernel (fetch, throttling, pause, ETA
/// and error isolation live there; this class is the part that differs:
/// obtain an image, call /tag through the sidecar, write metadata, record the
/// result). Port of ``ProcessingManager``/processing.py.
/// </summary>
public sealed class ProcessingOrchestrator
{
    private static readonly WorkflowRunner Runner = new();

    private readonly IInferenceSidecar _sidecar;
    private readonly IMetadataWriter _metadataWriter;
    private readonly int _maxDegreeOfParallelism;

    public ProcessingOrchestrator(
        IInferenceSidecar sidecar,
        IMetadataWriter? metadataWriter = null,
        int maxDegreeOfParallelism = 4)
    {
        _sidecar = sidecar;
        _metadataWriter = metadataWriter ?? new MetadataWriterService();
        _maxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism);
    }

    /// <summary>
    /// The tagging run's definition: four parallel sidecar calls, plus the
    /// field-selection clause the batch-start message carries. This — not the
    /// loop — is what differs from dedup (sequential hashing) and upscaling
    /// (sequential inference).
    /// </summary>
    private WorkflowDefinition BuildWorkflow(TagFieldSelection? tagFields)
    {
        var writtenFields = tagFields ?? TagFieldSelection.All;
        return new WorkflowDefinition(
            Key: "tagging",
            Title: "Tagging",
            MaxDegreeOfParallelism: _maxDegreeOfParallelism,
            Description: $"writing {writtenFields.Summary}");
    }

    /// <summary>
    /// Run the batch: fetch items (local recursive scan or Daminion paginated
    /// fetch) and run them through the shared kernel. Per-item concurrency
    /// bounded by SemaphoreSlim (port of DaemonThreadPoolExecutor);
    /// pause/abort via CancellationToken.
    /// </summary>
    public async Task RunAsync(
        DatasourceSelection ds,
        TagRequest template,
        IProgress<ProcessProgress> progress,
        Func<string, Task> log,
        CancellationToken ct,
        List<ProcessItemResult>? results = null,
        PauseToken? pause = null,
        TagFieldSelection? tagFields = null)
    {
        var handler = new TaggingItemHandler(ds, template, _sidecar, _metadataWriter, log, results, tagFields);
        await Runner.RunAsync(BuildWorkflow(tagFields), ds, handler, progress, log, pause, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Run an explicit work-item subset (used by Step 4 "retry failed items").
    /// Same per-item pipeline and concurrency rules as <see cref="RunAsync"/>.
    /// </summary>
    public async Task RunItemsAsync(
        DatasourceSelection ds,
        TagRequest template,
        IReadOnlyList<ProcessWorkItem> items,
        IProgress<ProcessProgress> progress,
        Func<string, Task> log,
        CancellationToken ct,
        List<ProcessItemResult>? results = null,
        PauseToken? pause = null,
        TagFieldSelection? tagFields = null)
    {
        var handler = new TaggingItemHandler(ds, template, _sidecar, _metadataWriter, log, results, tagFields);
        await Runner.RunItemsAsync(BuildWorkflow(tagFields), items, handler, progress, log, pause, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The tagging route's per-item operation (port of
    /// ProcessingManager.process_single_item): obtain an image (local path or
    /// Daminion temp download honoring resizeScale/thumbnailOverride), call
    /// /tag, keep the selected fields, write metadata, record the result.
    /// Never throws for ordinary failures — a thrown exception is reserved for
    /// infrastructure errors the runner should count as failed items.
    /// </summary>
    private sealed class TaggingItemHandler : IWorkflowItemHandler
    {
        private readonly DatasourceSelection _ds;
        private readonly TagRequest _template;
        private readonly IInferenceSidecar _sidecar;
        private readonly IMetadataWriter _metadataWriter;
        private readonly Func<string, Task> _log;
        private readonly List<ProcessItemResult>? _results;
        private readonly object _resultsLock = new();
        private readonly TagFieldSelection _tagFields;

        public TaggingItemHandler(
            DatasourceSelection ds,
            TagRequest template,
            IInferenceSidecar sidecar,
            IMetadataWriter metadataWriter,
            Func<string, Task> log,
            List<ProcessItemResult>? results,
            TagFieldSelection? tagFields)
        {
            _ds = ds;
            _template = template;
            _sidecar = sidecar;
            _metadataWriter = metadataWriter;
            _log = log;
            _results = results;
            _tagFields = tagFields ?? TagFieldSelection.All;
        }

        public async Task<WorkflowItemOutcome> ProcessAsync(ProcessWorkItem item, CancellationToken ct)
        {
            string? tempFile = null;
            try
            {
                string imagePath;
                if (item.DaminionId is { } daminionId)
                {
                    var client = _ds.DaminionClient ?? throw new InvalidOperationException("Daminion client not connected");
                    await _log($"Processing Daminion Item: {item.FileName}...").ConfigureAwait(false);

                    string? downloaded;
                    if (_ds.UseThumbnailOverride)
                    {
                        downloaded = await client.DownloadThumbnailAsync(daminionId, 200, 200, ct).ConfigureAwait(false);
                    }
                    else if (_ds.ResizeScale >= 100)
                    {
                        downloaded = await client.DownloadOriginalAsync(daminionId, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var dims = await client.GetItemDimensionsAsync(daminionId, ct).ConfigureAwait(false);
                        var targetW = dims is { } d ? Math.Max(75, (int)(d.W * _ds.ResizeScale / 100.0)) : Math.Max(75, 2000 * _ds.ResizeScale / 100);
                        downloaded = await client.DownloadPreviewAsync(daminionId, targetW, null, ct).ConfigureAwait(false);
                    }

                    if (string.IsNullOrEmpty(downloaded) || !File.Exists(downloaded))
                        throw new IOException($"Could not download image for item {daminionId}");
                    tempFile = downloaded;
                    imagePath = downloaded;
                }
                else
                {
                    imagePath = item.LocalPath ?? throw new InvalidOperationException("Work item has no image path");
                    await _log($"Processing: {item.FileName}...").ConfigureAwait(false);
                }

            var request = _template with { ImagePath = imagePath };
            var response = await _sidecar.TagAsync(request, ct).ConfigureAwait(false);

            // One multimodal call returns all three fields; keep only the ones
            // the user asked to tag (original Step 2 checkbox behavior).
            var category = _tagFields.Category ? response.Category : null;
            var keywords = _tagFields.Keywords ? response.Keywords : Array.Empty<string>();
            var description = _tagFields.Description ? response.Description : null;

            var tags = new TagResult(category, keywords, description);
            var written = await WriteMetadataAsync(item, tags, ct).ConfigureAwait(false);

            var status = written ? "Success" : "Write Failed";
            var tagsSummary = $"Cat: {category}, Kws: {keywords.Length}, Desc: {Truncate(description, 20)}";
            await _log($"Result: {tagsSummary}").ConfigureAwait(false);

            var result = new ProcessItemResult(
                item.FileName, status, tagsSummary,
                category, keywords, description,
                response.Probabilities,
                response.Scoring is null ? null : new ScoringResultDto(response.Scoring.Tier, response.Scoring.Calibrated),
                item.DaminionId);
            if (_results is not null)
            {
                lock (_resultsLock) _results.Add(result);
            }

            // Success is "the item was processed to the end": a metadata write
            // that did not land shows up in the results grid as "Write Failed"
            // (historical behavior) rather than in the failed counter.
            return new WorkflowItemOutcome(true, status);
        }
        finally
        {
            if (tempFile is not null)
            {
                try { File.Delete(tempFile); }
                catch { /* cleanup best-effort */ }
            }
        }
        }

        private async Task<bool> WriteMetadataAsync(ProcessWorkItem item, TagResult tags, CancellationToken ct)
        {
            if (item.DaminionId is { } daminionId)
            {
                var client = _ds.DaminionClient ?? throw new InvalidOperationException("Daminion client not connected");
                return await client.UpdateItemMetadataAsync(daminionId, tags.Category, tags.Keywords, tags.Description, ct).ConfigureAwait(false);
            }
            return await _metadataWriter.WriteAsync(item.LocalPath!, tags, ct).ConfigureAwait(false);
        }

        private static string Truncate(string? s, int len) => string.IsNullOrEmpty(s) ? "" : (s.Length <= len ? s : s[..len] + "...");
    }
}

/// <summary>One unit of work: either a local path or a Daminion item id.</summary>
public sealed record ProcessWorkItem
{
    public string? LocalPath { get; init; }
    public int? DaminionId { get; init; }
    public required string FileName { get; init; }

    /// <summary>
    /// The Daminion server's content hash for this item, when the server
    /// included it in the response. When this is set the dedup scan can group
    /// the item from the server hash alone — no download, no algorithmic hash.
    /// Items whose server hash is 0 or missing still go through the
    /// download-and-hash path.
    /// </summary>
    public long ServerHashCode { get; init; }

    /// <summary>
    /// Server-reported file size in bytes for a Daminion item (the API's
    /// <c>fileSize</c>), or 0 when unknown. Lets the dedup review offer
    /// size-based auto-select for items grouped from their server hash — the
    /// path that deliberately never downloads the original.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Best available date for this item: EXIF capture date, else file creation
    /// date (for Daminion items the server may include this, otherwise it is
    /// filled in later from the downloaded original or left null).
    /// </summary>
    public DateTime? DateTakenUtc { get; init; }
}
