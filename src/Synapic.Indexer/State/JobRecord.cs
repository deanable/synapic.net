namespace Synapic.Indexer.State;

/// <summary>
/// One row of <c>jobs</c>. Immutable: the repository returns new instances, so
/// nothing can mutate a row it read and believe the database changed with it.
/// </summary>
public sealed record JobRecord
{
    public long Id { get; init; }
    public required string Key { get; init; }
    public string Source { get; init; } = "";
    public string? Path { get; init; }
    public int? DaminionId { get; init; }
    public required string FileName { get; init; }
    public long? Size { get; init; }
    public DateTime? MtimeUtc { get; init; }
    public string? ContentHash { get; init; }
    public JobState State { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public DateTime EnqueuedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? NextAttemptAt { get; init; }

    /// <summary>
    /// The cheap half of the fingerprint matrix: size and last-write time both
    /// match, so a scan can skip the file without reading one byte of it
    /// (SCAN-02's cost model).
    /// </summary>
    public bool MatchesSizeAndTime(long? size, DateTime? mtimeUtc) =>
        Size == size && MtimeUtc == mtimeUtc;

    /// <summary>
    /// The full fingerprint rule. Same stat is enough; when the stat moved, the
    /// content hash decides — a re-saved or touched file with identical bytes is
    /// the same work and must not be enqueued again.
    /// </summary>
    public bool MatchesFingerprint(long? size, DateTime? mtimeUtc, string? hash)
    {
        if (Size != size) return false;
        if (MtimeUtc == mtimeUtc) return true;
        return ContentHash is not null && hash is not null &&
               string.Equals(ContentHash, hash, StringComparison.OrdinalIgnoreCase);
    }
}
