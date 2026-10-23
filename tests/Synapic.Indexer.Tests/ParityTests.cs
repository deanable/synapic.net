using Synapic.Core.Services;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Detection;
using Synapic.Indexer.Pipeline;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

/// <summary>
/// TAG-04 / spec §9.3: the indexer is the desktop's pipeline, not a look-alike.
/// One deterministic sidecar, one template, two files — the desktop's
/// <c>RunAsync</c> over a folder scope and the indexer's <c>BatchExecutor</c> over
/// a queue row. Parity is compared by reading the written XMP back, so a
/// difference in field selection or write path cannot hide behind in-memory
/// results.
/// </summary>
public sealed class ParityTests
{
    [Fact]
    public async Task The_desktop_and_the_indexer_write_the_same_tags_for_the_same_image()
    {
        using var harness = await PipelineHarness.CreateAsync(overwrite: true);
        var desktop = harness.AddImage("desktop/photo.jpg");
        var indexer = harness.AddImage("indexer/photo.jpg");

        var (desktopTags, indexerTags) = await RunBothAsync(harness, desktop, indexer);
        Assert.Equal(desktopTags.Category, indexerTags.Category);
        Assert.Equal(desktopTags.Description, indexerTags.Description);
        Assert.Equal(
            desktopTags.Keywords.OrderBy(k => k, StringComparer.Ordinal),
            indexerTags.Keywords.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("Nature", indexerTags.Category);
        Assert.Equal(new[] { "forest", "tree" }, indexerTags.Keywords.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Field_selection_applies_identically_on_both_paths()
    {
        using var harness = await PipelineHarness.CreateAsync(
            overwrite: true,
            configure: config => config.Tagging.WriteDescription = false);
        var desktop = harness.AddImage("desktop/photo.jpg");
        var indexer = harness.AddImage("indexer/photo.jpg");

        var (desktopTags, indexerTags) = await RunBothAsync(harness, desktop, indexer);

        Assert.True(string.IsNullOrEmpty(desktopTags.Description));
        Assert.True(string.IsNullOrEmpty(indexerTags.Description));
        Assert.Equal(desktopTags.Category, indexerTags.Category);
        Assert.Equal(
            desktopTags.Keywords.OrderBy(k => k, StringComparer.Ordinal),
            indexerTags.Keywords.OrderBy(k => k, StringComparer.Ordinal));
    }

    private static async Task<(TagResult Desktop, TagResult Indexer)> RunBothAsync(
        PipelineHarness harness, string desktopPath, string indexerPath)
    {
        var fields = TagRequestFactory.Fields(harness.Config.Tagging);
        var template = TagRequestFactory.Create(harness.Config.Engine);

        // (a) the desktop's call shape: a folder scope through the orchestrator.
        var results = new List<ProcessItemResult>();
        var desktopRun = new ProcessingOrchestrator(harness.Sidecar, harness.Writer, maxDegreeOfParallelism: 1);
        await desktopRun.RunAsync(
            new DatasourceSelection
            {
                IsDaminion = false,
                LocalPath = Path.GetDirectoryName(desktopPath)!,
                LocalRecursive = false,
                Scope = "all",
            },
            template,
            new Progress<ProcessProgress>(_ => { }),
            _ => Task.CompletedTask,
            CancellationToken.None,
            results,
            pause: null,
            tagFields: fields);

        Assert.Single(results);
        Assert.Equal(ProcessStatus.Success, results[0].Status);

        // (b) the indexer's path: one queued row through the executor.
        await harness.EnqueueAsync(indexerPath);
        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);
        Assert.Equal(JobState.Completed, (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(indexerPath)))!.State);

        return ((await harness.Writer.ReadAsync(desktopPath))!, (await harness.Writer.ReadAsync(indexerPath))!);
    }
}
