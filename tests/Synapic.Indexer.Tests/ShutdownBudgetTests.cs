using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synapic.Core.Services;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Hosting;
using Xunit;

namespace Synapic.Indexer.Tests;

/// <summary>
/// `sc stop` and a console Ctrl+C both have ten seconds (D-11-12, spec §9.4).
/// The test measures a real host stop instead of trusting the setting.
/// </summary>
public sealed class ShutdownBudgetTests : IDisposable
{
    private readonly string _root;
    private readonly IndexerPaths _paths;

    public ShutdownBudgetTests()
    {
        SynapicLog.ResetForTests();
        _root = Path.Combine(Path.GetTempPath(), "synapic-indexer-stop-" + Guid.NewGuid().ToString("N"));
        _paths = new IndexerPaths(_root);
        _paths.EnsureCreated();
        IndexerLogging.Configure(_paths, "Information");
    }

    public void Dispose()
    {
        SynapicLog.ResetForTests();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_host_with_the_worker_stops_inside_the_budget()
    {
        var config = IndexerConfig.CreateDefault();
        config.Engine.ModelId = "LiquidAI/LFM2.5-VL-1.6B";
        config.Schedule.Mode = "Interval";

        var supervisor = new FakeSupervisor();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(config);
                services.AddSingleton(_paths);
                services.AddSingleton<ISidecarSupervisor>(supervisor);
                services.AddHostedService<IndexerWorkerService>();
            })
            .Build();

        await host.StartAsync();
        // BackgroundService.StartAsync returns before ExecuteAsync has run.
        await WaitUntilAsync(() => supervisor.StartCount == 1);

        var stopwatch = Stopwatch.StartNew();
        await host.StopAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"stop took {stopwatch.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(1, supervisor.StopCount);

        SynapicLog.ResetForTests(); // flushes the file
        var log = File.ReadAllText(Directory.GetFiles(_paths.LogsDirectory, "indexer-*.log").Single());
        Assert.Contains("Indexer service started", log);
        Assert.Contains("Indexer service stopped", log);
    }

    [Fact]
    public async Task Stopping_before_start_completes_is_still_bounded()
    {
        var config = IndexerConfig.CreateDefault();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(config);
                services.AddSingleton<ISidecarSupervisor>(new FakeSupervisor());
                services.AddHostedService<IndexerWorkerService>();
            })
            .Build();

        var stopwatch = Stopwatch.StartNew();
        await host.StartAsync();
        await host.StopAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"start+stop took {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition was not met within the timeout");
    }

    private sealed class FakeSupervisor : ISidecarSupervisor
    {
        public bool IsReady { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task EnsureReadyAsync(CancellationToken ct)
        {
            StartCount++;
            IsReady = true;
            return Task.CompletedTask;
        }

        public Task EnsureStoppedAsync(CancellationToken ct)
        {
            StopCount++;
            IsReady = false;
            return Task.CompletedTask;
        }
    }
}
