using System.Diagnostics;
using System.IO;
using Synapic.Avalonia.Services.Daminion;
using Synapic.Shared.Contracts;

namespace Synapic.Avalonia.Services.Processing;

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
    TimeSpan? PerItem = null);

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
    /// keywords; this is the permutation the app keeps, so it is also the answer
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
/// Batch tagging pipeline — C# port of ``ProcessingManager``: fetch items
/// (local recursive scan or Daminion paginated fetch), run inference per item
/// through the sidecar, and write metadata to files or Daminion. Per-item
/// concurrency bounded by SemaphoreSlim (port of DaemonThreadPoolExecutor);
/// pause/abort via CancellationToken.
/// </summary>
public sealed class ProcessingOrchestrator
{
    private static readonly string[] LocalExtensions = { ".jpg", ".jpeg", ".png", ".tif", ".tiff" };

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
    /// Fetch items per the datasource selection (processing.py _fetch_items port).
    /// Local: recursive/shallow scan. Daminion: paged 500-item batches while
    /// auto-paginate is on and maxItems allows.
    /// </summary>
    public async Task<IReadOnlyList<ProcessWorkItem>> FetchItemsAsync(DatasourceSelection ds, CancellationToken ct)
    {
        var items = new List<ProcessWorkItem>();

        if (!ds.IsDaminion)
        {
            if (string.IsNullOrWhiteSpace(ds.LocalPath) || !Directory.Exists(ds.LocalPath))
                throw new DirectoryNotFoundException($"Folder not found: {ds.LocalPath}");

            var option = ds.LocalRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var path in Directory.EnumerateFiles(ds.LocalPath, "*.*", option))
            {
                if (LocalExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                    items.Add(new ProcessWorkItem { LocalPath = path, FileName = Path.GetFileName(path) });
            }
            SynapicLog.Info(nameof(ProcessingOrchestrator), $"Found {items.Count} image files in {ds.LocalPath} (recursive={ds.LocalRecursive})");
            return items;
        }

        var client = ds.DaminionClient ?? throw new InvalidOperationException("Daminion client not connected");
        var startIndex = 0;
        // "Process all" (or a zero/negative max) lifts the ceiling entirely: the
        // only stopping condition is an empty batch from the server.
        var processAll = ds.ProcessAll || ds.MaxItems <= 0;
        var limit = processAll ? int.MaxValue : ds.MaxItems;
        var lastPageIds = new HashSet<int>();

        while (items.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await client.GetItemsFilteredAsync(
                scope: ds.Scope,
                savedSearchId: ds.SavedSearchId,
                collectionId: ds.CollectionId,
                searchTerm: ds.SearchTerm,
                untaggedFields: ds.UntaggedFields,
                statusFilter: ds.StatusFilter,
                maxItems: Math.Min(500, limit - items.Count),
                startIndex: startIndex,
                ct: ct).ConfigureAwait(false);

            if (batch.Length == 0) break;

            // Infinite-loop guard (processing.py parity): if the server returns
            // the same ids for every offset (e.g. the untagged filter is not
            // applied server-side), stop instead of looping forever.
            var pageIds = batch.Select(i => i.Id).ToHashSet();
            if (pageIds.Count > 0 && pageIds.SetEquals(lastPageIds))
            {
                SynapicLog.Warning(nameof(ProcessingOrchestrator),
                    "Daminion returned the same ids as the previous page — stopping pagination to avoid an infinite loop");
                break;
            }
            lastPageIds = pageIds;

            foreach (var item in batch)
            {
                items.Add(new ProcessWorkItem
                {
                    DaminionId = item.Id,
                    FileName = item.FileName ?? $"Item {item.Id}",
                });
            }

            startIndex += batch.Length;

            // Process-all never treats a partial page as the end: some endpoints
            // cap a single response, so keep requesting until a page is empty.
            if (processAll) continue;
            if (!ds.AutoPaginate || batch.Length < 500) break;
        }

        SynapicLog.Info(nameof(ProcessingOrchestrator), $"Fetched {items.Count} Daminion items (scope={ds.Scope})");
        return items;
    }

    /// <summary>
    /// Run the batch: per item — obtain an image (local path or Daminion temp
    /// download honoring resizeScale/thumbnailOverride), call /tag, write
    /// metadata, record the result. Never aborts the whole run on one failure.
    /// While paused, queued items wait before starting; running items finish.
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
        var items = await FetchItemsAsync(ds, ct).ConfigureAwait(false);
        // Report the fetched total immediately: with a cold model the first
        // item can take minutes, and the UI must not sit at an empty bar.
        progress.Report(new ProcessProgress(0, 0, items.Count, 0, null, "Starting…"));
        await RunItemsAsync(ds, template, items, progress, log, ct, results, pause, tagFields).ConfigureAwait(false);
    }

    /// <summary>
    /// Run an explicit work-item subset (used by Step 4 “retry failed items”).
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
        var total = items.Count;
        var processed = 0;
        var failed = 0;
        var startedAt = Stopwatch.StartNew();
        results ??= new List<ProcessItemResult>();
        var resultsLock = new object();

        // Say which fields will be written before the first item runs. The
        // model returns all three whatever this says; only the write step
        // honours it, so a partial selection used to look exactly like the
        // model having stopped producing fields.
        var writtenFields = tagFields ?? TagFieldSelection.All;
        await log($"Starting batch: {total} items — writing {writtenFields.Summary}").ConfigureAwait(false);
        SynapicLog.Info(nameof(ProcessingOrchestrator),
            $"Batch started: {total} items, writing {writtenFields.Summary}");

        using var throttle = new SemaphoreSlim(_maxDegreeOfParallelism);
        var tasks = items.Select(item => Task.Run(async () =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (pause is { } pauseToken) await pauseToken.WaitWhilePausedAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                // Report at item START: with a cold sidecar the first items
                // take minutes (model load + generation), and completion-only
                // reporting left the progress UI frozen the whole time.
                int doneSnapshot, failedSnapshot;
                lock (resultsLock) { doneSnapshot = processed; failedSnapshot = failed; }
                var startEstimate = Estimate(startedAt, doneSnapshot, total);
                progress.Report(new ProcessProgress(
                    doneSnapshot, failedSnapshot, total,
                    total == 0 ? 0 : 100.0 * doneSnapshot / total,
                    startEstimate.Eta, item.FileName, startEstimate.PerItem));

                var result = await ProcessSingleItemAsync(ds, template, item, log, ct, tagFields).ConfigureAwait(false);
                lock (resultsLock)
                {
                    processed++;
                    results.Add(result);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                lock (resultsLock)
                {
                    processed++;
                    failed++;
                }
                await log($"Failed: {item.FileName} — {e.Message}").ConfigureAwait(false);
                SynapicLog.Error(nameof(ProcessingOrchestrator), $"Failed to process '{item.FileName}': {e}");
            }
            finally
            {
                throttle.Release();

                lock (resultsLock)
                {
                    var done = processed;
                    var estimate = Estimate(startedAt, done, total);
                    progress.Report(new ProcessProgress(
                        done, failed, total,
                        total == 0 ? 0 : 100.0 * done / total,
                        estimate.Eta, item.FileName, estimate.PerItem));
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        await log($"Batch finished: {processed - failed} ok, {failed} failed, {total} total").ConfigureAwait(false);
    }

    /// <summary>
    /// Progress estimate (port of processing.py progress_callback): average
    /// elapsed time per completed item × remaining items. The ETA only becomes
    /// available once the first item finishes (processed &gt; 0), mirroring the
    /// Python app rather than waiting for a few warm-up samples.
    /// </summary>
    private static (TimeSpan? Eta, TimeSpan? PerItem) Estimate(Stopwatch stopwatch, int processed, int total)
        => EstimateProgress(stopwatch.Elapsed, processed, total);

    /// <summary>Time-based ETA math, separated out so it is directly testable.</summary>
    public static (TimeSpan? Eta, TimeSpan? PerItem) EstimateProgress(TimeSpan elapsed, int processed, int total)
    {
        if (processed <= 0 || total <= 0) return (null, null);
        var perItem = TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / processed);
        return (perItem * Math.Max(total - processed, 0), perItem);
    }

    private async Task<ProcessItemResult> ProcessSingleItemAsync(
        DatasourceSelection ds,
        TagRequest template,
        ProcessWorkItem item,
        Func<string, Task> log,
        CancellationToken ct,
        TagFieldSelection? tagFields = null)
    {
        string? tempFile = null;
        try
        {
            string imagePath;
            if (item.DaminionId is { } daminionId)
            {
                var client = ds.DaminionClient ?? throw new InvalidOperationException("Daminion client not connected");
                await log($"Processing Daminion Item: {item.FileName}...").ConfigureAwait(false);

                string? downloaded;
                if (ds.UseThumbnailOverride)
                {
                    downloaded = await client.DownloadThumbnailAsync(daminionId, 200, 200, ct).ConfigureAwait(false);
                }
                else if (ds.ResizeScale >= 100)
                {
                    downloaded = await client.DownloadOriginalAsync(daminionId, ct).ConfigureAwait(false);
                }
                else
                {
                    var dims = await client.GetItemDimensionsAsync(daminionId, ct).ConfigureAwait(false);
                    var targetW = dims is { } d ? Math.Max(75, (int)(d.W * ds.ResizeScale / 100.0)) : Math.Max(75, 2000 * ds.ResizeScale / 100);
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
                await log($"Processing: {item.FileName}...").ConfigureAwait(false);
            }

            var request = template with { ImagePath = imagePath };
            var response = await _sidecar.TagAsync(request, ct).ConfigureAwait(false);

            // One multimodal call returns all three fields; keep only the ones
            // the user asked to tag (original Step 2 checkbox behavior).
            var selected = tagFields ?? TagFieldSelection.All;
            var category = selected.Category ? response.Category : null;
            var keywords = selected.Keywords ? response.Keywords : Array.Empty<string>();
            var description = selected.Description ? response.Description : null;

            var tags = new TagResult(category, keywords, description);
            var written = await WriteMetadataAsync(ds, item, tags, ct).ConfigureAwait(false);

            var status = written ? "Success" : "Write Failed";
            var tagsSummary = $"Cat: {category}, Kws: {keywords.Length}, Desc: {Truncate(description, 20)}";
            await log($"Result: {tagsSummary}").ConfigureAwait(false);

            return new ProcessItemResult(
                item.FileName, status, tagsSummary,
                category, keywords, description,
                response.Probabilities,
                response.Scoring is null ? null : new ScoringResultDto(response.Scoring.Tier, response.Scoring.Calibrated),
                item.DaminionId);
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

    private async Task<bool> WriteMetadataAsync(DatasourceSelection ds, ProcessWorkItem item, TagResult tags, CancellationToken ct)
    {
        if (item.DaminionId is { } daminionId)
        {
            var client = ds.DaminionClient ?? throw new InvalidOperationException("Daminion client not connected");
            return await client.UpdateItemMetadataAsync(daminionId, tags.Category, tags.Keywords, tags.Description, ct).ConfigureAwait(false);
        }
        return await _metadataWriter.WriteAsync(item.LocalPath!, tags, ct).ConfigureAwait(false);
    }

    private static string Truncate(string? s, int len) => string.IsNullOrEmpty(s) ? "" : (s.Length <= len ? s : s[..len] + "...");
}

/// <summary>One unit of work: either a local path or a Daminion item id.</summary>
public sealed record ProcessWorkItem
{
    public string? LocalPath { get; init; }
    public int? DaminionId { get; init; }
    public required string FileName { get; init; }
}
