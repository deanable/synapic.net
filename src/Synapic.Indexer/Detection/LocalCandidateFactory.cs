using Synapic.Indexer.State;

namespace Synapic.Indexer.Detection;

/// <summary>What a local path turned out to be when examined for queueing.</summary>
internal enum LocalCandidateStatus
{
    Candidate,
    AlreadyQueued,
    Unchanged,
    Empty,
    Missing,
    Unreadable,
}

internal readonly record struct LocalCandidateResult(LocalCandidateStatus Status, JobCandidate? Candidate = null);

/// <summary>
/// The one place a local file is turned into a <see cref="JobCandidate"/> or
/// rejected. The interval scan and the watcher must apply the same fingerprint
/// rules, or a file the scan considers unchanged would be re-hashed on every
/// watcher event (and vice versa).
/// </summary>
internal static class LocalCandidateFactory
{
    public static async Task<LocalCandidateResult> TryCreateAsync(
        string path,
        IndexingQueueRepository repository,
        IFileHasher hasher,
        string source,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return new(LocalCandidateStatus.Missing);

        if (info.Length == 0) return new(LocalCandidateStatus.Empty);

        if (!PathKey.TryForLocal(path, out var key)) return new(LocalCandidateStatus.Missing);

        var mtime = info.LastWriteTimeUtc;
        var existing = await repository.GetByKeyAsync(key, ct).ConfigureAwait(false);

        // The queue already owns this file, so hashing it cannot change anything.
        if (existing is { State: JobState.Pending or JobState.InFlight })
            return new(LocalCandidateStatus.AlreadyQueued);

        // Size and last-write time equal: one stat, no content read (SCAN-02).
        if (existing is { State: JobState.Completed } && existing.MatchesSizeAndTime(info.Length, mtime))
            return new(LocalCandidateStatus.Unchanged);

        var hash = await hasher.TryHashAsync(path, ct).ConfigureAwait(false);
        if (hash is null) return new(LocalCandidateStatus.Unreadable);

        return new(LocalCandidateStatus.Candidate, new JobCandidate(
            key, source, path, null, info.Name, info.Length, mtime, hash));
    }
}
