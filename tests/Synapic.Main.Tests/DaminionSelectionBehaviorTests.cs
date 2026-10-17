using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Synapic.Shared.Contracts;
using Synapic.Main.Models;
using Synapic.Main.Services;
using Synapic.Main.Services.Daminion;
using Synapic.Main.Services.Processing;
using Synapic.Main.ViewModels.Steps;
using Synapic.Main.Views.Wizard;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

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
    /// ceiling (100 by default) belongs to tagging. This walks 1,500 records in
    /// three pages with the ceiling at 100 and auto-pagination switched off, and
    /// expects every one of them.
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
    /// The reverse guarantee: a tagging run still honours its ceiling and its
    /// auto-paginate switch. Whole-scope fetching is for dedup/similar only.
    /// </summary>
    [Fact]
    public async Task Scoped_fetch_still_respects_the_step_one_ceiling()
    {
        using var server = new CatalogServer(index => Page(index, index < 1500 ? 500 : 0));
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
    private sealed class CatalogServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<int, string> _items;
        private readonly Func<int, string>? _collectionItems;
        private readonly ConcurrentQueue<int> _indexes = new();
        private readonly ConcurrentQueue<string> _routes = new();
        private readonly Action<int>? _thumbnailOverride;
        public string[] Routes => _routes.ToArray();
        public string BaseUrl { get; }
        public int[] GetIndexes => _indexes.ToArray();

        public CatalogServer(Func<int, string> items, Action<int>? thumbnailOverride = null,
            Func<int, string>? collectionItems = null)
        {
            _items = items;
            _thumbnailOverride = thumbnailOverride;
            _collectionItems = collectionItems;
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
                else if (route == "/api/SharedCollection/GetItems")
                {
                    var index = ParseIndex(path);
                    _indexes.Enqueue(index);
                    body = _collectionItems is null ? "{}" : _collectionItems(index);
                }
                else if (route == "/api/Settings/GetCatalogGuid")
                {
                    body = "{\"guid\":\"test-catalog\"}";
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
