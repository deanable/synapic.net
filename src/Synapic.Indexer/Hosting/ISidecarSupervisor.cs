namespace Synapic.Indexer.Hosting;

/// <summary>
/// Owns the sidecar process from inside the service. Phase 7 calls
/// <see cref="EnsureReadyAsync"/> before a batch; the restart/backoff layer wraps
/// this rather than launching processes itself.
/// </summary>
public interface ISidecarSupervisor
{
    bool IsReady { get; }

    /// <summary>
    /// Starts the sidecar when needed and returns only after <c>/health</c> says
    /// <c>ready</c>. Throws <see cref="SidecarUnavailableException"/> when the
    /// executable is missing and <see cref="TimeoutException"/> when readiness
    /// never arrives.
    /// </summary>
    Task EnsureReadyAsync(CancellationToken ct);

    /// <summary>Stops the sidecar (5 s budget), cancels the idle timer and clears the pid record.</summary>
    Task EnsureStoppedAsync(CancellationToken ct);
}

/// <summary>
/// What the idle timer needs to know about real work (phase 6/7 implement it; a
/// build with no scheduler ships <see cref="NoWorkActivity"/>).
/// </summary>
public interface ISidecarActivity
{
    DateTime? LastWorkAt { get; }
    bool HasPendingWork { get; }
}

/// <summary>No scheduler yet: nothing has run and nothing is queued.</summary>
public sealed class NoWorkActivity : ISidecarActivity
{
    public DateTime? LastWorkAt => null;
    public bool HasPendingWork => false;
}

/// <summary>The sidecar executable could not be found where the service ships it.</summary>
public sealed class SidecarUnavailableException : Exception
{
    public SidecarUnavailableException(string message) : base(message) { }
}
