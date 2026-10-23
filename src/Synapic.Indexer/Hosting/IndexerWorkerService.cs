using Microsoft.Extensions.Hosting;
using Synapic.Core.Services;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Pipeline;

namespace Synapic.Indexer.Hosting;

/// <summary>
/// The service's lifetime: start the sidecar, report ready, wait for the stop
/// signal, stop the sidecar and return. Detection runs as separate hosted
/// services registered after this one, so the host's reverse-order shutdown stops
/// them before the sidecar they depend on (D-11-12, HOST-06).
/// </summary>
public sealed class IndexerWorkerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(5);

    private readonly IndexerConfig _config;
    private readonly ISidecarSupervisor _supervisor;
    private readonly BatchExecutor? _executor;
    private readonly IWorkSignal? _signal;
    private readonly TimeProvider _clock;

    public IndexerWorkerService(
        IndexerConfig config,
        ISidecarSupervisor supervisor,
        BatchExecutor? executor = null,
        IWorkSignal? signal = null,
        TimeProvider? clock = null)
    {
        _config = config;
        _supervisor = supervisor;
        _executor = executor;
        _signal = signal;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SynapicLog.Info(nameof(IndexerWorkerService),
            $"Indexer service started (schedule {_config.Schedule.Mode}, every {_config.Schedule.IntervalMinutes} min, " +
            $"max {_config.Service.MaxConcurrentItems} concurrent, device {IndexerConfigValidator.NormalizeDevice(_config.Engine.Device)}).");

        try
        {
            await _supervisor.EnsureReadyAsync(stoppingToken).ConfigureAwait(false);
            SynapicLog.Info(nameof(IndexerWorkerService),
                "Indexer ready; sidecar is accepting work.");

            if (_executor is null || _signal is null)
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await ExecuteLoopAsync(_executor, _signal, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The stop signal, not a failure.
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(IndexerWorkerService), "The indexer worker failed; the service is stopping.", e);
            throw;
        }
        finally
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _supervisor.EnsureStoppedAsync(budget.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                SynapicLog.Warning(nameof(IndexerWorkerService), $"Sidecar shutdown reported {e.GetType().Name}: {e.Message}");
            }

            SynapicLog.Info(nameof(IndexerWorkerService), "Indexer service stopped.");
        }
    }

    /// <summary>
    /// The execution loop: wait for detection to say there is work (or for the
    /// poll to expire, so a missed signal cannot strand the queue), run one batch,
    /// repeat. A failing batch is logged and the loop keeps going — the service's
    /// job is to stay up.
    /// </summary>
    private async Task ExecuteLoopAsync(
        BatchExecutor executor, IWorkSignal signal, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var signalled = await signal.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);
            if (!signalled)
                SynapicLog.Debug(nameof(IndexerWorkerService), "No work signal in 30 s; polling the queue.");

            try
            {
                await executor.ExecuteOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                SynapicLog.Error(nameof(IndexerWorkerService),
                    "A batch failed; the next one will still run.", e);
                await Task.Delay(FailureDelay, _clock, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
