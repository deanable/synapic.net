using Synapic.Indexer.Configuration;
using Synapic.Indexer.Detection;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class ScanSchedulerTests : IDisposable
{
    private readonly string _directory;
    private readonly TestClock _clock = new();
    private readonly IndexingQueueRepository _repository;

    public ScanSchedulerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "synapic-indexer-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _repository = new IndexingQueueRepository(Path.Combine(_directory, "indexer.db"), _clock);
        _repository.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _repository.ClearPool();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeSource : IWorkSource
    {
        private readonly bool _throws;
        public FakeSource(string name, bool throws = false)
        {
            Name = name;
            _throws = throws;
        }

        public string Name { get; }
        public int Calls { get; private set; }
        public int Found { get; set; } = 3;

        public Task<SourceScanResult> ScanAsync(CancellationToken ct)
        {
            Calls++;
            if (_throws) throw new InvalidOperationException("source exploded");
            return Task.FromResult(new SourceScanResult { Found = Found, Enqueued = Found });
        }
    }

    private static IndexerConfig Config(string mode = "Interval") => new()
    {
        Schedule = new ScheduleSettings { Mode = mode, IntervalMinutes = 15 },
    };

    [Fact]
    public async Task One_pass_runs_every_source_in_order()
    {
        var first = new FakeSource("local");
        var second = new FakeSource("daminion");
        var scheduler = new ScanScheduler(new IWorkSource[] { first, second }, Config(), _clock);

        var results = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.All(results, result => Assert.Equal(3, result.Enqueued));
    }

    [Fact]
    public async Task A_throwing_source_does_not_stop_the_next_one()
    {
        var broken = new FakeSource("broken", throws: true);
        var healthy = new FakeSource("healthy");
        var scheduler = new ScanScheduler(new IWorkSource[] { broken, healthy }, Config(), _clock);

        var results = await scheduler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Unavailable);
        Assert.False(results[1].Unavailable);
        Assert.Equal(1, healthy.Calls);
    }

    [Fact]
    public async Task Interval_mode_runs_once_at_startup()
    {
        var source = new FakeSource("local");
        var scheduler = new ScanScheduler(new IWorkSource[] { source }, Config(), _clock);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (source.Calls == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(source.Calls >= 1);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Watch_mode_never_scans_on_an_interval()
    {
        var source = new FakeSource("local");
        var scheduler = new ScanScheduler(new IWorkSource[] { source }, Config("Watch"), _clock);

        await scheduler.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("Interval", true)]
    [InlineData("intervalandwatch", true)]
    [InlineData("Watch", false)]
    [InlineData("nonsense", false)]
    public void Mode_predicate_matches_the_config_words(string mode, bool expected) =>
        Assert.Equal(expected, ScanScheduler.ModeIncludesInterval(mode));

    [Fact]
    public async Task The_activity_adapter_reads_the_queue_not_a_heartbeat()
    {
        var activity = new QueueSidecarActivity(_repository, _clock);
        Assert.False(activity.HasPendingWork);
        Assert.Null(activity.LastWorkAt);

        var key = PathKey.ForLocal(Path.Combine(_directory, "a.jpg"));
        var enqueuedAt = _clock.GetUtcNow().UtcDateTime;
        await _repository.EnqueueAsync(new[]
        {
            new JobCandidate(key, "local", Path.Combine(_directory, "a.jpg"), null, "a.jpg", 10,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "hash"),
        });

        // The 1 s cache is deliberate; advance the clock past it rather than
        // reaching into the adapter's internals.
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(activity.HasPendingWork);
        Assert.Equal(enqueuedAt, activity.LastWorkAt);

        var claimed = await _repository.ClaimBatchAsync(1);
        Assert.True(await _repository.MarkCompletedAsync(claimed[0].Id));
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.False(activity.HasPendingWork);
        Assert.Null(activity.LastWorkAt);
    }

    [Fact]
    public async Task The_activity_adapter_serves_a_cached_answer_within_one_second()
    {
        var activity = new QueueSidecarActivity(_repository, _clock);
        Assert.False(activity.HasPendingWork);

        await _repository.EnqueueAsync(new[]
        {
            new JobCandidate("local|c:\\x.jpg", "local", "c:\\x.jpg", null, "x.jpg", 1,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "h"),
        });

        Assert.False(activity.HasPendingWork); // still the cached answer
        _clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.True(activity.HasPendingWork);
    }
}
