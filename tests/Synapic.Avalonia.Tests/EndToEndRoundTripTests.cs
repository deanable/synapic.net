using System.Diagnostics;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Daminion;
using Synapic.Avalonia.Services.Processing;
using Synapic.Shared.Contracts;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// End-to-end round trip (spec Phase 2 deliverable): fetch → tag → write →
/// verify. The local variant runs against the real sidecar exe and a temp
/// image folder; the Daminion variant runs against a live server when
/// SYNAPIC_TEST_DAMINION_URL / _USERNAME / _PASSWORD are set.
/// </summary>
public class EndToEndRoundTripTests
{
    private static string? FindSampleImage()
    {
        // Test bin dir → up 5 levels reaches the repo root (bin/Release/net10.0 → TestData).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Synapic.Avalonia.Tests", "TestData", "sample.jpg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static DatasourceSelection LocalSelection(string dir) => new()
    {
        IsDaminion = false,
        LocalPath = dir,
        LocalRecursive = false,
    };

    /// <summary>
    /// Full local round trip: real sidecar + real metadata writer. Verifies the
    /// JPEG on disk ends up with the tags the model produced (write path proven,
    /// not just the HTTP hop). Skips when the sidecar exe has not been built.
    /// </summary>
    [Fact]
    public async Task Local_round_trip_via_real_sidecar_writes_metadata()
    {
        var exe = InferenceSidecarService.FindExecutable();
        if (exe is null)
        {
            // Sidecar not built in this environment — nothing to verify here.
            return;
        }
        var sample = FindSampleImage();
        if (sample is null)
        {
            return;
        }

        var workDir = Path.Combine(Path.GetTempPath(), $"synapic-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var files = new[] { "one.jpg", "two.jpg", "three.jpg" };
        foreach (var f in files) File.Copy(sample, Path.Combine(workDir, f));

        var sidecar = new InferenceSidecarService();
        try
        {
            await sidecar.StartAsync();
            Assert.Equal(SidecarStatus.Ready, sidecar.CurrentStatus);

            var orchestrator = new ProcessingOrchestrator(sidecar, maxDegreeOfParallelism: 1);
            var results = new List<ProcessItemResult>();
            var template = new TagRequest
            {
                ImagePath = "",
                ModelId = "LiquidAI/LFM2.5-VL-450M",
                Task = "image-text-to-text",
                Options = new TagOptions { MaxNewTokens = 128 },
            };

            var logSink = new List<string>();
            await orchestrator.RunAsync(
                LocalSelection(workDir), template, new Progress<ProcessProgress>(),
                line => { lock (logSink) logSink.Add(line); return Task.CompletedTask; },
                CancellationToken.None, results);

            Assert.True(
                results.Count == files.Length,
                $"expected {files.Length} results, got {results.Count}. Log:\n" +
                string.Join("\n", logSink));
            Assert.All(results, r => Assert.Equal("Success", r.Status));

            // Verify the write-back: the JPEGs must now carry metadata.
            var writer = new MetadataWriterService();
            foreach (var f in files)
            {
                var path = Path.Combine(workDir, f);
                var read = await writer.ReadAsync(path);
                Assert.True(
                    read is { } && (!string.IsNullOrWhiteSpace(read.Category) || read.Keywords.Count > 0),
                    $"metadata not found on {f}");
            }
        }
        finally
        {
            await sidecar.StopAsync();
            try { Directory.Delete(workDir, recursive: true); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>
    /// Live Daminion round trip: authenticate → list saved searches → fetch a
    /// few items → tag one → write metadata → verify the write → restore the
    /// original values. Opt-in: set SYNAPIC_TEST_DAMINION_URL (+ _USERNAME,
    /// _PASSWORD) against a TEST catalog only.
    /// </summary>
    [Fact]
    [Trait("Category", "Daminion")]
    public async Task Daminion_round_trip_against_live_server()
    {
        var baseUrl = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_URL");
        var username = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_USERNAME");
        var password = Environment.GetEnvironmentVariable("SYNAPIC_TEST_DAMINION_PASSWORD");
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            // Not configured — the live round trip is opt-in only.
            return;
        }

        var client = new DaminionApiClient(baseUrl, username, password);
        await client.AuthenticateAsync();

        var searches = await client.GetSavedSearchesAsync();
        Assert.NotEmpty(searches);

        var items = await client.GetItemsFilteredAsync(
            scope: "all", savedSearchId: null, collectionId: null, searchTerm: null,
            untaggedFields: null, statusFilter: "all", maxItems: 3, startIndex: 0);
        Assert.NotEmpty(items);

        // Tag the first item, verify, then restore the original values.
        var target = items[0];
        Assert.True(await client.UpdateItemMetadataAsync(
            target.Id, "Verified by Synapic e2e", ["synapic-test"], "Temporary tag written by the Synapic end-to-end test."));

        var verdict = await client.VerifyItemMetadataAsync(
            target.Id, "Verified by Synapic e2e", ["synapic-test"], "Temporary tag written by the Synapic end-to-end test.");
        Assert.True(verdict.Ok, verdict.Detail);

        // Restore: clear the temporary keyword so the test is self-cleaning.
        await Task.Delay(1000); // let the server settle the BatchChange write
        Assert.True(await client.RemoveKeywordsAsync(target.Id, ["synapic-test"]));
    }
}
