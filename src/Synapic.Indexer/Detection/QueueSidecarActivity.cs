using Synapic.Indexer.Hosting;
using Synapic.Indexer.State;

namespace Synapic.Indexer.Detection;

/// <summary>
/// Answers the sidecar's idle timer from the queue itself (D-11-18): pending work
/// keeps the sidecar alive, and a quiet queue lets it stop on schedule. No
/// heartbeat file, no second source of truth.
/// </summary>
public sealed class QueueSidecarActivity : ISidecarActivity
{
    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(1);

    private readonly IndexingQueueRepository _repository;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private (DateTimeOffset ReadAt, QueueActivity Value)? _cached;

    public QueueSidecarActivity(IndexingQueueRepository repository, TimeProvider? clock = null)
    {
        _repository = repository;
        _clock = clock ?? TimeProvider.System;
    }

    public DateTime? LastWorkAt => Snapshot().LastWorkAt;

    public bool HasPendingWork => Snapshot().HasPendingWork;

    /// <summary>
    /// The idle timer reads both properties within a few instructions of each
    /// other every 30 s; one query per second is plenty and keeps them consistent.
    /// </summary>
    private QueueActivity Snapshot()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_cached is { } cached && now - cached.ReadAt < CacheWindow)
                return cached.Value;

            var value = _repository.GetActivity();
            _cached = (now, value);
            return value;
        }
    }

    /// <summary>Drops the cache, so a test can observe a change without moving the clock.</summary>
    internal void Invalidate()
    {
        lock (_gate) _cached = null;
    }
}
