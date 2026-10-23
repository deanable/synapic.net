namespace Synapic.Indexer.Detection;

/// <summary>One path whose debounce window has elapsed.</summary>
/// <param name="Key">Canonical queue key (<c>local|…</c>).</param>
/// <param name="Attempts">How many stability probes have already failed for this path.</param>
public sealed record ReadyPath(string Key, int Attempts);

/// <summary>
/// Accumulates watcher events per canonical path and releases a path only after
/// it has been quiet for the debounce window (D-11-16). A camera copy of fifty
/// files therefore produces fifty candidates once, not a hundred and fifty
/// events.
/// </summary>
/// <remarks>
/// Thread-safe: <see cref="FileSystemWatcher"/> callbacks arrive on thread-pool
/// threads while the flush loop drains. Time comes from an injected
/// <see cref="TimeProvider"/>, so tests move the clock instead of sleeping.
/// </remarks>
public sealed class DebounceBuffer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;

    public DebounceBuffer(TimeSpan window, TimeProvider clock)
    {
        if (window < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
        _clock = clock;
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Records an event for <paramref name="key"/>, restarting its window.</summary>
    public void Add(string key)
    {
        lock (_gate)
        {
            // A new event means the writer is active again; the probe attempt
            // count starts over because this is a different, newer write.
            _entries[key] = new Entry(_clock.GetUtcNow() + _window, Attempts: 0);
        }
    }

    /// <summary>Puts a path back with an explicit delay after a failed stability probe.</summary>
    public void ReArm(string key, TimeSpan delay)
    {
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));

        lock (_gate)
        {
            var attempts = _entries.TryGetValue(key, out var existing) ? existing.Attempts + 1 : 1;
            _entries[key] = new Entry(_clock.GetUtcNow() + delay, attempts);
        }
    }

    /// <summary>Removes and returns every path whose window has elapsed, oldest first.</summary>
    public IReadOnlyList<ReadyPath> DrainReady()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var ready = _entries
                .Where(pair => pair.Value.Due <= now)
                .OrderBy(pair => pair.Value.Due)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ReadyPath(pair.Key, pair.Value.Attempts))
                .ToList();

            foreach (var path in ready) _entries.Remove(path.Key);
            return ready;
        }
    }

    public bool Contains(string key)
    {
        lock (_gate) return _entries.ContainsKey(key);
    }

    private readonly record struct Entry(DateTimeOffset Due, int Attempts);
}
