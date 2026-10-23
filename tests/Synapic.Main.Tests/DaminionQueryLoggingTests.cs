using System.Text.Json;
using Synapic.Core.Services;
using Synapic.Core.Services.Daminion;
using Synapic.Core.Services.Processing;
using Xunit;

namespace Synapic.Main.Tests;

/// <summary>
/// A count and a paged walk that disagree are diagnosable only if the log says
/// which query produced each number. These tests pin the lines a user (or a
/// support log) needs: the count request/response, each page's request/response
/// and the warning when a page's totalCount contradicts the scope count.
/// Serialized with the other log-file tests: they reconfigure the process-global
/// Serilog logger and read its file.
/// </summary>
[Collection("SynapicLogSerial")]
public class DaminionQueryLoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "synapic-query-log-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Count_and_page_queries_are_logged_with_the_clauses_they_sent()
    {
        using var server = new DaminionSelectionBehaviorTests.CatalogServer(
            index => JsonSerializer.Serialize(new
            {
                items = Enumerable.Range(index + 1, index == 0 ? 2 : 0)
                    .Select(id => new { id, fileName = $"item-{id}.jpg" })
                    .ToArray(),
                // The page claims the whole catalog; the count says 7.
                totalCount = 47_851,
            }),
            // A filtered count is 7; the unfiltered catalog count is 47,851 â€”
            // exactly the pair that used to leave the progress bar tiny.
            countBody: path => path.Contains("search=", StringComparison.Ordinal)
                ? "{\"count\":7}"
                : "{\"count\":47851}");
        var client = new DaminionApiClient(server.BaseUrl, "test", "test");
        await client.AuthenticateAsync();
        try
        {
            SynapicLog.RestartForTests(_dir, "info");

            var count = await client.GetFilteredItemCountAsync(scope: "all", statusFilter: "approved");
            await client.GetItemsFilteredAsync(scope: "all", statusFilter: "approved", maxItems: 10);
            var reports = new List<CatalogFetchProgress>();
            await WorkflowRunner.FetchEntireScopeAsync(new DatasourceSelection
            {
                IsDaminion = true,
                DaminionClient = client,
                StatusFilter = "approved",
            }, new RecordingProgress(reports.Add), CancellationToken.None);

            Assert.Equal(7, count);
            Assert.Equal(7, reports[^1].Total ?? -1);
        }
        finally
        {
            SynapicLog.ResetForTests();
        }

        try
        {
            var log = File.ReadAllText(Path.Combine(_dir, "synapic.log"));

            // The count's query and the raw response fields.
            Assert.Contains("Count request: scope=all", log);
            Assert.Contains("search='flag:approved'", log);
            Assert.Contains("Count response: count=7", log);

            // Every page's query and what came back for it.
            Assert.Contains("Page MediaItems/Get: index=0", log);
            Assert.Contains("search='flag:approved'", log);
            Assert.Contains("totalCount=47851", log);

            // The mismatch that made the progress bar wrong in the first place.
            Assert.Contains("but the scope count said 7", log);
        }
        finally
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Reports progress synchronously, so the fetched count is known before the log is read.</summary>
    private sealed class RecordingProgress(Action<CatalogFetchProgress> record) : IProgress<CatalogFetchProgress>
    {
        public void Report(CatalogFetchProgress value) => record(value);
    }

    public void Dispose()
    {
        // Leave the global logger in a clean state for other tests.
        SynapicLog.ResetForTests();
    }
}
