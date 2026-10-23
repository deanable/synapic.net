using Microsoft.Extensions.Hosting;
using Synapic.Core.Services;
using Synapic.Indexer.Configuration;

namespace Synapic.Indexer.Detection;

/// <summary>
/// Runs every configured detector once at startup and then on the configured
/// interval (D-11-19). One source failing is logged and does not stop the loop:
/// a catalog outage at 09:00 must not mean no scans at 09:15.
/// </summary>
public sealed class ScanScheduler : BackgroundService
{
    private readonly IReadOnlyList<IWorkSource> _sources;
    private readonly IndexerConfig _config;
    private readonly TimeProvider _clock;

    public ScanScheduler(IReadOnlyList<IWorkSource> sources, IndexerConfig config, TimeProvider? clock = null)
    {
        _sources = sources;
        _config = config;
        _clock = clock ?? TimeProvider.System;
    }

    public static bool ModeIncludesInterval(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() is "interval" or "intervalandwatch";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Clamp(_config.Schedule.IntervalMinutes, 1, 1440));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ModeIncludesInterval(_config.Schedule.Mode))
        {
            SynapicLog.Debug(nameof(ScanScheduler),
                $"Schedule mode '{_config.Schedule.Mode}' has no interval scans; the watcher answers events.");
            return;
        }

        if (_sources.Count == 0)
        {
            SynapicLog.Info(nameof(ScanScheduler),
                "No interval work source is configured; detection waits for a configured datasource.");
            return;
        }

        SynapicLog.Info(nameof(ScanScheduler),
            $"Interval detection every {_config.Schedule.IntervalMinutes} min " +
            $"({string.Join(", ", _sources.Select(source => source.Name))}).");

        try
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Interval, _clock, stoppingToken).ConfigureAwait(false);
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The stop signal, not a failure.
        }
    }

    /// <summary>One pass over every source. Public so a test can drive the exact work a tick does.</summary>
    public async Task<IReadOnlyList<SourceScanResult>> RunOnceAsync(CancellationToken ct)
    {
        var results = new List<SourceScanResult>(_sources.Count);

        foreach (var source in _sources)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await source.ScanAsync(ct).ConfigureAwait(false);
                results.Add(result);
                Log(source, result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // A detector that throws is a bug, not a reason to stop detecting.
                SynapicLog.Error(nameof(ScanScheduler), $"Scan source '{source.Name}' failed.", e);
                results.Add(SourceScanResult.UnavailableResult());
            }
        }

        return results;
    }

    private static void Log(IWorkSource source, SourceScanResult result)
    {
        if (result.Unavailable)
        {
            SynapicLog.Warning(nameof(ScanScheduler),
                $"scan: source={source.Name} unavailable; the queue is unchanged.");
            return;
        }

        var message = $"scan: source={source.Name} found={result.Found} enqueued={result.Enqueued} " +
                      $"skipped={result.Skipped} errors={result.Errors}";

        if (result.Found == 0 && result.Enqueued == 0 && result.Errors == 0)
            SynapicLog.Debug(nameof(ScanScheduler), message);
        else
            SynapicLog.Info(nameof(ScanScheduler), message);
    }
}
