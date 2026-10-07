namespace Synapic.Avalonia.Services.Processing;

/// <summary>
/// Cooperative pause signal for the processing batch. Items call
/// <see cref="PauseToken.WaitWhilePausedAsync"/> between work units; running
/// items finish but no new ones start until <see cref="Resume"/> is called.
/// Abort (cancellation) always wins over pause.
/// </summary>
public sealed class PauseTokenSource
{
    private volatile TaskCompletionSource<bool>? _pausedTcs;

    public bool IsPaused => _pausedTcs is not null;

    public void Pause()
    {
        if (_pausedTcs is null)
            _pausedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        var tcs = _pausedTcs;
        _pausedTcs = null;
        tcs?.TrySetResult(true);
    }

    public PauseToken Token => new(this);

    /// <summary>Task that completes on the next resume (safe for multiple waiters).</summary>
    public Task WaitUntilResumedAsync() => _pausedTcs?.Task ?? Task.CompletedTask;
}

/// <summary>Read-side handle handed to the orchestrator (mirrors PauseTokenSource).</summary>
public readonly struct PauseToken
{
    private readonly PauseTokenSource? _source;

    internal PauseToken(PauseTokenSource source) => _source = source;

    public static PauseToken None => default;

    public bool IsPaused => _source?.IsPaused ?? false;

    /// <summary>Completes when resumed; throws OperationCanceledException if cancelled while paused.</summary>
    public async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        if (_source is null || !_source.IsPaused) return;

        var waitTask = _source.WaitUntilResumedAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await completed.ConfigureAwait(false);
    }
}
