using Synapic.Core.Services;
using Synapic.Core.Services.Daminion;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Detection;
using Synapic.Indexer.Pipeline;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

/// <summary>
/// The five failure rows of docs/windows-indexer.md §8, each played through the
/// real executor and a real temp queue. The sidecar is faked (a real model is not
/// deterministic or CI-fast), but everything the queue sees — states, attempts,
/// due times, pause — is the production path.
/// </summary>
public sealed class ResilienceTests
{
    [Fact]
    public async Task A_sidecar_crash_between_items_is_restarted_and_the_batch_finishes()
    {
        using var harness = await PipelineHarness.CreateAsync(concurrency: 1);
        var first = harness.AddImage("crash-a.jpg");
        var second = harness.AddImage("crash-b.jpg");
        await harness.EnqueueAsync(first);
        await harness.EnqueueAsync(second);

        var calls = 0;
        harness.Sidecar.OnTag = (_, _) =>
        {
            // The first item succeeds; the process then dies during the second.
            // After the restart every call answers again.
            if (Interlocked.Increment(ref calls) != 2) return Task.FromResult(FakeSidecar.CannedTags);
            harness.Sidecar.IsRunning = false;
            throw new InvalidOperationException("the sidecar process exited unexpectedly");
        };
        harness.Recovery.OnRecovered = () => harness.Sidecar.IsRunning = true;

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        var firstRow = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(first)))!;
        var secondRow = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(second)))!;
        Assert.Equal(JobState.Completed, firstRow.State);
        Assert.True(secondRow.State == JobState.Completed,
            $"state={secondRow.State} attempts={secondRow.Attempts} due={secondRow.NextAttemptAt} " +
            $"err={secondRow.LastError} calls={calls} recovery={harness.Recovery.Calls} " +
            $"running={harness.Sidecar.IsRunning}");
        Assert.Equal(1, firstRow.Attempts);
        Assert.Equal(2, secondRow.Attempts);
        Assert.Equal(1, harness.Recovery.Calls);
        Assert.Equal(0, (await harness.Repository.GetSummaryAsync()).InFlight);
        Assert.Equal("Nature", (await harness.Writer.ReadAsync(second))!.Category);
    }

    [Fact]
    public async Task A_sidecar_that_will_not_restart_leaves_the_rows_waiting_five_minutes()
    {
        using var harness = await PipelineHarness.CreateAsync(concurrency: 1);
        var first = harness.AddImage("dead-a.jpg");
        var second = harness.AddImage("dead-b.jpg");
        await harness.EnqueueAsync(first);
        await harness.EnqueueAsync(second);

        harness.Sidecar.OnTag = (_, _) =>
        {
            harness.Sidecar.IsRunning = false;
            throw new InvalidOperationException("the sidecar process exited unexpectedly");
        };
        harness.Recovery.Succeeds = false;

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        var summary = await harness.Repository.GetSummaryAsync();
        Assert.Equal(0, summary.Completed);
        Assert.Equal(0, summary.InFlight);
        Assert.Equal(2, summary.TransientFailure);
        Assert.Equal(1, harness.Recovery.Calls);
        Assert.False(harness.Executor.IsPaused);

        foreach (var path in new[] { first, second })
        {
            var row = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
            Assert.Equal(JobState.TransientFailure, row.State);
            var due = row.NextAttemptAt!.Value - harness.Clock.GetUtcNow().UtcDateTime;
            Assert.InRange(due.TotalMinutes, 4.9, 5.1);
        }
    }

    [Fact]
    public async Task A_file_locked_during_the_write_retries_after_a_minute_without_failing_the_batch()
    {
        using var harness = await PipelineHarness.CreateAsync(concurrency: 2);
        var locked = harness.AddImage("held-open.jpg");
        var free = harness.AddImage("free.jpg");
        await harness.EnqueueAsync(locked);
        await harness.EnqueueAsync(free);

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await harness.Executor.ExecuteOnceAsync(CancellationToken.None);
        }

        var lockedRow = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(locked)))!;
        var freeRow = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(free)))!;
        Assert.Equal(JobState.Completed, freeRow.State);
        Assert.Equal(JobState.TransientFailure, lockedRow.State);
        Assert.Equal(1, lockedRow.Attempts);
        var due = lockedRow.NextAttemptAt!.Value - harness.Clock.GetUtcNow().UtcDateTime;
        Assert.InRange(due.TotalSeconds, 55, 65);

        harness.Clock.Advance(TimeSpan.FromSeconds(61));
        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        lockedRow = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(locked)))!;
        Assert.Equal(JobState.Completed, lockedRow.State);
        Assert.Equal(2, lockedRow.Attempts);
    }

    [Fact]
    public async Task A_corrupt_image_fails_permanently_on_the_first_attempt_and_the_batch_continues()
    {
        using var harness = await PipelineHarness.CreateAsync(concurrency: 1);
        var corrupt = harness.AddImage("corrupt.jpg");
        await File.WriteAllBytesAsync(corrupt, new byte[] { 0xFF, 0xD8, 0x00, 0x13, 0x37 });
        var good = harness.AddImage("good.jpg");
        await harness.EnqueueAsync(corrupt);
        await harness.EnqueueAsync(good);

        harness.Sidecar.OnTag = (request, _) =>
        {
            if (request.ImagePath is not null &&
                string.Equals(request.ImagePath, corrupt, StringComparison.OrdinalIgnoreCase))
            {
                throw new InferenceApiException(422, "Failed to decode image: cannot identify image file");
            }

            return Task.FromResult(FakeSidecar.CannedTags);
        };

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        var corruptRow = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(corrupt)))!;
        var goodRow = (await harness.Repository.GetByKeyAsync(PathKey.ForLocal(good)))!;
        Assert.Equal(JobState.Failed, corruptRow.State);
        Assert.Equal(1, corruptRow.Attempts);
        Assert.Contains("could not be decoded", corruptRow.LastError);
        Assert.Equal(JobState.Completed, goodRow.State);
    }

    [Fact]
    public async Task A_catalog_outage_pauses_the_run_and_a_batch_resumes_after_the_window()
    {
        // A real client pointed at a closed port: the download throws before any
        // tags are written, exactly as an unreachable server does in production.
        var client = new DaminionApiClient("http://127.0.0.1:9", "bot", "secret");
        using var harness = await PipelineHarness.CreateAsync(
            catalog: true,
            catalogSelection: () => new DatasourceSelection
            {
                IsDaminion = true,
                DaminionClient = client,
                ResizeScale = 100,
            });

        await harness.EnqueueCatalogAsync(1, "cat-a.jpg");
        await harness.EnqueueCatalogAsync(2, "cat-b.jpg");

        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        var summary = await harness.Repository.GetSummaryAsync();
        Assert.True(summary.Pending == 2 && summary.InFlight == 0,
            $"pending={summary.Pending} inflight={summary.InFlight} failed={summary.Failed} " +
            $"transient={summary.TransientFailure} completed={summary.Completed} " +
            $"err={(await harness.Repository.GetByKeyAsync(PathKey.ForDaminion(1)))!.LastError}");
        Assert.True(harness.Executor.IsPaused);
        Assert.Equal(1, (await harness.Repository.GetByKeyAsync(PathKey.ForDaminion(1)))!.Attempts);

        // Inside the window nothing is claimed, so nothing is attempted again.
        var during = await harness.Executor.ExecuteOnceAsync(CancellationToken.None);
        Assert.Equal(0, during);
        Assert.Equal(1, (await harness.Repository.GetByKeyAsync(PathKey.ForDaminion(1)))!.Attempts);

        harness.Clock.Advance(JobOutcomeClassifier.CatalogPauseDelay + TimeSpan.FromSeconds(1));
        Assert.False(harness.Executor.IsPaused);
        await harness.Executor.ExecuteOnceAsync(CancellationToken.None);

        // The outage is still there, so the batch pauses again — but it ran, and
        // no item was dropped: every row is still Pending and still owned.
        Assert.Equal(2, (await harness.Repository.GetByKeyAsync(PathKey.ForDaminion(1)))!.Attempts);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(2, (await harness.Repository.GetSummaryAsync()).Pending);
    }
}
