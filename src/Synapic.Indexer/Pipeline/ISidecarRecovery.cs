using Synapic.Core.Services;
using Synapic.Indexer.Hosting;

namespace Synapic.Indexer.Pipeline;

/// <summary>Restarts a sidecar that died mid-batch, with the spec's bounded backoff.</summary>
public interface ISidecarRecovery
{
    Task<bool> TryRecoverAsync(CancellationToken ct);
}

/// <summary>
/// Three attempts at 1 s, 2 s and 4 s (D-11-25). Delays are injected so tests
/// observe the schedule without sleeping; production passes `Task.Delay`.
/// </summary>
public sealed class SidecarRecovery : ISidecarRecovery
{
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    };

    private readonly ISidecarSupervisor _supervisor;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SidecarRecovery(
        ISidecarSupervisor supervisor,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _supervisor = supervisor;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    public async Task<bool> TryRecoverAsync(CancellationToken ct)
    {
        foreach (var delay in Backoff)
        {
            await _delay(delay, ct).ConfigureAwait(false);
            try
            {
                await _supervisor.EnsureReadyAsync(ct).ConfigureAwait(false);
                SynapicLog.Info(nameof(SidecarRecovery), "The inference sidecar restarted after a crash.");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                SynapicLog.Warning(nameof(SidecarRecovery),
                    $"Sidecar restart failed ({e.GetType().Name}: {e.Message}); retrying.");
            }
        }

        return false;
    }
}
