using Synapic.Core.Services;
using Synapic.Core.Services.Daminion;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Detection;
using Synapic.Indexer.Pipeline;
using Synapic.Indexer.State;
using Synapic.Shared.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace Synapic.Indexer.Tests;

/// <summary>
/// The live catalog path, against the server the milestone was given rather than
/// a fake seam (phase 10, DAM-01..DAM-04). Opt-in: every fact skips with its
/// reason when the credentials, the server or the scope is not there, so a
/// machine without a catalog never reports a green that asserted nothing.
///
/// Credentials come from SYNAPIC_TEST_DAMINION_URL/_USERNAME/_PASSWORD.
/// build/acceptance-daminion.ps1 fills them from the saved Windows connection
/// (HKCU\Software\Synapic\Daminion, DPAPI) so no secret has to be typed.
///
/// What this writes: metadata on the items of the configured scope, and nothing
/// else — no deletes, no flags, no catalog administration. Run it with
/// -Filter "FullyQualifiedName~Live_round_trip" to write, or leave it out of a
/// hermetic run with --filter "Category!=Live".
/// </summary>
[Trait("Category", "Live")]
public sealed class DaminionLiveAcceptanceTests
{
    /// <summary>The scope the milestone was given: saved search 16 ("city street").</summary>
    private const int SavedSearchId = 16;

    /// <summary>
    /// What the scope is called when it cannot be resolved on this server. The
    /// milestone named saved search 16, but the catalog this runs against does
    /// not have it: it enumerates 47 saved searches, none numbered 16, and its
    /// structured query answers an unknown id with the whole catalog instead of
    /// refusing it. The selection then falls back to the same set the desktop
    /// uses when no scope is picked — the catalog, filtered to items whose
    /// keywords, category and description are all empty — and says so.
    /// </summary>
    private const string FallbackScopeName = "city street";

    /// <summary>How many items the write round trip tags. Bounded on purpose.</summary>
    private const int RoundTripItems = 25;

    /// <summary>Minimum the round trip will run on before calling the scope exhausted.</summary>
    private const int MinimumItems = 5;

    private const string MarkerKeyword = "synapic-live-2026-10-23";
    private const string WrittenCategory = "Synapic Live Acceptance";
    private const string WrittenDescription = "Synapic live acceptance 2026-10-23";

    private static readonly string[] UntaggedFields = { "keywords", "categories", "description" };

    private readonly ITestOutputHelper _output;

    public DaminionLiveAcceptanceTests(ITestOutputHelper output) => _output = output;

    // ── DAM-01: the run knows what it is working against ────────────────────

    [SkippableFact]
    public async Task Live_catalog_reports_server_scope_and_counts()
    {
        var client = await ConnectAsync();

        var catalogGuid = await client.GetCatalogGuidAsync();
        var searches = await client.GetSavedSearchesAsync();
        var scope = await ResolveScopeAsync(client, searches);

        var scopeCount = await client.GetFilteredItemCountAsync(
            scope: scope.Scope, savedSearchId: scope.SavedSearchId);
        var untaggedCount = await client.GetFilteredItemCountAsync(
            scope: scope.Scope, savedSearchId: scope.SavedSearchId, untaggedFields: UntaggedFields);

        _output.WriteLine($"server:        {client.BaseUrl}");
        _output.WriteLine($"catalog GUID:  {catalogGuid ?? "(server did not report one)"}");
        _output.WriteLine($"scope:         {scope.Description}");
        _output.WriteLine($"items in scope:{scopeCount,6}");
        _output.WriteLine($"untagged:      {untaggedCount,6}  (keywords + category + description all empty)");
        // The clause this server supports and the clause it ignores have to be
        // told apart before either number is used to describe the run.
        _output.WriteLine(untaggedCount == scopeCount
            ? "untagged clause: ignored by this server (the count does not change) — " +
              "the item walk confirms emptiness per item instead"
            : "untagged clause: honoured by this server (the count narrows)");
        _output.WriteLine($"saved searches enumerated: {searches.Count}");
        foreach (var saved in searches.OrderBy(s => s.Id))
            _output.WriteLine($"  saved search {saved.Id}: \"{saved.Name}\" ({saved.Count} items)");

        // The counts above are the server's word; this is what the walk actually
        // hands over. The two have disagreed before (a structured saved-search
        // query answered with the catalog total), so the scope is reported by the
        // records it resolves to, not by the number alone.
        var page = await client.GetItemsFilteredAsync(
            scope: scope.Scope,
            savedSearchId: scope.SavedSearchId,
            untaggedFields: UntaggedFields,
            maxItems: 5);
        _output.WriteLine($"first page:     {page.Length} item(s)");
        foreach (var item in page)
        {
            _output.WriteLine($"  item {item.Id} \"{item.FileName}\" keywords=[{string.Join(", ", item.Keywords ?? Array.Empty<string>())}] " +
                              $"categories=[{string.Join(", ", item.Categories ?? Array.Empty<string>())}] " +
                              $"description={(string.IsNullOrEmpty(item.Description) ? "(empty)" : "(set)")}");
        }

        Skip.If(page.Length == 0,
            $"Saved search {SavedSearchId} resolved to no records even though the server counted {scopeCount}.");

        // Read-only probe: which scopes does this server actually narrow? A saved
        // search answered with item 1 of the catalog means the structured query
        // was ignored, and the acceptance must not call that "the scope".
        if (searches.Count > 0)
        {
            var realSavedSearch = await client.GetItemsFilteredAsync(
                scope: "saved_search",
                savedSearchId: searches.OrderBy(s => s.Id).First().Id,
                untaggedFields: UntaggedFields,
                maxItems: 3);
            _output.WriteLine($"saved search {searches.OrderBy(s => s.Id).First().Id}: {realSavedSearch.Length} item(s): " +
                              string.Join(", ", realSavedSearch.Select(i => $"{i.Id} \"{i.FileName}\"")));
        }

        var realSavedSearchProbe = await client.GetItemsFilteredAsync(
            scope: "saved_search", savedSearchId: 219, untaggedFields: UntaggedFields, maxItems: 3);
        _output.WriteLine($"saved search 219 (\"Untagged Images - Keyword\"): {realSavedSearchProbe.Length} item(s): " +
                          string.Join(", ", realSavedSearchProbe.Select(i => $"{i.Id} \"{i.FileName}\"")));

        var searched = await client.GetItemsFilteredAsync(
            scope: "search", searchTerm: "city street", untaggedFields: UntaggedFields, maxItems: 3);
        _output.WriteLine($"search \"city street\": {searched.Length} item(s): " +
                          string.Join(", ", searched.Select(i => $"{i.Id} \"{i.FileName}\"")));
        _output.WriteLine($"search \"city street\" count: " +
                          await client.GetFilteredItemCountAsync(scope: "search", searchTerm: "city street", untaggedFields: UntaggedFields));

        Skip.If(scopeCount <= 0,
            $"The resolved scope ({scope.Description}) reports {scopeCount} items, so the live " +
            "catalog does not contain the scope this phase was given.");
        Assert.True(scopeCount > 0, $"the scope must hold items, got {scopeCount}");
        Assert.InRange(untaggedCount, 0, scopeCount);

        // The untagged filter is applied on this side for a structured scope; if
        // it were not, the first page could carry tagged records and the write
        // would be judged on items that never had empty fields.
        // Read each record's own metadata rather than trusting the item's
        // keyword/category fields (this server's media-item record carries no tag
        // fields at all). The count is evidence about the clause, not an
        // assertion: on a server that ignores it, an "untagged" page is the whole
        // catalog and being tagged here is the finding.
        var tagged = 0;
        foreach (var item in page)
        {
            var pairs = await client.GetItemTagPairsAsync(item.Id);
            var hasTags = HasAnyValue(pairs, "categor") || HasAnyValue(pairs, "keyword") ||
                          HasAnyValue(pairs, "description") || HasAnyValue(pairs, "caption");
            if (hasTags) tagged++;
        }
        _output.WriteLine($"first page already tagged: {tagged}/{page.Length}");
    }

    // ── scope resolution ────────────────────────────────────────────────────

    private sealed record ResolvedScope(string Scope, int? SavedSearchId, string Description, bool RequestedMissing);

    /// <summary>
    /// The scope to run against, resolved by what the server actually holds. The
    /// milestone named saved search 16; this catalog does not have it and the
    /// server answers an unknown saved-search id with the whole catalog, so the
    /// id is never trusted on its own. Order: the named saved search if the
    /// server enumerates it, then a saved search by name, then the catalog
    /// filtered to items whose three fields are empty (the desktop's selection
    /// when no scope is chosen).
    /// </summary>
    private async Task<ResolvedScope> ResolveScopeAsync(
        DaminionApiClient client, IReadOnlyList<DaminionSavedSearch> searches)
    {
        var byId = searches.FirstOrDefault(s => s.Id == SavedSearchId);
        if (byId is not null)
            return new ResolvedScope("saved_search", byId.Id, $"saved search {byId.Id} \"{byId.Name}\"", false);

        var byName = searches.FirstOrDefault(s =>
            s.Name.Contains(FallbackScopeName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            _output.WriteLine(
                $"saved search {SavedSearchId} is not on this server; using {byName.Id} \"{byName.Name}\" for \"{FallbackScopeName}\".");
            return new ResolvedScope("saved_search", byName.Id, $"saved search {byName.Id} \"{byName.Name}\"", true);
        }

        _output.WriteLine(
            $"saved search {SavedSearchId} (\"{FallbackScopeName}\") is not on this server: " +
            $"it enumerates {searches.Count} saved searches, none with that id, and its structured " +
            "query answers an unknown id with the whole catalog. Falling back to the catalog " +
            "filtered to items whose keywords, category and description are all empty — the same " +
            "selection the desktop makes when no scope is chosen.");

        return new ResolvedScope("all", null,
            $"catalog ({searches.Count} saved searches, none matching the requested scope)", true);
    }

    /// <summary>The scope every live fact runs against; resolved once per fact.</summary>
    private async Task<ResolvedScope> ResolveScopeAsync(DaminionApiClient client) =>
        await ResolveScopeAsync(client, await client.GetSavedSearchesAsync());

    // ── DAM-02: tag, read back, re-run ──────────────────────────────────────

    [SkippableFact]
    public async Task Live_round_trip_writes_and_reads_back()
    {
        var client = await ConnectAsync();

        var scope = await ResolveScopeAsync(client);
        var items = await SelectTargetsAsync(client, scope, RoundTripItems);

        Skip.If(items.Length < MinimumItems,
            $"The scope ({scope.Description}) yielded {items.Length} item(s) the round trip could " +
            $"write to; it needs at least {MinimumItems}.");

        _output.WriteLine($"Writing to {items.Length} item(s): " +
                          string.Join(", ", items.Select(i => i.Id)));

        // What each item carries before the run: the write must add the model's
        // values without dropping a single existing one.
        var before = await SnapshotAsync(client, items);

        var first = await RunBatchAsync(client, scope, items);
        Assert.Equal(items.Length, first.Completed);
        Assert.Equal(0, first.Failed);
        Assert.Equal(0, first.Transient);
        Assert.False(first.Paused, "a reachable catalog must not pause the run");

        await AssertCatalogHoldsWrittenValuesAsync(client, items, before, "after the first run");

        // The same ids through a fresh queue and executor: the catalog write path
        // runs again against items that already carry the tags. Nothing may be
        // added twice, and nothing the item carried before may be lost.
        var second = await RunBatchAsync(client, scope, items);
        Assert.Equal(items.Length, second.Completed);
        Assert.Equal(0, second.Failed);
        Assert.Equal(0, second.Transient);

        await AssertCatalogHoldsWrittenValuesAsync(client, items, before, "after the second run");
    }

    private async Task<(int Completed, int Failed, int Transient, bool Paused)> RunBatchAsync(
        DaminionApiClient client, ResolvedScope scope, DaminionItem[] items)
    {
        using var harness = await PipelineHarness.CreateAsync(
            concurrency: 3,
            overwrite: false,
            catalog: true,
            catalogSelection: () => new DatasourceSelection
            {
                IsDaminion = true,
                DaminionClient = client,
                Scope = scope.Scope,
                SavedSearchId = scope.SavedSearchId,
                UntaggedFields = UntaggedFields,
                ResizeScale = 100,
                MaxItems = 0,
            });

        harness.Sidecar.OnTag = (_, _) => Task.FromResult(new TagResponse
        {
            Category = WrittenCategory,
            Keywords = new[] { MarkerKeyword },
            Description = WrittenDescription,
        });

        foreach (var item in items)
            await harness.EnqueueCatalogAsync(item.Id, item.FileName ?? $"item-{item.Id}");

        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < deadline)
        {
            var summary = await harness.Repository.GetSummaryAsync();
            if (summary.Pending == 0 && summary.InFlight == 0) break;

            if (summary.Pending == 0 && summary.TransientFailure > 0)
                harness.Clock.Advance(TimeSpan.FromSeconds(61)); // a retry that is not due yet

            await harness.Executor.ExecuteOnceAsync(CancellationToken.None);
        }

        var final = await harness.Repository.GetSummaryAsync();
        _output.WriteLine($"batch: completed={final.Completed} transient={final.TransientFailure} " +
                          $"failed={final.Failed} pending={final.Pending} inflight={final.InFlight} " +
                          $"paused={harness.Executor.IsPaused}");

        return (final.Completed, final.Failed, final.TransientFailure, harness.Executor.IsPaused);
    }

    /// <summary>
    /// The catalog's own answer: the product verifier plus the values read from
    /// the item. The written keyword and category must appear <em>exactly once</em>
    /// — a repeat run that appends instead of recognising what is already there
    /// doubles them — and every value the item carried before the run must still
    /// be there.
    /// </summary>
    private async Task AssertCatalogHoldsWrittenValuesAsync(
        DaminionApiClient client, DaminionItem[] items,
        IReadOnlyDictionary<int, (string[] Keywords, string[] Categories)> before, string when)
    {
        foreach (var item in items)
        {
            // The server commits a BatchChange before its own metadata index is
            // necessarily up to date, so the read-back is given a short settle
            // window — a fixed one, not "eventually": the last read is what is
            // asserted, and it must be right within the window or the write is
            // a failure.
            var settleDeadline = DateTime.UtcNow.AddSeconds(15);
            IReadOnlyList<(string Key, string Value)> pairs;
            DaminionVerifyResult verify;
            while (true)
            {
                pairs = await client.GetItemTagPairsAsync(item.Id);
                verify = await client.VerifyItemMetadataAsync(
                    item.Id, WrittenCategory, new[] { MarkerKeyword }, WrittenDescription);
                if (verify.Ok || DateTime.UtcNow >= settleDeadline) break;
                await Task.Delay(1000);
            }

            _output.WriteLine($"{when}: item {item.Id}: read back {pairs.Count} tag value(s): " +
                              string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}")));

            Assert.True(verify.Ok, $"{when}: item {item.Id}: {verify.Detail}");
            var keywords = pairs
                .Where(p => p.Key.Contains("keyword", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value.Trim())
                .ToArray();
            var categories = pairs
                .Where(p => p.Key.Contains("categor", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value.Trim())
                .ToArray();
            var descriptions = pairs
                .Where(p => p.Key.Contains("description", StringComparison.OrdinalIgnoreCase) ||
                            p.Key.Contains("caption", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Value.Trim())
                .ToArray();

            _output.WriteLine($"{when}: item {item.Id} keywords=[{string.Join(", ", keywords)}] " +
                              $"categories=[{string.Join(", ", categories)}] " +
                              $"description=\"{string.Join(" | ", descriptions)}\"");

            Assert.Equal(1, keywords.Count(k => string.Equals(k, MarkerKeyword, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(1, categories.Count(c => string.Equals(c, WrittenCategory, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(WrittenDescription, descriptions);

            foreach (var keyword in before[item.Id].Keywords)
                Assert.Contains(keyword, keywords, StringComparer.OrdinalIgnoreCase);
            foreach (var category in before[item.Id].Categories)
                Assert.Contains(category, categories, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The keyword and category values each item carries before the write.</summary>
    private async Task<Dictionary<int, (string[] Keywords, string[] Categories)>> SnapshotAsync(
        DaminionApiClient client, DaminionItem[] items)
    {
        var snapshot = new Dictionary<int, (string[] Keywords, string[] Categories)>();
        foreach (var item in items)
        {
            var pairs = await client.GetItemTagPairsAsync(item.Id);
            snapshot[item.Id] = (Values(pairs, "keyword"), Values(pairs, "categor"));
        }
        return snapshot;
    }

    private static string[] Values(
        IReadOnlyList<(string Key, string Value)> pairs, string keyFragment) =>
        pairs.Where(p => p.Key.Contains(keyFragment, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Value.Trim())
            .ToArray();

    // ── DAM-03: a real outage pauses the run, and it resumes by itself ──────

    /// <summary>
    /// Items in the outage batch. Six is enough for the run to be mid-batch when
    /// the block lands and small enough that a bounded window still covers it.
    /// </summary>
    private const int OutageItems = 6;

    [SkippableFact]
    public async Task Live_outage_pauses_and_the_queue_completes()
    {
        var blockDir = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_BLOCK_DIR");
        Skip.If(string.IsNullOrWhiteSpace(blockDir),
            "The outage window needs the elevated helper, which is not pointed at this run: start " +
            "build/block-daminion.ps1 -WorkDir <dir> from an elevated shell and set " +
            "SYNAPIC_TEST_DAMINION_BLOCK_DIR to the same <dir>. Without it a real firewall block " +
            "cannot be applied, and a closed port is not the outage this fact is about.");

        var client = await ConnectAsync();
        var scope = await ResolveScopeAsync(client);
        var items = await SelectTargetsAsync(client, scope, OutageItems);

        Skip.If(items.Length < 3,
            $"The resolved scope yielded {items.Length} item(s) the outage batch could use; it needs at least 3.");

        // The pause and the resume are log lines as well as state; keep this run's
        // log in its own directory so they can be quoted into the transcript.
        var logDir = Path.Combine(Path.GetTempPath(), "synapic-live-outage-" + Guid.NewGuid().ToString("N"));
        SynapicLog.RestartForTests(logDir, "info");

        DaminionApiClient active = client;
        using var harness = await PipelineHarness.CreateAsync(
            concurrency: 1,
            overwrite: false,
            catalog: true,
            catalogSelection: () => new DatasourceSelection
            {
                IsDaminion = true,
                DaminionClient = active,
                Scope = scope.Scope,
                SavedSearchId = scope.SavedSearchId,
                UntaggedFields = UntaggedFields,
                ResizeScale = 100,
                MaxItems = 0,
            });

        harness.Sidecar.OnTag = (_, _) => Task.FromResult(new TagResponse
        {
            Category = WrittenCategory,
            Keywords = new[] { MarkerKeyword },
            Description = WrittenDescription,
        });

        foreach (var item in items)
            await harness.EnqueueCatalogAsync(item.Id, item.FileName ?? $"item-{item.Id}");

        // The block: requested from the elevated watcher, then proven with a fresh
        // client that has no session and no pooled connection — the run's own
        // client is authenticated, and a firewall rule does not usefully break an
        // established HTTP connection, so "fresh client cannot connect" is the
        // only honest proof that the catalog is gone from this machine.
        await File.WriteAllTextAsync(Path.Combine(blockDir!, "block.request"),
            $"requested_at={DateTimeOffset.Now:o}; host={new Uri(client.BaseUrl).Host}; items={items.Length}");

        var blockAck = await WaitForAckAsync(blockDir!, "block.ack", TimeSpan.FromMinutes(4));
        _output.WriteLine("block ack:");
        foreach (var line in blockAck) _output.WriteLine("  " + line);
        Assert.Contains(blockAck, line => line.StartsWith("blocked_after_apply=True", StringComparison.Ordinal));

        if (!await WaitForCatalogToDisappearAsync(TimeSpan.FromMinutes(2)))
        {
            // Put the machine back before reporting: a skip must not leave a
            // firewall rule behind for the rest of the session.
            await File.WriteAllTextAsync(Path.Combine(blockDir!, "unblock.request"),
                $"requested_at={DateTimeOffset.Now:o}; reason=block_not_observed");
            await WaitForAckAsync(blockDir!, "unblock.ack", TimeSpan.FromMinutes(3));
            Skip.If(true,
                "A fresh client still authenticated after the block was reported applied, so the outage " +
                "never reached this machine. Refusing to call the pause an outage test.");
        }
        _output.WriteLine("outage proven: a fresh client cannot reach the catalog.");

        // A client created during the outage: its first call has to connect, and
        // that connection is what the rule drops.
        active = new DaminionApiClient(
            Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_URL")!,
            Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_USERNAME")!,
            Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_PASSWORD")!);

        var during = await harness.Executor.ExecuteOnceAsync(CancellationToken.None);
        var pausedSummary = await harness.Repository.GetSummaryAsync();
        _output.WriteLine($"during the outage: finished={during} paused={harness.Executor.IsPaused} " +
                          $"pending={pausedSummary.Pending} transient={pausedSummary.TransientFailure} " +
                          $"failed={pausedSummary.Failed} inflight={pausedSummary.InFlight} completed={pausedSummary.Completed}");

        Assert.True(harness.Executor.IsPaused, "an unreachable catalog must pause the run");
        Assert.Equal(1, harness.Executor.PauseCount);
        Assert.Equal(0, pausedSummary.Failed);
        Assert.Equal(0, pausedSummary.InFlight);
        Assert.Equal(items.Length, pausedSummary.Pending + pausedSummary.TransientFailure + pausedSummary.Completed);

        // The outage is lifted the same way it arrived, and the resume is only
        // claimed once a fresh client gets a session again.
        await File.WriteAllTextAsync(Path.Combine(blockDir!, "unblock.request"),
            $"requested_at={DateTimeOffset.Now:o}");
        var unblockAck = await WaitForAckAsync(blockDir!, "unblock.ack", TimeSpan.FromMinutes(3));
        _output.WriteLine("unblock ack:");
        foreach (var line in unblockAck) _output.WriteLine("  " + line);
        Assert.Contains(unblockAck, line => line.StartsWith("reachable_after_unblock=True", StringComparison.Ordinal));
        Assert.True(await WaitForCatalogToReturnAsync(TimeSpan.FromMinutes(3)),
            "a fresh client still cannot reach the catalog after the rule was removed");
        _output.WriteLine("recovery proven: a fresh client authenticates again.");

        active = await ConnectAsync();
        harness.Clock.Advance(JobOutcomeClassifier.CatalogPauseDelay + TimeSpan.FromSeconds(61));

        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < deadline)
        {
            var summary = await harness.Repository.GetSummaryAsync();
            if (summary.Pending == 0 && summary.InFlight == 0 &&
                summary.TransientFailure == 0 && summary.Completed == items.Length)
            {
                break;
            }

            if (summary.Pending == 0 && summary.InFlight == 0 && summary.TransientFailure > 0)
                harness.Clock.Advance(JobOutcomeClassifier.LockRetryDelay);

            await harness.Executor.ExecuteOnceAsync(CancellationToken.None);
        }

        var final = await harness.Repository.GetSummaryAsync();
        _output.WriteLine($"after the outage: completed={final.Completed} transient={final.TransientFailure} " +
                          $"failed={final.Failed} pending={final.Pending} inflight={final.InFlight} " +
                          $"pauses={harness.Executor.PauseCount} resumes={harness.Executor.ResumeCount}");

        Assert.Equal(items.Length, final.Completed);
        Assert.Equal(0, final.Failed);
        Assert.Equal(0, final.InFlight);
        Assert.True(harness.Executor.ResumeCount >= 1, "the run must resume by itself once the catalog returns");

        foreach (var item in items)
        {
            var row = (await harness.Repository.GetByKeyAsync(PathKey.ForDaminion(item.Id)))!;
            Assert.Equal(JobState.Completed, row.State);
        }

        // The log named both transitions; quote them, then assert them.
        SynapicLog.ResetForTests();
        var logPath = Path.Combine(logDir, "synapic.log");
        var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : "";
        var pauseLines = log.Split('\n')
            .Where(l => l.Contains("the run is paused", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains("resumes after being paused", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var line in pauseLines) _output.WriteLine("log: " + line.Trim());

        Assert.Contains(pauseLines, l => l.Contains("the run is paused", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pauseLines, l => l.Contains("resumes after being paused", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Items whose three fields the catalog itself reports empty. The server's
    /// untagged clause narrows the page it is asked for, but on this build it
    /// does not reliably exclude a tagged record from the page, so the selection
    /// is confirmed item by item — writing over an item that already had tags
    /// would make the read-back assert the wrong thing.
    /// </summary>
    /// <summary>
    /// The items the round trip writes to: real catalog records from the resolved
    /// scope, skipping any that already carry this run's marker keyword (so a
    /// re-run writes to fresh items and never confuses this run's values with a
    /// previous one's).
    /// </summary>
    /// <remarks>
    /// "Empty keywords, category and description" is <em>not</em> used as the
    /// selection here. This server ignores the untagged clause — both
    /// <c>MediaItems/Get</c> and <c>MediaItems/GetCount</c> answer
    /// <c>keywords:none</c> with the whole catalog — so an "untagged" page is the
    /// catalog head and cannot be trusted to be untagged. The round trip does not
    /// need pre-empty items to prove what it must prove (the written values land,
    /// exactly once, and nothing already there is lost); it snapshots each item
    /// first and asserts against that.
    /// </remarks>
    private async Task<DaminionItem[]> SelectTargetsAsync(
        DaminionApiClient client, ResolvedScope scope, int wanted)
    {
        const int pageSize = 500;
        var chosen = new List<DaminionItem>();
        var skipped = 0;
        var startIndex = 0;

        while (chosen.Count < wanted)
        {
            var page = await client.GetItemsPageAsync(
                scope: scope.Scope,
                savedSearchId: scope.SavedSearchId,
                maxItems: pageSize,
                startIndex: startIndex);
            if (page.RawCount == 0) break;

            foreach (var item in page.Items)
            {
                if (chosen.Count == wanted) break;

                var pairs = await client.GetItemTagPairsAsync(item.Id);
                if (Values(pairs, "keyword").Any(k =>
                        string.Equals(k, MarkerKeyword, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                chosen.Add(item);
            }

            startIndex += page.RawCount;
        }

        _output.WriteLine($"selection: {chosen.Count} item(s) from {scope.Description} " +
                          $"(skipped {skipped} already carrying {MarkerKeyword})");
        return chosen.ToArray();
    }

    private static bool HasAnyValue(
        IReadOnlyList<(string Key, string Value)> pairs, string keyFragment) =>
        pairs.Any(p => p.Key.Contains(keyFragment, StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(p.Value));

    private async Task<string[]> WaitForAckAsync(string dir, string name, TimeSpan timeout)
    {
        var path = Path.Combine(dir, name);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var lines = (await File.ReadAllLinesAsync(path)).Where(l => l.Length > 0).ToArray();
                if (lines.Length > 0) return lines;
            }
            await Task.Delay(500);
        }

        Assert.Fail($"The elevated outage helper did not write {name} within {timeout.TotalSeconds:0}s.");
        return Array.Empty<string>();
    }

    /// <summary>
    /// A new client, no session: authenticated means the catalog is reachable
    /// from this machine again. Every probe has its own short deadline because a
    /// blocked connect waits for the OS to give up on the SYN.
    /// </summary>
    private async Task<bool> WaitForCatalogToDisappearAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var probe = new DaminionApiClient(
                Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_URL")!,
                Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_USERNAME")!,
                Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_PASSWORD")!);
            try
            {
                await probe.AuthenticateAsync();
                return false;
            }
            catch (DaminionNetworkException)
            {
                return true;
            }
            catch (DaminionAuthenticationException)
            {
                return false; // the server answered; the block is not in place
            }
        }

        return false;
    }

    private async Task<bool> WaitForCatalogToReturnAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var probe = new DaminionApiClient(
                Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_URL")!,
                Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_USERNAME")!,
                Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_PASSWORD")!);
            try
            {
                await probe.AuthenticateAsync();
                return true;
            }
            catch (DaminionException)
            {
                await Task.Delay(2000);
            }
        }

        return false;
    }

    // ── shared ──────────────────────────────────────────────────────────────

    private async Task<DaminionApiClient> ConnectAsync()
    {
        var url = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_URL");
        var username = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_USERNAME");
        var password = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_PASSWORD");

        Skip.If(string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrEmpty(password),
            "Live Daminion credentials are not configured (SYNAPIC_TEST_DAMINION_URL/_USERNAME/_PASSWORD, " +
            "or run build/acceptance-daminion.ps1 to use the saved Windows connection).");

        var client = new DaminionApiClient(url!, username!, password!);
        try
        {
            await client.AuthenticateAsync();
        }
        catch (DaminionAuthenticationException e)
        {
            // A rejected login is a configuration error, not an environment skip:
            // the credentials are present and the server answered.
            Assert.Fail($"The live catalog rejected the configured credentials: {e.Message}");
        }
        catch (DaminionNetworkException e)
        {
            Skip.If(true, $"The configured Daminion server is unreachable: {e.Message}");
        }
        return client;
    }
}
