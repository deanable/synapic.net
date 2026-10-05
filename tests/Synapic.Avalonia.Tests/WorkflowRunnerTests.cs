using Synapic.Avalonia.Services.Processing;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>
/// The shared batch kernel every route runs on: fetching, bounded per-item
/// execution, progress with ETA, error isolation and the run summary are
/// implemented once in WorkflowRunner — tagging, dedup and upscaling only
/// differ in their WorkflowDefinition and IWorkflowItemHandler.
/// </summary>
public class WorkflowRunnerTests
{
    private static ProcessWorkItem Item(string name) => new() { FileName = name, LocalPath = name };

    private static WorkflowDefinition SerialWorkflow => new("test", "Testing", MaxDegreeOfParallelism: 1);

    /// <summary>Synchronous progress recorder (Progress&lt;T&gt; would post to a
    /// sync context and make the assertions timing-dependent).</summary>
    private sealed class Recorder : IProgress<ProcessProgress>
    {
        private readonly List<ProcessProgress> _reports = new();

        public void Report(ProcessProgress value)
        {
            lock (_reports) _reports.Add(value);
        }

        public IReadOnlyList<ProcessProgress> Snapshot()
        {
            lock (_reports) return _reports.ToList();
        }
    }

    /// <summary>Handler that records overlap and delegates the outcome.</summary>
    private sealed class TestHandler : IWorkflowItemHandler
    {
        public Func<ProcessWorkItem, Task<WorkflowItemOutcome>>? OnItem { get; set; }

        private int _inFlight;
        public int MaxInFlight { get; private set; }
        public List<string> Handled { get; } = new();

        public async Task<WorkflowItemOutcome> ProcessAsync(ProcessWorkItem item, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            lock (Handled)
            {
                Handled.Add(item.FileName);
                if (now > MaxInFlight) MaxInFlight = now;
            }
            try
            {
                await Task.Delay(15, ct);
                return OnItem is null ? WorkflowItemOutcome.Ok : await OnItem(item);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    // ── Error isolation and summaries ───────────────────────────────────────

    [Fact]
    public async Task One_throwing_item_fails_alone_and_the_batch_still_finishes()
    {
        var handler = new TestHandler
        {
            OnItem = item => item.FileName == "b.jpg"
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(WorkflowItemOutcome.Ok),
        };
        var logs = new List<string>();

        var summary = await new WorkflowRunner().RunItemsAsync(
            SerialWorkflow,
            new[] { Item("a.jpg"), Item("b.jpg"), Item("c.jpg") },
            handler,
            log: line => { lock (logs) logs.Add(line); return Task.CompletedTask; });

        Assert.Equal(3, summary.Total);
        Assert.Equal(3, summary.Processed);
        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(1, summary.Failed);

        // Every item got its turn — one bad item never aborts the batch.
        Assert.Equal(3, handler.Handled.Count);

        lock (logs)
        {
            Assert.Contains(logs, l => l.StartsWith("Starting batch: 3 items", StringComparison.Ordinal));
            Assert.Contains(logs, l => l.Contains("Failed: b.jpg — boom", StringComparison.Ordinal));
            Assert.Contains(logs, l => l.Contains("Batch finished: 2 ok, 1 failed, 3 total", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Soft_failures_are_counted_as_failed_without_an_error_line()
    {
        var handler = new TestHandler { OnItem = _ => Task.FromResult(WorkflowItemOutcome.Fail("checkout failed")) };
        var logs = new List<string>();

        var summary = await new WorkflowRunner().RunItemsAsync(
            SerialWorkflow,
            new[] { Item("a.jpg"), Item("b.jpg") },
            handler,
            log: line => { lock (logs) logs.Add(line); return Task.CompletedTask; });

        Assert.Equal(2, summary.Processed);
        Assert.Equal(0, summary.Succeeded);
        Assert.Equal(2, summary.Failed);
        lock (logs)
        {
            // A handled soft failure is not an exception: no "Failed: …" line.
            Assert.DoesNotContain(logs, l => l.StartsWith("Failed:", StringComparison.Ordinal));
            Assert.Contains(logs, l => l.Contains("Batch finished: 0 ok, 2 failed, 2 total", StringComparison.Ordinal));
        }
    }

    // ── Bounded parallelism (one model in RAM stays one model in RAM) ──────

    [Fact]
    public async Task Serial_workflows_never_overlap_items()
    {
        var handler = new TestHandler();

        var summary = await new WorkflowRunner().RunItemsAsync(
            SerialWorkflow,
            Enumerable.Range(0, 6).Select(i => Item($"img{i}.jpg")).ToArray(),
            handler);

        Assert.Equal(6, summary.Succeeded);
        Assert.Equal(1, handler.MaxInFlight);
    }

    // ── Cancellation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_propagates_to_the_caller()
    {
        using var cts = new CancellationTokenSource();
        var handler = new TestHandler
        {
            OnItem = _ =>
            {
                cts.Cancel();
                return Task.FromResult(WorkflowItemOutcome.Ok);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WorkflowRunner().RunItemsAsync(
                SerialWorkflow,
                new[] { Item("a.jpg"), Item("b.jpg") },
                handler,
                ct: cts.Token));
    }

    // ── Pause ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Paused_runs_hold_every_item_until_resumed()
    {
        var pauseSource = new PauseTokenSource();
        pauseSource.Pause();
        var handler = new TestHandler();

        var run = new WorkflowRunner().RunItemsAsync(
            SerialWorkflow,
            Enumerable.Range(0, 5).Select(i => Item($"img{i}.jpg")).ToArray(),
            handler,
            pause: pauseSource.Token);

        await Task.Delay(150);
        Assert.Empty(handler.Handled);   // no item starts while paused

        pauseSource.Resume();
        var summary = await run;

        Assert.Equal(5, summary.Succeeded);
        Assert.Equal(5, handler.Handled.Count);
    }

    // ── Progress ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Progress_walks_from_zero_to_the_total()
    {
        var recorder = new Recorder();

        var summary = await new WorkflowRunner().RunItemsAsync(
            new WorkflowDefinition("test", "Testing", MaxDegreeOfParallelism: 2),
            Enumerable.Range(0, 4).Select(i => Item($"img{i}.jpg")).ToArray(),
            new TestHandler(),
            progress: recorder);

        Assert.Equal(4, summary.Succeeded);
        var reports = recorder.Snapshot();
        Assert.Contains(reports, r => r.Processed == 0);                       // first start
        Assert.Contains(reports, r => r.Processed == 4 && r.Percent == 100);   // last completion
        Assert.All(reports, r => Assert.Equal(4, r.Total));
        Assert.All(reports, r => Assert.Equal(0, r.Failed));
    }

    // ── Fetch (shared local scan) ───────────────────────────────────────────

    [Fact]
    public async Task Fetch_picks_up_only_images_and_honours_recursion()
    {
        var dir = Directory.CreateTempSubdirectory("synapic-fetch-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.jpg"), "x");
            File.WriteAllText(Path.Combine(dir, "b.png"), "x");
            File.WriteAllText(Path.Combine(dir, "c.txt"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "d.tif"), "x");

            var flat = await WorkflowRunner.FetchItemsAsync(
                new DatasourceSelection { LocalPath = dir, LocalRecursive = false }, CancellationToken.None);
            Assert.Equal(2, flat.Count);

            var recursive = await WorkflowRunner.FetchItemsAsync(
                new DatasourceSelection { LocalPath = dir, LocalRecursive = true }, CancellationToken.None);
            Assert.Equal(3, recursive.Count);
            Assert.Contains(recursive, i => i.FileName == "d.tif");
            Assert.DoesNotContain(recursive, i => i.FileName == "c.txt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Fetch_of_a_missing_folder_throws_instead_of_returning_empty()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"synapic-gone-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            WorkflowRunner.FetchItemsAsync(new DatasourceSelection { LocalPath = missing }, CancellationToken.None));
    }
}
