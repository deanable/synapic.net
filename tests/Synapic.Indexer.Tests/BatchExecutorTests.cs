using Synapic.Core.Services;
using Synapic.Indexer.Detection;
using Synapic.Indexer.Pipeline;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class BatchExecutorTests
{
    private sealed class FailingWriter : IMetadataWriter
    {
        public Task<TagResult?> ReadAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<TagResult?>(null);

        public Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    [Fact]
    public async Task Two_local_images_are_tagged_once_and_completed()
    {
        using var harness = await PipelineHarness.CreateAsync(concurrency: 2, overwrite: true);
        var first = harness.AddImage("first.jpg");
        var second = harness.AddImage("second.jpg");
        await harness.EnqueueAsync(first);
        await harness.EnqueueAsync(second);

        var finished = await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(2, finished);
        foreach (var path in new[] { first, second })
        {
            var row = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
            Assert.Equal(JobState.Completed, row.State);
            Assert.Equal(1, row.Attempts);

            var tags = await harness.Writer.ReadAsync(path);
            Assert.NotNull(tags);
            Assert.Equal("Nature", tags!.Category);
            Assert.Contains("forest", tags.Keywords);
            Assert.Contains("tree", tags.Keywords);
            Assert.Equal("A forest at dawn", tags.Description);
        }
    }

    [Fact]
    public async Task A_failed_metadata_write_retries_in_a_minute()
    {
        using var harness = await PipelineHarness.CreateAsync(writer: new FailingWriter());
        var path = harness.AddImage("write-fails.jpg");
        await harness.EnqueueAsync(path);

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        var row = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
        Assert.Equal(JobState.TransientFailure, row.State);
        Assert.Equal(1, row.Attempts);
        var due = row.NextAttemptAt!.Value - harness.Clock.GetUtcNow().UtcDateTime;
        Assert.InRange(due.TotalSeconds, 55, 65);
    }

    [Fact]
    public async Task A_locked_file_is_classified_as_transient_not_corrupt()
    {
        using var harness = await PipelineHarness.CreateAsync();
        var path = harness.AddImage("locked.jpg");
        await harness.EnqueueAsync(path);
        harness.Sidecar.OnTag = (_, _) => throw new IOException(
            "The process cannot access the file because it is being used by another process.");

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        var row = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
        Assert.Equal(JobState.TransientFailure, row.State);
        Assert.Contains("in use", row.LastError);
        Assert.NotEqual(JobState.Failed, row.State);
    }

    [Fact]
    public async Task The_observed_parallelism_matches_the_configured_limit()
    {
        using var harness = await PipelineHarness.CreateAsync(concurrency: 2);
        harness.Sidecar.OnTag = async (_, _) =>
        {
            await Task.Delay(60);
            return FakeSidecar.CannedTags;
        };

        for (var i = 0; i < 4; i++)
            await harness.EnqueueAsync(harness.AddImage($"parallel-{i}.jpg"));

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(2, harness.Sidecar.MaxConcurrent);
        Assert.Equal(4, harness.Sidecar.TagCalls);
    }

    [Fact]
    public async Task An_empty_queue_does_nothing()
    {
        using var harness = await PipelineHarness.CreateAsync();

        var finished = await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(0, finished);
        Assert.Equal(0, harness.Sidecar.TagCalls);
        Assert.Equal(0, (await harness.Repository.GetSummaryAsync()).Total);
    }

    [Fact]
    public async Task Cancelling_a_batch_returns_every_claimed_row_to_Pending()
    {
        using var harness = await PipelineHarness.CreateAsync(concurrency: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Sidecar.OnTag = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeSidecar.CannedTags;
        };

        await harness.EnqueueAsync(harness.AddImage("cancel-a.jpg"));
        await harness.EnqueueAsync(harness.AddImage("cancel-b.jpg"));

        using var cts = new CancellationTokenSource();
        var run = harness.Executor.ExecuteOnceAsync(cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var summary = await harness.Repository.GetSummaryAsync();
        Assert.Equal(0, summary.InFlight);
        Assert.Equal(2, summary.Pending);
    }
}
