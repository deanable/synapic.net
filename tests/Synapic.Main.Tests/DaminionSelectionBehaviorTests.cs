using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Synapic.Shared.Contracts;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Core.Services.Daminion;
using Synapic.Core.Services.Processing;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views.Wizard;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

using Synapic.Core.Services;
namespace Synapic.Main.Tests;

/// <summary>Behavioral checks for the filtered catalog selection used by Step 1 and all workflows.</summary>
public sealed class DaminionSelectionBehaviorTests
{
    [Fact]
    public async Task Saved_search_applies_status_and_untagged_filters_to_returned_items()
    {
        using var server = new CatalogServer(index => JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new { id = 1, fileName = "eligible.jpg", flag = 1, Keywords = Array.Empty<string>(), Categories = Array.Empty<string>(), Description = "" },
                new { id = 2, fileName = "rejected.jpg", flag = 2, Keywords = Array.Empty<string>(), Categories = Array.Empty<string>(), Description = "" },
                new { id = 3, fileName = "tagged.jpg", flag = 1, Keywords = new[] { "tree" }, Categories = Array.Empty<string>(), Description = "" },
                new { id = 4, fileName = "categorized.jpg", flag = 1, Keywords = Array.Empty<string>(), Categories = new[] { "landscape" }, Description = "" },
                new { id = 5, fileName = "described.jpg", flag = 1, Keywords = Array.Empty<string>(), Categories = Array.Empty<string>(), Description = "A tree" },
            }, totalCount = 4
        }));
        var client = new DaminionApiClient(server.BaseUrl, "test", "test");
        await client.AuthenticateAsync();

        var items = await client.GetItemsFilteredAsync(scope: "saved_search", savedSearchId: 3,
            statusFilter: "approved", untaggedFields: ["keywords", "categories", "description"], maxItems: 20);

        var selected = Assert.Single(items);
        Assert.Equal(1, selected.Id);
        Assert.Equal("eligible.jpg", selected.FileName);
    }

    [AvaloniaFact]
    public async Task Dedup_scan_runs_against_the_connected_daminion_scope_and_groups_catalog_duplicates()
    {
        using var server = new CatalogServer(index => JsonSerializer.Serialize(new
        {
            items = new[]
            {
                new { id = 11, fileName = "copy-a.jpg", hashCode = 4242, fileSize = 1024 },
                new { id = 12, fileName = "copy-b.jpg", hashCode = 4242, fileSize = 2048 },
            },
            totalCount = 2,
        }));
        var session = new Session();
        session.Datasource.Type = "daminion";
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = server.BaseUrl,
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);

        var vm = new StepDedupViewModel(step1: source);
        var view = new StepDedup { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await vm.ScanCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            var group = Assert.Single(vm.Groups);
            Assert.Equal(new[] { "copy-a.jpg", "copy-b.jpg" }, group.Items.Select(item => item.DisplayName));
            Assert.Equal(new int?[] { 11, 12 }, group.Items.Select(item => item.DaminionId));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.Contains("2 files scanned — 1 groups, 1 duplicates", StringComparison.Ordinal) == true && text.IsVisible);

            // The walk's retrieval numbers reach the view model (and therefore
            // the read-only diagnostics block) even though the count stub has no
            // count body: the page's own total is still reported.
            var retrieval = Assert.IsType<CatalogRetrievalDiagnostics>(vm.LastRetrieval);
            Assert.Equal(2, retrieval.PageTotal);
            Assert.Equal(1, retrieval.Pages);
            Assert.Equal(2, retrieval.Kept);
            Assert.True(vm.HasRetrievalDiagnostics);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    [Fact]
    public async Task Tagging_uses_the_200_pixel_thumbnail_when_override_is_selected()
    {
        using var server = new CatalogServer(_ => "{}", id => Assert.Equal(17, id));
        var client = await Connect(server);
        var folder = Directory.CreateTempSubdirectory("synapic-thumbnail-override-").FullName;
        var requestedImage = "";
        var sidecar = new ThumbnailRecordingSidecar(path =>
        {
            requestedImage = path;
            return Task.CompletedTask;
        });
        try
        {
            var orchestrator = new ProcessingOrchestrator(sidecar, new CapturingMetadataWriter(), maxDegreeOfParallelism: 1);
            var results = new List<ProcessItemResult>();
            var session = new Session();
            session.Datasource.Type = "daminion";
            var source = new Step1DatasourceViewModel(session);
            source.UseThumbnailOverride = true;
            Assert.True(session.Datasource.UseThumbnailOverride);
            await orchestrator.RunItemsAsync(
                source.ToSelectionForProcessing(client),
                new TagRequest { ModelId = "test-model", Task = "image-text-to-text" },
                [new ProcessWorkItem { DaminionId = 17, FileName = "catalog.jpg" }],
                new Progress<ProcessProgress>(), _ => Task.CompletedTask, CancellationToken.None, results);

            Assert.Equal(Path.Combine(client.TempDirectory, "17_thumb_200x200"), requestedImage);
            Assert.Contains(server.Routes, route => route.StartsWith("/api/Thumbnail/Get/17?", StringComparison.Ordinal)
                && route.Contains("width=200", StringComparison.Ordinal)
                && route.Contains("height=200", StringComparison.Ordinal));
            Assert.False(File.Exists(requestedImage)); // the tag handler owns and removes its temporary input
            Assert.Equal("Success", Assert.Single(results).Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A dedup/similar scan must see the whole scope, not a prefix: the catalog
    /// API answers at most 500 records per call, and Step 1's "max items"
    /// ceiling belongs to partial workflows. This walks 1,500 records in three
    /// pages with the ceiling at 100 and auto-pagination switched off.
    /// </summary>
    [Fact]
    public async Task Whole_scope_fetch_walks_every_page_past_the_source_ceiling()
    {
        using var server = new CatalogServer(index => Page(index, index < 1500 ? 500 : 0));
        var client = await Connect(server);
        var reports = new List<CatalogFetchProgress>();

        var items = await WorkflowRunner.FetchEntireScopeAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
            MaxItems = 100,
            AutoPaginate = false,
        }, new RecordingProgress<CatalogFetchProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(1500, items.Count);
        Assert.Equal(new[] { 0, 500, 1000, 1500 }, server.GetIndexes);
        Assert.Equal(1, items[0].DaminionId);
        Assert.Equal(1500, items[^1].DaminionId);

        // Each page boundary is reported, and the final report carries the
        // server's total for the scope, so a long walk can show "1,500 of
        // 1,500". (The stub derives its total from each page; what matters here
        // is that the count the UI sees tracks the walk.)
        Assert.Equal(3, reports.Count);
        Assert.Equal(new[] { 500, 1000, 1500 }, reports.Select(r => r.Scanned).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, reports.Select(r => r.Pages).ToArray());
        Assert.All(reports, r => Assert.NotNull(r.Total));
        Assert.Equal(1500, reports[^1].Total);
    }

    /// <summary>
    /// The count shown for a scope and the pages walked for it must come from
    /// the same query. A status filter used to be counted as a structured
    /// flag-tag clause but fetched as a text clause, so the two could never
    /// agree; both now send the documented text value.
    /// </summary>
    [Fact]
    public async Task Status_count_and_status_pages_ask_the_same_question()
    {
        using var server = new CatalogServer(_ => "{}");
        var client = await Connect(server);

        await client.GetFilteredItemCountAsync(scope: "all", statusFilter: "approved");
        await client.GetItemsFilteredAsync(scope: "all", statusFilter: "approved", maxItems: 10);

        var routes = server.Routes.Select(Uri.UnescapeDataString).ToArray();
        var countRoute = Assert.Single(routes, r => r.StartsWith("/api/MediaItems/GetCount", StringComparison.Ordinal));
        var pageRoute = Assert.Single(routes, r => r.StartsWith("/api/MediaItems/Get?", StringComparison.Ordinal));

        Assert.Contains("search=flag:approved", countRoute, StringComparison.Ordinal);
        Assert.Contains("search=flag:approved", pageRoute, StringComparison.Ordinal);
        Assert.DoesNotContain("flag:flagged", pageRoute, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unfiltered whole-catalog request must ask for every item, not search
    /// for a literal '*'. The page's totalCount then describes the same set the
    /// count does.
    /// </summary>
    [Fact]
    public async Task Unfiltered_all_scope_sends_no_search_wildcard()
    {
        using var server = new CatalogServer(_ => "{}");
        var client = await Connect(server);

        await client.GetItemsFilteredAsync(scope: "all", maxItems: 10);

        var routes = server.Routes.Select(Uri.UnescapeDataString).ToArray();
        var pageRoute = Assert.Single(routes, r => r.StartsWith("/api/MediaItems/Get?", StringComparison.Ordinal));
        Assert.DoesNotContain("search=", pageRoute, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dedup/similar walk's denominator is the scope count, not the page's
    /// totalCount: a server that reports the whole catalog there made a 1,500-item
    /// scope read "500 of 47,851" and left the bar at a few percent forever.
    /// </summary>
    [Fact]
    public async Task Whole_scope_walk_takes_its_total_from_the_scope_count()
    {
        using var server = new CatalogServer(
            index => JsonSerializer.Serialize(new
            {
                items = Enumerable.Range(index + 1, Math.Clamp(1500 - index, 0, 500))
                    .Select(id => new { id, fileName = $"item-{id}.jpg" })
                    .ToArray(),
                // Every page claims the full catalog; the count says 1,500.
                totalCount = 47_851,
            }),
            countBody: _ => "{\"count\":1500}");
        var client = await Connect(server);
        var reports = new List<CatalogFetchProgress>();

        var items = await WorkflowRunner.FetchEntireScopeAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
        }, new RecordingProgress<CatalogFetchProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(1500, items.Count);
        Assert.Equal(new[] { 0, 500, 1000, 1500 }, server.GetIndexes);
        Assert.All(reports, r => Assert.Equal(1500, r.Total));
        Assert.Equal(new[] { 500, 1000, 1500 }, reports.Select(r => r.Scanned).ToArray());
    }

    /// <summary>
    /// A saved-search server may cap its count at 500 yet have more result pages.
    /// If offsets run dry after the first page, continue by page serials when no
    /// count is available to prove the result set has ended.
    /// </summary>
    [Fact]
    public async Task Whole_scope_fetch_continues_with_page_serials_when_offsets_run_dry()
    {
        using var server = new CatalogServer(
            index =>
            {
                // One page per serial: 0..1500 in three pages of 500.
                var start = index * 500;
                return start >= 1500 ? "{}" : Page(start, Math.Min(500, 1500 - start));
            });
        var client = await Connect(server);
        var reports = new List<CatalogFetchProgress>();

        var items = await WorkflowRunner.FetchEntireScopeAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
            MaxItems = 100,
            AutoPaginate = false,
        }, new RecordingProgress<CatalogFetchProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(1500, items.Count);
        Assert.Equal(1, items[0].DaminionId);
        Assert.Equal(1500, items[^1].DaminionId);
        // Page 0, the empty offset page (index 500 is past the end under page
        // serials), then serials 1 and 2 and the final empty serial page.
        Assert.Equal(new[] { 0, 500, 1, 2, 3 }, server.GetIndexes);
        Assert.Equal(1500, reports[^1].Scanned);
        // This server reports an incrementing total while pages are requested;
        // the progress total follows that monotonic high-water mark.
        Assert.Equal(new int?[] { 500, 1000, 1500, 1500 }, reports.Select(r => r.Total).ToArray());
    }

    /// <summary>
    /// The read-only diagnostics view needs all three numbers together: the
    /// count the source panel shows, the total the pages reported and what was
    /// actually kept. A server whose pages claim the catalog size is exactly the
    /// case where reading them side by side explains the progress bar.
    /// </summary>
    [Fact]
    public async Task Whole_scope_walk_reports_count_page_total_scanned_and_kept_together()
    {
        using var server = new CatalogServer(
            index => JsonSerializer.Serialize(new
            {
                items = Enumerable.Range(index + 1, Math.Clamp(1000 - index, 0, 500))
                    .Select(id => new { id, fileName = $"item-{id}.jpg" })
                    .ToArray(),
                totalCount = 47_851,
            }),
            countBody: _ => "{\"count\":1000}");
        var client = await Connect(server);
        var diagnostics = new List<CatalogRetrievalDiagnostics>();

        var items = await WorkflowRunner.FetchEntireScopeAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
        }, diagnostics: new RecordingProgress<CatalogRetrievalDiagnostics>(diagnostics.Add));

        Assert.Equal(1000, items.Count);
        var report = Assert.Single(diagnostics);
        Assert.Equal("all", report.Scope);
        Assert.Equal(1000, report.ScopeCount); // the count that steered the walk
        Assert.Equal(47_851, report.PageTotal); // what the pages themselves claimed
        Assert.Equal(new[] { 0, 500, 1000 }, server.GetIndexes);
        Assert.Equal(2, report.Pages);
        Assert.Equal(1000, report.Scanned);
        Assert.Equal(1000, report.Kept);
        Assert.True(report.CountDrivesTotal);
        Assert.True(report.ReportedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    /// <summary>
    /// The diagnostic re-walk ignores the count as the denominator — the pages'
    /// total steers the bar — but still asks for the count so the report can keep
    /// showing the disagreement that prompted the re-walk.
    /// </summary>
    [Fact]
    public async Task Page_total_rewalk_ignores_the_count_as_the_denominator_but_still_reports_it()
    {
        using var server = new CatalogServer(
            index => JsonSerializer.Serialize(new
            {
                items = Enumerable.Range(index + 1, Math.Clamp(1000 - index, 0, 500))
                    .Select(id => new { id, fileName = $"item-{id}.jpg" })
                    .ToArray(),
                totalCount = 47_851,
            }),
            countBody: _ => "{\"count\":1000}");
        var client = await Connect(server);
        var progress = new List<CatalogFetchProgress>();
        var diagnostics = new List<CatalogRetrievalDiagnostics>();

        var items = await WorkflowRunner.FetchEntireScopeAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
        },
            new RecordingProgress<CatalogFetchProgress>(progress.Add),
            diagnostics: new RecordingProgress<CatalogRetrievalDiagnostics>(diagnostics.Add),
            ignoreScopeCount: true);

        Assert.Equal(1000, items.Count);
        Assert.All(progress, p => Assert.Equal(47_851, p.Total));
        var report = Assert.Single(diagnostics);
        Assert.Equal(1000, report.ScopeCount);
        Assert.Equal(1000, report.Kept);
        Assert.False(report.CountDrivesTotal);
    }

    /// <summary>
    /// An empty scope produces no page progress at all, so the diagnostics report
    /// is the only place its retrieval numbers appear: one report with zero pages
    /// and zero kept, not a missing block.
    /// </summary>
    [Fact]
    public async Task Empty_scope_still_reports_zeroed_retrieval_diagnostics()
    {
        using var server = new CatalogServer(_ => "{}");
        var client = await Connect(server);
        var diagnostics = new List<CatalogRetrievalDiagnostics>();

        var items = await WorkflowRunner.FetchEntireScopeAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
        }, diagnostics: new RecordingProgress<CatalogRetrievalDiagnostics>(diagnostics.Add));

        Assert.Empty(items);
        var report = Assert.Single(diagnostics);
        Assert.Equal(0, report.Pages);
        Assert.Equal(0, report.Scanned);
        Assert.Equal(0, report.Kept);
        Assert.Null(report.PageTotal);
        Assert.False(report.CountDrivesTotal);
    }

    /// <summary>
    /// A keyword scope with an empty box counts the whole filtered catalog, so
    /// the walk must read that same set instead of throwing "unsupported scope".
    /// </summary>
    [Fact]
    public async Task Empty_keyword_scope_walks_the_filtered_catalog_instead_of_failing()
    {
        using var server = new CatalogServer(_ => "{}");
        var client = await Connect(server);

        var items = await WorkflowRunner.FetchItemsAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
            Scope = "search",
            SearchTerm = "",
            ProcessAll = true,
        }, CancellationToken.None);

        Assert.Empty(items); // the stub holds nothing; the request itself must succeed
        var routes = server.Routes.Select(Uri.UnescapeDataString).ToArray();
        var pageRoute = Assert.Single(routes, r => r.StartsWith("/api/MediaItems/Get?", StringComparison.Ordinal));
        Assert.DoesNotContain("search=", pageRoute, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tagging itself must page across the full Daminion scope regardless of
    /// the Step 1 item ceiling. A 1,200-item scope must produce 1,200 sidecar
    /// calls and result rows rather than stopping at the first 500-item response.
    /// </summary>
    [Fact]
    public async Task Tagging_processes_every_item_across_full_pages()
    {
        using var server = new CatalogServer(index => Page(index, index < 1500 ? 500 : 0));
        var client = await Connect(server);
        var sidecar = new CountingTagSidecar();
        var results = new List<ProcessItemResult>();
        var orchestrator = new ProcessingOrchestrator(sidecar, new CapturingMetadataWriter(), maxDegreeOfParallelism: 8);

        await orchestrator.RunAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
            MaxItems = 100,
            ProcessAll = false,
            AutoPaginate = false,
        },
            new TagRequest { ModelId = "test-model", Task = "image-text-to-text" },
            new Progress<ProcessProgress>(), _ => Task.CompletedTask, CancellationToken.None, results);

        Assert.Equal(1500, sidecar.TagCalls);
        Assert.Equal(1500, results.Count);
        Assert.Equal(new[] { 0, 500, 1000, 1500 }, server.GetIndexes);
    }

    [Fact]
    public async Task Upscale_scoped_fetch_still_respects_the_step_one_ceiling()
    {
        using var server = new CatalogServer(index => Page(index, 500));
        var client = await Connect(server);

        var items = await WorkflowRunner.FetchItemsAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
            MaxItems = 100,
        }, CancellationToken.None);

        Assert.Equal(100, items.Count);
        Assert.Equal(new[] { 0 }, server.GetIndexes);
    }

    [Fact]
    public void Daminion_similarity_requires_pixel_hashing()
    {
        var options = new Synapic.Core.Services.Processing.DedupOptions(UseServerHashes: false);
        Assert.False(options.UseServerHashes);
    }

    private sealed class CountingTagSidecar : IInferenceSidecar
    {
        private int _tagCalls;
        public int TagCalls => Volatile.Read(ref _tagCalls);
        public SidecarStatus CurrentStatus => SidecarStatus.Ready;
        public int SidecarPort => 0;
        public bool IsRunning => true;
        public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public event Action<string>? LogReceived { add { } remove { } }
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(TimeSpan? gracefulTimeout = null) => Task.CompletedTask;
        public Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _tagCalls);
            return Task.FromResult(new TagResponse { Category = "Nature", Keywords = ["tree"], Description = "A tree" });
        }
        public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<ModelInfo>());
        public Task DownloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new HealthResponse());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingMetadataWriter : IMetadataWriter
    {
        public Task<bool> WriteAsync(string path, TagResult tags, CancellationToken ct = default) => Task.FromResult(true);
        public Task<TagResult?> ReadAsync(string path, CancellationToken ct = default) => Task.FromResult<TagResult?>(null);
    }

    [Fact]
    public async Task Fetch_respects_max_items_across_full_and_partial_pages()
    {
        using var server = new CatalogServer(index => Page(index, index == 0 ? 500 : 250));
        var client = await Connect(server);

        var items = await WorkflowRunner.FetchItemsAsync(new DatasourceSelection
        {
            IsDaminion = true, DaminionClient = client, MaxItems = 750, AutoPaginate = true,
        }, CancellationToken.None);

        Assert.Equal(750, items.Count);
        Assert.Equal(new[] { 0, 500 }, server.GetIndexes);
        Assert.Equal(1, items[0].DaminionId);
        Assert.Equal(750, items[^1].DaminionId);
    }

    [Fact]
    public async Task Auto_pagination_stops_at_the_requested_item_limit()
    {
        using var server = new CatalogServer(index => Page(index, 500));
        var client = await Connect(server);

        var items = await WorkflowRunner.FetchItemsAsync(new DatasourceSelection
        {
            IsDaminion = true, DaminionClient = client, MaxItems = 100, AutoPaginate = true,
        }, CancellationToken.None);

        Assert.Equal(100, items.Count);
        Assert.Equal(new[] { 0 }, server.GetIndexes);
    }

    [Fact]
    public async Task Process_all_continues_after_a_short_page_then_stops_on_empty_page()
    {
        using var server = new CatalogServer(index => Page(index, index == 0 ? 3 : 0));
        var client = await Connect(server);

        var items = await WorkflowRunner.FetchItemsAsync(new DatasourceSelection
        {
            IsDaminion = true, DaminionClient = client, MaxItems = 1, ProcessAll = true,
        }, CancellationToken.None);

        Assert.Equal(new[] { 0, 3 }, server.GetIndexes);
        Assert.Equal(new[] { 1, 2, 3 }, items.Select(item => item.DaminionId!.Value).ToArray());
    }

    /// <summary>
    /// End-to-end through the dedup screen: a duplicate pair that only appears on
    /// the second catalog page must still be grouped. Step 1 is left at its
    /// shipped ceiling (100 items), so a scan that inherited that limit would
    /// never see id 700 and would report no duplicates at all.
    /// </summary>
    [AvaloniaFact]
    public async Task Dedup_scan_groups_duplicates_found_past_the_first_page()
    {
        using var server = new CatalogServer(index => JsonSerializer.Serialize(new
        {
            items = Enumerable.Range(index + 1, Math.Clamp(1000 - index, 0, 500))
                .Select(id => new
                {
                    id,
                    fileName = $"item-{id}.jpg",
                    // ids 501 and 700 share a hash: only visible once the walk
                    // leaves the first 500-record page.
                    hashCode = id is 501 or 700 ? 777L : 100_000L + id,
                    fileSize = 10,
                })
                .ToArray(),
            totalCount = 1000,
        }));
        var session = new Session();
        session.Datasource.Type = "daminion";
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = server.BaseUrl,
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);
        Assert.Equal(100, source.MaxItems); // the shipped tagging ceiling is in play

        var vm = new StepDedupViewModel(step1: source);
        var view = new StepDedup { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        try
        {
            await vm.ScanCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            var group = Assert.Single(vm.Groups);
            Assert.Equal(new int?[] { 501, 700 }, group.Items.Select(item => item.DaminionId));
            Assert.Equal(new[] { 0, 500, 1000 }, server.GetIndexes);

            // The shared run bar reports the hashing phase it ended in — the
            // catalog walk set it up, then the per-item progress took over and
            // cleared the walk's estimate.
            Assert.Equal("1000/1000 (0 failed)", vm.ProgressText);
            Assert.Equal(100, vm.ProgressPercent);
            Assert.Equal("", vm.EtaText);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Text?.Contains("1000 files scanned — 1 groups, 1 duplicates", StringComparison.Ordinal) == true && text.IsVisible);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    /// <summary>
    /// The diagnostics block's re-walk action on a real catalog scan: when the
    /// count claims more than the pages kept, the action runs the walk again (so
    /// the transient short read gets a second chance) and the new report records
    /// that page totals, not the count, steered it.
    /// </summary>
    [AvaloniaFact]
    public async Task Dedup_reWalk_action_reruns_the_walk_with_page_totals()
    {
        using var server = new CatalogServer(index => index == 0
            ? JsonSerializer.Serialize(new
            {
                items = new[]
                {
                    new { id = 11, fileName = "copy-a.jpg", hashCode = 4242, fileSize = 1024 },
                    new { id = 12, fileName = "copy-b.jpg", hashCode = 4242, fileSize = 2048 },
                },
                totalCount = 2,
            })
            : "{}", countBody: _ => "{\"count\":5}");
        var session = new Session();
        session.Datasource.Type = "daminion";
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = server.BaseUrl,
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);

        var vm = new StepDedupViewModel(step1: source);
        var view = new StepDedup { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 900 };
        window.Show();
        try
        {
            await vm.ScanCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, server.GetIndexes.Length); // page 0 and the empty page after it

            // This scope came entirely from the server's content hashes, so there
            // is nothing for a threshold to move: the slider is gone and a line
            // says why, instead of offering a control that cannot change the
            // result (review A3).
            Assert.True(vm.IsExactHashOnlyScan);
            Assert.False(vm.CanRetune);
            Assert.True(vm.HasRetainedScan); // …while a plain Scan still re-groups it
            var slider = view.GetVisualDescendants().OfType<Slider>()
                .Single(s => s.Name == "RetuneThresholdSlider");
            Assert.False(slider.IsEffectivelyVisible); // its group is collapsed
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.Name == "ExactHashScanNotice" && text.IsEffectivelyVisible &&
                text.Text?.Contains("compared exactly", StringComparison.Ordinal) == true);
            Assert.True(vm.HasRetrievalMismatch);
            Assert.Equal(5, vm.LastRetrieval!.ScopeCount);
            Assert.Equal(2, vm.LastRetrieval.Kept);
            Assert.True(vm.ReWalkUsingPageTotalsCommand.CanExecute(null));

            await vm.ReWalkUsingPageTotalsCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(4, server.GetIndexes.Length); // the walk ran a second time
            var report = Assert.IsType<CatalogRetrievalDiagnostics>(vm.LastRetrieval);
            Assert.False(report.CountDrivesTotal);
            Assert.Equal(5, report.ScopeCount); // the count is still reported for comparison
            Assert.Equal(2, report.Kept);
            Assert.Contains("followed the pages' total", vm.RetrievalComparisonText);

            // A normal Scan press now re-uses the retained fingerprints (the
            // fast rescan), so it walks nothing and the retrieval report stays.
            var pagesAfterReWalk = server.GetIndexes.Length;
            await vm.ScanCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(pagesAfterReWalk, server.GetIndexes.Length);
            Assert.Contains("fingerprints reused", vm.ScanSummary);

            // The page-total mode is one-shot: the explicit re-hash walks again
            // with the count as the denominator.
            await vm.HashAgainCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.LastRetrieval!.CountDrivesTotal);
        }
        finally
        {
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    /// <summary>
    /// The destructive half of R03: a scan's rows carry catalog ids, and a
    /// catalog rebuilt behind the same URL reuses them, so an id can name a
    /// different image by the time Apply runs. The write therefore re-checks the
    /// catalog generation first and refuses, leaving the review list untouched —
    /// a rescan is cheap, deleting the wrong image is not.
    /// </summary>
    [AvaloniaFact]
    public async Task Catalog_apply_refuses_when_the_server_now_reports_another_catalog()
    {
        using var server = new CatalogServer(index => index == 0
            ? JsonSerializer.Serialize(new
            {
                items = new[]
                {
                    new { id = 11, fileName = "copy-a.jpg", hashCode = 4242, fileSize = 1024 },
                    new { id = 12, fileName = "copy-b.jpg", hashCode = 4242, fileSize = 2048 },
                },
                totalCount = 2,
            })
            : "{}");
        var session = new Session();
        session.Datasource.Type = "daminion";
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = server.BaseUrl,
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);
        Assert.Equal("test-catalog", source.ActiveCatalog);

        var vm = new StepDedupViewModel(step1: source);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Single(vm.Groups);
        Assert.Single(vm.Groups[0].Items, i => !i.IsChecked);

        // The catalog is rebuilt behind the same URL while the review list stays
        // on screen; the ids now name other records.
        server.CatalogGuid = "rebuilt-catalog";
        var prompts = 0;
        vm.ConfirmAction = _ => { prompts++; return Task.FromResult(true); };
        vm.SelectedAction = 0; // "Delete from catalog"

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Contains("Catalog changed since this scan", vm.ScanSummary, StringComparison.Ordinal);
        Assert.Contains("rebuilt-catalog", vm.ScanSummary, StringComparison.Ordinal);
        Assert.Single(vm.Groups); // nothing left the review list
        Assert.DoesNotContain(server.Routes, route =>
            route.Contains("MediaItems/Remove", StringComparison.Ordinal));
        // The gate sits between the user's confirmation and the write itself.
        Assert.Equal(1, prompts);
    }

    /// <summary>
    /// The walk has to be visible while it runs, not only once it ends. This
    /// holds the second page's HTTP response open, then reads the shared run bar
    /// the user is looking at — count, current page and a per-page ETA — before
    /// letting the scan finish. A bar left at "Idle" for a 47,000-item walk is
    /// exactly the complaint this covers.
    /// </summary>
    [AvaloniaFact]
    public async Task Catalog_walk_reports_pages_and_an_eta_on_the_run_bar_while_it_runs()
    {
        var secondPageRequested = new ManualResetEventSlim(false);
        var releaseSecondPage = new ManualResetEventSlim(false);
        using var server = new CatalogServer(index =>
        {
            if (index == 500)
            {
                secondPageRequested.Set();
                releaseSecondPage.Wait(TimeSpan.FromSeconds(20));
            }
            return JsonSerializer.Serialize(new
            {
                items = Enumerable.Range(index + 1, Math.Clamp(1000 - index, 0, 500))
                    .Select(id => new
                    {
                        id,
                        fileName = $"item-{id}.jpg",
                        // A server hash keeps the later grouping phase from
                        // downloading 1,000 originals over the stub.
                        hashCode = 100_000L + id,
                        fileSize = 10,
                    })
                    .ToArray(),
                totalCount = 1000,
            });
        });
        var session = new Session();
        session.Datasource.Type = "daminion";
        var source = new Step1DatasourceViewModel(session)
        {
            DaminionUrl = server.BaseUrl,
            DaminionUser = "test",
            DaminionPass = "test",
        };
        await source.ConnectCommand.ExecuteAsync(null);
        Assert.True(source.IsDaminionConnected, source.ConnectionMessage);

        var vm = new StepDedupViewModel(step1: source);
        var view = new StepDedup { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 700 };
        window.Show();
        Task? scan = null;
        try
        {
            scan = vm.ScanCommand.ExecuteAsync(null);

            // Wait for the walk to be parked on page 2. The page-1 progress was
            // posted to the dispatcher from a worker thread, so pump it in.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while ((!secondPageRequested.IsSet ||
                    !vm.ProgressText.StartsWith("Fetching catalog:", StringComparison.Ordinal)) &&
                   DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Assert.True(secondPageRequested.IsSet, "the scan never requested the second page");
            Assert.StartsWith("Fetching catalog:", vm.ProgressText);
            Assert.Contains("500", vm.ProgressText);
            Assert.Equal("catalog page 1", vm.CurrentFile);
            Assert.StartsWith("ETA", vm.EtaText);
            Assert.Contains("per page", vm.EtaText);

            releaseSecondPage.Set();
            await scan;
            Dispatcher.UIThread.RunJobs();

            // The fetch ETA is replaced by the hashing phase, and cleared when it
            // completes — the walk's estimate must not outlive the walk.
            Assert.Equal("1000/1000 (0 failed)", vm.ProgressText);
            Assert.Equal("", vm.EtaText);
        }
        finally
        {
            releaseSecondPage.Set();
            if (scan is { IsCompleted: false })
            {
                try { await scan; } catch { /* the assertion above is the failure */ }
            }
            window.Close();
            source.DisconnectCommand.Execute(null);
        }
    }

    /// <summary>Records progress synchronously (Progress&lt;T&gt; would post to a
    /// sync context and make the assertions timing-dependent).</summary>
    private sealed class RecordingProgress<T>(Action<T> record) : IProgress<T>
    {
        public void Report(T value) => record(value);
    }

    private sealed class ThumbnailRecordingSidecar(Func<string, Task>? onTag = null) : IInferenceSidecar
    {
        public SidecarStatus CurrentStatus => SidecarStatus.Ready;
        public int SidecarPort => 0;
        public bool IsRunning => true;
#pragma warning disable CS0067
        public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
        public event Action<string>? LogReceived;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(TimeSpan? gracefulTimeout = null) => Task.CompletedTask;
        public Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
        {
            if (onTag is not null) _ = onTag(request.ImagePath);
            return Task.FromResult(new TagResponse { Category = "Nature", Keywords = ["tree"], Description = "A tree" });
        }
        public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<ModelInfo>());
        public Task DownloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new HealthResponse());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<DaminionApiClient> Connect(CatalogServer server)
    {
        var client = new DaminionApiClient(server.BaseUrl, "test", "test");
        await client.AuthenticateAsync();
        return client;
    }

    [Fact]
    public async Task Process_all_stops_when_the_catalog_repeats_the_previous_page()
    {
        using var server = new CatalogServer(_ => JsonSerializer.Serialize(new
        {
            items = new[] { new { id = 9, fileName = "same.jpg" } }, totalCount = 1
        }));
        var client = await Connect(server);

        var items = await WorkflowRunner.FetchItemsAsync(new DatasourceSelection
        {
            IsDaminion = true, DaminionClient = client, ProcessAll = true,
        }, CancellationToken.None);

        Assert.Single(items);
        Assert.Equal(new[] { 0, 1 }, server.GetIndexes);
    }

    /// <summary>
    /// A collection page is filtered on this side, so it can contribute fewer
    /// eligible items than the raw records it returned. The cursor must advance
    /// by the raw page, and a page that filtered out every record must not end
    /// the walk — otherwise a match that only appears on a later raw page is
    /// never returned. Regression for the review's "IDs 1 and 4" reproduction:
    /// the old loop advanced by the eligible count (0 and 1) and stopped on the
    /// filtered-empty page, returning only ID 1.
    /// </summary>
    [Fact]
    public async Task Collection_paging_advances_by_the_raw_page_and_keeps_later_matches()
    {
        using var server = new CatalogServer(index => Page(index, 500), collectionItems: RawPagedItems);
        var client = await Connect(server);

        var items = await WorkflowRunner.FetchItemsAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
            Scope = "collection",
            CollectionId = 5,
            StatusFilter = "approved",
            ProcessAll = true,
        }, CancellationToken.None);

        Assert.Equal(new[] { 1, 4 }, items.Select(item => item.DaminionId!.Value).ToArray());
        // Raw pages of two records: offset 0, the second record's page at 2, then
        // the empty page at 4 that ends the walk.
        Assert.Equal(new[] { 0, 2, 4 }, server.GetIndexes);
    }

    /// <summary>
    /// The saved-search scope filters on this side too, so it exercises the same
    /// raw-cursor contract as a collection. A match on the second raw page must
    /// survive the first page's client-side filtering.
    /// </summary>
    [Fact]
    public async Task Saved_search_paging_keeps_matches_on_later_raw_pages()
    {
        using var server = new CatalogServer(RawPagedItems);
        var client = await Connect(server);

        var items = await WorkflowRunner.FetchItemsAsync(new DatasourceSelection
        {
            IsDaminion = true,
            DaminionClient = client,
            Scope = "saved_search",
            SavedSearchId = 3,
            StatusFilter = "approved",
            ProcessAll = true,
        }, CancellationToken.None);

        Assert.Equal(new[] { 1, 4 }, items.Select(item => item.DaminionId!.Value).ToArray());
        Assert.Equal(new[] { 0, 2, 4 }, server.GetIndexes);
    }

    /// <summary>Raw pages of two records, ids 1..4, with only the ends approved.</summary>
    private static string RawPagedItems(int index) => JsonSerializer.Serialize(new
    {
        items = Enumerable.Range(index + 1, 2)
            .Where(id => id <= 4)
            .Select(id => new { id, fileName = $"c-{id}.jpg", flag = id is 1 or 4 ? 1 : 2 })
            .ToArray(),
        totalCount = 4,
    });

    private static string Page(int start, int count) => JsonSerializer.Serialize(new
    {
        items = Enumerable.Range(start + 1, count).Select(id => new { id, fileName = $"item-{id}.jpg" }),
        totalCount = start + count,
    });

    /// <summary>A loopback catalog stub; selection results and page boundaries are deterministic.</summary>
    internal sealed class CatalogServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<int, string> _items;
        private readonly Func<int, string>? _collectionItems;
        private readonly Func<string, string>? _countBody;
        private readonly ConcurrentQueue<int> _indexes = new();
        private readonly ConcurrentQueue<string> _routes = new();
        private readonly Action<int>? _thumbnailOverride;
        public string[] Routes => _routes.ToArray();
        public string BaseUrl { get; }
        public int[] GetIndexes => _indexes.ToArray();

        /// <summary>
        /// The catalog guid this stub reports. A test can swap it to stand in for
        /// a catalog that was rebuilt behind the same URL — the case where item
        /// ids are reused and a scan's ids stop naming the reviewed images.
        /// </summary>
        public string CatalogGuid { get; set; } = "test-catalog";

        public CatalogServer(Func<int, string> items, Action<int>? thumbnailOverride = null,
            Func<int, string>? collectionItems = null, Func<string, string>? countBody = null)
        {
            _items = items;
            _thumbnailOverride = thumbnailOverride;
            _collectionItems = collectionItems;
            _countBody = countBody;
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = new StringBuilder();
                var buffer = new byte[2048];
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                var path = request.ToString().Split('\r', '\n')[0].Split(' ')[1];
                var route = path.Split('?')[0];
                _routes.Enqueue(path);
                var extra = route == "/api/UserManager/Login" ? "Set-Cookie: SynapicDam=ok; Path=/\\r\\n" : "";
                var body = "{}";
                if (route.StartsWith("/api/Thumbnail/Get/", StringComparison.Ordinal))
                {
                    var id = int.Parse(route[("/api/Thumbnail/Get/".Length)..]);
                    _thumbnailOverride?.Invoke(id);
                    body = "thumbnail-data";
                }
                if (route == "/api/MediaItems/Get")
                {
                    var index = ParseIndex(path);
                    _indexes.Enqueue(index);
                    body = _items(index);
                }
                else if (route == "/api/MediaItems/GetCount")
                {
                    body = _countBody?.Invoke(path) ?? "{}";
                }
                else if (route == "/api/SharedCollection/GetItems")
                {
                    var index = ParseIndex(path);
                    _indexes.Enqueue(index);
                    body = _collectionItems is null ? "{}" : _collectionItems(index);
                }
                else if (route == "/api/Settings/GetCatalogGuid")
                {
                    body = $"{{\"guid\":\"{CatalogGuid}\"}}";
                }
                else if (route == "/api/Settings/GetTags")
                {
                    body = "[]";
                }
                else if (route == "/api/ItemData/GetDefaultLayout")
                {
                    body = "{\"properties\":[{\"propertyName\":\"Categories\",\"propertyGuid\":\"category-guid\"},{\"propertyName\":\"Keywords\",\"propertyGuid\":\"keywords-guid\"},{\"propertyName\":\"Description\",\"propertyGuid\":\"description-guid\"}]}";
                }
                else if (route == "/api/IndexedTagValues/GetIndexedTagValues")
                {
                    body = "[]";
                }
                else if (route == "/api/SharedCollection/GetCollections")
                {
                    body = "[]";
                }
                var payload = Encoding.UTF8.GetBytes(body);
                var header = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n{extra}Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header.Concat(payload).ToArray(), _stop.Token);
            }
        }

        private static int ParseIndex(string path) => int.Parse(path.Split('&')
            .Select(part => part.Split('='))
            .First(part => part[0].EndsWith("index", StringComparison.Ordinal))[1]);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
