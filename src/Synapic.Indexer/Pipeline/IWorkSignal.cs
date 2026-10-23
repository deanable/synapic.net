namespace Synapic.Indexer.Pipeline;

/// <summary>
/// Wakes the executor when new work reaches the queue. Detection sets it after an
/// enqueue; the executor also polls on a timeout, so a signal missed while the
/// process was busy cannot strand work overnight.
/// </summary>
public interface IWorkSignal
{
    void Set();

    /// <summary>Waits for a signal or the timeout; true when a signal arrived.</summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>A one-slot semaphore: many sets collapse into one wake-up, which is all the executor needs.</summary>
public sealed class WorkSignal : IWorkSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Set()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending; one is enough.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        _semaphore.WaitAsync(timeout, ct);
}
