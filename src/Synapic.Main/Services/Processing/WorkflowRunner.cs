using System.Diagnostics;
using Synapic.Main.Services.Daminion;

namespace Synapic.Main.Services.Processing;

/// <summary>
/// The parameters that differ between the three workflows — tag assignment,
/// duplicate scanning, upscaling. The run itself (fetch → bounded per-item
/// execution → progress/ETA → summary) is identical for all of them and lives
/// in <see cref="WorkflowRunner"/>; only this record and the
/// <see cref="IWorkflowItemHandler"/> implementation change per workflow.
/// </summary>
/// <param name="Key">Stable identifier for logs ("tagging" | "dedup" | "upscale").</param>
/// <param name="Title">Human name shown in batch messages.</param>
/// <param name="MaxDegreeOfParallelism">
/// How many items may be in flight. Tagging fans out four sidecar calls; the
/// dedup scan streams downloads one at a time so a large collection never
/// lands on disk whole; upscaling serializes like the original app's single
/// worker thread.
/// </param>
/// <param name="Description">
/// Optional clause appended to the batch-start messages ("writing keywords and
/// categories"), so each workflow states its own parameters in the same line.
/// </param>
public sealed record WorkflowDefinition(
    string Key,
    string Title,
    int MaxDegreeOfParallelism = 1,
    string? Description = null);

/// <summary>The workflow-specific operation applied to one fetched item.</summary>
/// <remarks>
/// Implementations own everything that varies per workflow: obtaining the
/// bytes (local path vs. Daminion download), the operation itself (tag, hash,
/// upscale), writing the result back (metadata, catalog check-in, sibling
/// file) and cleaning up. The runner owns everything that does not vary:
/// throttling, pause, cancellation, progress/ETA reporting, error isolation
/// and the run summary.
/// </remarks>
public interface IWorkflowItemHandler
{
    /// <summary>
    /// Process one item. Throwing marks the item failed (the runner logs the
    /// exception and continues with the rest of the batch); returning
    /// <c>Success=false</c> marks a handled soft failure (checkout refused,
    /// download missing) that the handler has already reported itself.
    /// Cancellation must propagate as <see cref="OperationCanceledException"/>.
    /// </remarks>
    Task<WorkflowItemOutcome> ProcessAsync(ProcessWorkItem item, CancellationToken ct);
}

/// <summary>How one item went — counted by the runner for the run summary.</summary>
/// <param name="Success">False for a handled soft failure; exceptions are counted separately.</param>
/// <param name="Status">Short outcome text for handlers that tally their own results.</param>
public sealed record WorkflowItemOutcome(bool Success, string Status)
{
    public static WorkflowItemOutcome Ok { get; } = new(true, "Success");

    public static WorkflowItemOutcome Fail(string status) => new(false, status);
}

/// <summary>What one run produced (progress reporting carries the live view).</summary>
public sealed record WorkflowRunSummary(int Total, int Processed, int Succeeded, int Failed)
{
    public static WorkflowRunSummary Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// How far a paged catalog walk has got. Reported after every page, so a long
/// whole-scope fetch shows movement, a count and a time estimate.
/// </summary>
/// <param name="Kept">Eligible items collected so far (unique catalog ids).</param>
/// <param name="Scanned">Raw records walked so far — the cursor's position,
/// including records the client-side status/untagged filter dropped. This is
/// what progress is measured against, because <paramref name="Total"/> is the
/// server's raw scope total.</param>
/// <param name="Pages">Pages completed so far (one page = ≤500 records).</param>
/// <param name="Total">The scope's total as reported by the server, when known.</param>
public readonly record struct CatalogFetchProgress(int Kept, int Scanned, int Pages, int? Total);

/// <summary>
/// The shared batch kernel behind every route of the app. Tagging,
/// deduplication and upscaling differ only in their
/// <see cref="WorkflowDefinition"/> and <see cref="IWorkflowItemHandler"/>;
/// fetching, concurrency, pause/cancel, progress with ETA, per-item error
/// isolation and summary accounting are implemented once, here.
/// </summary>
public sealed class WorkflowRunner
{
    /// <summary>The image types a folder scan picks up (the ones Synapic can read and write).</summary>
    public static readonly string[] LocalImageExtensions = { ".jpg", ".jpeg", ".png", ".tif", ".tiff" };

    /// <summary>True when a file's extension is one Synapic processes.</summary>
    public static bool IsLocalImage(string path) =>
        LocalImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Count the images a folder scan would process, with exactly the filter
    /// <see cref="FetchItemsAsync"/> applies — so a number shown before a run is
    /// the number the run will actually see.
    /// </summary>
    public static int CountLocalImages(string folder, bool recursive, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        ct.ThrowIfCancellationRequested();
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.*", option))
        {
            ct.ThrowIfCancellationRequested();
            if (IsLocalImage(path)) count++;
        }
        return count;
    }

    // ── Fetch (shared: local recursive scan or Daminion pagination) ─────────

    /// <summary>
    /// Fetch items per the datasource selection (processing.py _fetch_items
    /// port). Local: recursive/shallow scan. Daminion: paged 500-item batches
    /// while auto-paginate is on and maxItems allows. Moved here from
    /// ProcessingOrchestrator so every workflow resolves its work items the
    /// same way.
    /// </summary>
    /// <remarks>
    /// The Daminion API caps a single response at <see cref="DaminionApiClient.PageSize"/>
    /// (500) items, so a larger result set is walked page by page. The cursor
    /// advances by the <em>raw</em> page the server returned, not by the eligible
    /// items kept: collection and saved-search scopes are filtered on this side,
    /// and advancing by the smaller filtered count would overlap the raw records
    /// it skipped. A page whose records were all filtered out is likewise not the
    /// end of the result set — only a completely empty raw page is.
    ///
    /// This entry point honours the Step 1 ceiling (<see cref="DatasourceSelection.MaxItems"/>,
    /// <see cref="DatasourceSelection.ProcessAll"/> and <see cref="DatasourceSelection.AutoPaginate"/>)
    /// because it serves tagging and upscaling. A whole-set operation must use
    /// <see cref="FetchEntireScopeAsync"/> instead.
    /// </remarks>
    public static async Task<IReadOnlyList<ProcessWorkItem>> FetchItemsAsync(DatasourceSelection ds, CancellationToken ct)
        => await FetchItemsCoreAsync(ds, entireScope: false, progress: null, ct).ConfigureAwait(false);

    /// <summary>
    /// Fetch <em>every</em> item in the scope, walking the catalog 500 records at
    /// a time until the server returns an empty page.
    /// </summary>
    /// <remarks>
    /// Deduplication and the similarity search compare each item against every
    /// other, so a truncated set silently hides groups: an item whose copy sits
    /// past the cut looks unique, and the user deletes the wrong file. Those
    /// scans therefore ignore the Step 1 "max items" ceiling, the per-run
    /// process-all switch and auto-paginate — that ceiling belongs to tagging,
    /// where a partial batch is meaningful. The walk is long on a large catalog,
    /// which is what <paramref name="progress"/> is for: it reports the running
    /// item count (and the server's total when it reports one) after every page.
    /// </remarks>
    public static async Task<IReadOnlyList<ProcessWorkItem>> FetchEntireScopeAsync(
        DatasourceSelection ds,
        IProgress<CatalogFetchProgress>? progress = null,
        CancellationToken ct = default)
        => await FetchItemsCoreAsync(ds, entireScope: true, progress, ct).ConfigureAwait(false);

    private static async Task<IReadOnlyList<ProcessWorkItem>> FetchItemsCoreAsync(
        DatasourceSelection ds,
        bool entireScope,
        IProgress<CatalogFetchProgress>? progress,
        CancellationToken ct)
    {
        var items = new List<ProcessWorkItem>();

        if (!ds.IsDaminion)
        {
            if (string.IsNullOrWhiteSpace(ds.LocalPath) || !Directory.Exists(ds.LocalPath))
                throw new DirectoryNotFoundException($"Folder not found: {ds.LocalPath}");

            var option = ds.LocalRecursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var path in Directory.EnumerateFiles(ds.LocalPath, "*.*", option))
            {
                if (IsLocalImage(path))
                    items.Add(new ProcessWorkItem { LocalPath = path, FileName = Path.GetFileName(path) });
            }
            SynapicLog.Info(nameof(WorkflowRunner), $"Found {items.Count} image files in {ds.LocalPath} (recursive={ds.LocalRecursive})");
            return items;
        }

        var client = ds.DaminionClient ?? throw new InvalidOperationException("Daminion client not connected");
        var startIndex = 0;
        // "Process all" (or a zero/negative max) lifts the ceiling entirely: the
        // only stopping condition is an empty page from the server. A whole-scope
        // fetch (dedup/similar) always lifts it, whatever Step 1 holds.
        var processAll = entireScope || ds.ProcessAll || ds.MaxItems <= 0;
        var limit = processAll ? int.MaxValue : ds.MaxItems;
        var lastPageIds = new HashSet<int>();
        // Catalog ids already queued. Collection and saved-search pages are
        // filtered on this side and the cursor advances by the raw page, so a
        // retried or overlapping page can resurface an id; the set keeps one
        // work item per catalog record.
        var seenIds = new HashSet<int>();
        var pagesWalked = 0;

        while (items.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var page = await client.GetItemsPageAsync(
                scope: ds.Scope,
                savedSearchId: ds.SavedSearchId,
                collectionId: ds.CollectionId,
                searchTerm: ds.SearchTerm,
                untaggedFields: ds.UntaggedFields,
                statusFilter: ds.StatusFilter,
                // Ask for a full server page even near the ceiling: the eligible
                // count is capped below, and a page shorter than the requested
                // size is what signals the catalog ended.
                maxItems: DaminionApiClient.PageSize,
                startIndex: startIndex,
                ct: ct).ConfigureAwait(false);

            // Only a completely empty raw page is the end of the result set. A
            // page whose records were all filtered out is not: later raw pages can
            // still hold eligible items, so keep advancing past it.
            if (page.RawCount == 0) break;

            // Infinite-loop guard (processing.py parity): if the server returns
            // the same raw ids for every offset (e.g. the untagged filter is not
            // applied server-side), stop instead of looping forever.
            var pageIds = page.RawItems.Select(i => i.Id).ToHashSet();
            if (pageIds.Count > 0 && pageIds.SetEquals(lastPageIds))
            {
                SynapicLog.Warning(nameof(WorkflowRunner),
                    "Daminion returned the same ids as the previous page — stopping pagination to avoid an infinite loop");
                break;
            }
            lastPageIds = pageIds;

            foreach (var item in page.Items)
            {
                if (items.Count >= limit) break;
                if (!seenIds.Add(item.Id)) continue;
                items.Add(new ProcessWorkItem
                {
                    DaminionId = item.Id,
                    FileName = item.FileName ?? $"Item {item.Id}",
                    ServerHashCode = item.HashCode ?? 0,
                    SizeBytes = item.FileSize ?? 0,
                });
            }

            // Every page boundary is a progress point: on a large catalog the
            // walk itself can take a while, and a scan that shows no movement
            // looks hung. A filtered page keeps the same kept count, so it is
            // reported too — the raw cursor is what advanced. `Scanned` is taken
            // before the cursor moves so it names the position just reached.
            pagesWalked++;
            progress?.Report(new CatalogFetchProgress(
                items.Count, startIndex + page.RawCount, pagesWalked, page.TotalCount));
            SynapicLog.Info(nameof(WorkflowRunner),
                $"Daminion page at {startIndex}: {page.RawCount} record(s), {items.Count} kept so far" +
                (page.TotalCount is { } scopeTotal ? $" of {scopeTotal} in scope" : ""));

            // Advance by the raw page the server returned, not by the eligible
            // subset we kept: advancing by the smaller filtered count would skip
            // the raw records the filter dropped and overlap the next page.
            startIndex += page.RawCount;

            // Process-all never treats a partial page as the end: some endpoints
            // cap a single response, so keep requesting until a page is empty.
            if (processAll) continue;
            if (!ds.AutoPaginate || page.RawCount < DaminionApiClient.PageSize) break;
        }

        SynapicLog.Info(nameof(WorkflowRunner), $"Fetched {items.Count} Daminion items (scope={ds.Scope})");
        return items;
    }

    // ── Run ─────────────────────────────────────────────────────────────────

    /// <summary>Fetch the workflow's items, then run them through <see cref="RunItemsAsync"/>.</summary>
    public async Task<WorkflowRunSummary> RunAsync(
        WorkflowDefinition workflow,
        DatasourceSelection ds,
        IWorkflowItemHandler handler,
        IProgress<ProcessProgress>? progress = null,
        Func<string, Task>? log = null,
        PauseToken? pause = null,
        CancellationToken ct = default)
    {
        var items = await FetchItemsAsync(ds, ct).ConfigureAwait(false);
        // Report the fetched total immediately: with a cold model the first
        // item can take minutes, and the UI must not sit at an empty bar.
        progress?.Report(new ProcessProgress(0, 0, items.Count, 0, null, "Starting…"));
        return await RunItemsAsync(workflow, items, handler, progress, log, pause, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Run an explicit work-item set: per item — pause/cancel checks, the
    /// handler's operation, progress with ETA, error isolation. One bad item
    /// never aborts the batch; cancellation propagates as
    /// <see cref="OperationCanceledException"/> (callers report the abort).
    /// </summary>
    public async Task<WorkflowRunSummary> RunItemsAsync(
        WorkflowDefinition workflow,
        IReadOnlyList<ProcessWorkItem> items,
        IWorkflowItemHandler handler,
        IProgress<ProcessProgress>? progress = null,
        Func<string, Task>? log = null,
        PauseToken? pause = null,
        CancellationToken ct = default)
    {
        var total = items.Count;
        var processed = 0;
        var failed = 0;
        var countLock = new object();
        var startedAt = Stopwatch.StartNew();
        var description = workflow.Description is { } d ? $" — {d}" : "";
        var logLine = log ?? (_ => Task.CompletedTask);

        // Say what will happen before the first item runs. The model returns
        // all three fields whatever this says; only the write step honours it,
        // so a partial selection used to look exactly like the model having
        // stopped producing fields.
        await logLine($"Starting batch: {total} items{description}").ConfigureAwait(false);
        SynapicLog.Info(nameof(WorkflowRunner),
            $"{workflow.Key} batch started: {total} items, {workflow.MaxDegreeOfParallelism} in parallel{description}");

        using var throttle = new SemaphoreSlim(Math.Max(1, workflow.MaxDegreeOfParallelism));
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
                lock (countLock) { doneSnapshot = processed; failedSnapshot = failed; }
                var startEstimate = Estimate(startedAt, doneSnapshot, total);
                progress?.Report(new ProcessProgress(
                    doneSnapshot, failedSnapshot, total,
                    total == 0 ? 0 : 100.0 * doneSnapshot / total,
                    startEstimate.Eta, item.FileName, startEstimate.PerItem));

                var outcome = await handler.ProcessAsync(item, ct).ConfigureAwait(false);
                lock (countLock)
                {
                    processed++;
                    if (!outcome.Success) failed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }                catch (Exception e)
                {
                    lock (countLock)
                    {
                        processed++;
                        failed++;
                    }
                await logLine($"Failed: {item.FileName} — {e.Message}").ConfigureAwait(false);
                SynapicLog.Error(nameof(WorkflowRunner), $"Failed to process '{item.FileName}': {e}");
            }
            finally
            {
                throttle.Release();

                lock (countLock)
                {
                    var done = processed;
                    var estimate = Estimate(startedAt, done, total);
                    progress?.Report(new ProcessProgress(
                        done, failed, total,
                        total == 0 ? 0 : 100.0 * done / total,
                        estimate.Eta, item.FileName, estimate.PerItem));
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        await logLine($"Batch finished: {processed - failed} ok, {failed} failed, {total} total").ConfigureAwait(false);
        SynapicLog.Info(nameof(WorkflowRunner),
            $"{workflow.Key} batch finished: {processed - failed} ok, {failed} failed, {total} total");
        return new WorkflowRunSummary(total, processed, processed - failed, failed);
    }

    // ── Progress estimate (ported from processing.py progress_callback) ─────

    /// <summary>Time-based ETA math, separated out so it is directly testable.</summary>
    public static (TimeSpan? Eta, TimeSpan? PerItem) EstimateProgress(TimeSpan elapsed, int processed, int total)
    {
        if (processed <= 0 || total <= 0) return (null, null);
        var perItem = TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / processed);
        return (perItem * Math.Max(total - processed, 0), perItem);
    }

    private static (TimeSpan? Eta, TimeSpan? PerItem) Estimate(Stopwatch stopwatch, int processed, int total)
        => EstimateProgress(stopwatch.Elapsed, processed, total);

    /// <summary>
    /// Time-based ETA for a paged catalog walk: the average cost of a page so
    /// far × the pages the scope total still implies.
    /// </summary>
    /// <remarks>
    /// Pages are the natural unit here — one request/response per `PageSize`
    /// (500) records — so a long walk gets an estimate after its first page
    /// rather than after its first 500 hashes. A server that reports no scope
    /// total (or a cursor already past it) yields a per-page figure but no ETA;
    /// the caller then shows the running count without inventing a finish time.
    /// </remarks>
    public static (TimeSpan? Eta, TimeSpan? PerPage) EstimateCatalogFetchProgress(
        TimeSpan elapsed, int pages, int scanned, int? total)
    {
        if (pages <= 0) return (null, null);
        var perPage = TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds / pages);
        if (total is not { } scopeTotal || scopeTotal <= scanned) return (null, perPage);
        var remainingPages = (int)Math.Ceiling((scopeTotal - scanned) / (double)DaminionApiClient.PageSize);
        return (perPage * remainingPages, perPage);
    }
}
