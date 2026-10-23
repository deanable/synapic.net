namespace Synapic.Indexer.State;

/// <summary>
/// One unit of detected work, before it reaches the queue. Every detector
/// (interval scan, watcher, Daminion query) produces these, and
/// <see cref="IWorkIntake"/> is the only thing that turns them into rows.
/// </summary>
/// <param name="Key">Stable identity: <c>local|&lt;canonical path&gt;</c> or <c>daminion|&lt;id&gt;</c>.</param>
/// <param name="Source"><c>local</c> or <c>daminion</c>; informational, for <c>--status</c> and logs.</param>
/// <param name="Path">The local file path, or null for a catalog item.</param>
/// <param name="DaminionId">The catalog item id, or null for a local file.</param>
/// <param name="FileName">The display name; never empty.</param>
/// <param name="Size">Bytes, or null when the source cannot report it (a catalog item with no size).</param>
/// <param name="MtimeUtc">Last write time in UTC, or null for a catalog item.</param>
/// <param name="ContentHash">
/// SHA-256 hex for a local file, <c>srv:&lt;hash&gt;</c> for a catalog item whose server hash is known, or null.
/// </param>
public sealed record JobCandidate(
    string Key,
    string Source,
    string? Path,
    int? DaminionId,
    string FileName,
    long? Size,
    DateTime? MtimeUtc,
    string? ContentHash);

/// <summary>What one enqueue pass did.</summary>
public sealed record IntakeResult(int Enqueued, int Skipped, IReadOnlyList<string> EnqueuedKeys)
{
    public static IntakeResult Empty { get; } = new(0, 0, Array.Empty<string>());

    public int Total => Enqueued + Skipped;
}

/// <summary>What the idle timer needs to know, read in one pass over the queue.</summary>
public sealed record QueueActivity(bool HasPendingWork, DateTime? LastWorkAt)
{
    public static QueueActivity Idle { get; } = new(false, null);
}
