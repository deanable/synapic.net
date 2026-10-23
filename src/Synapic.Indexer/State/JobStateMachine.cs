namespace Synapic.Indexer.State;

/// <summary>
/// The permitted job transitions (spec §5.1, §8). The table is the whole
/// contract, in one place: a transition that is not listed here is a bug in the
/// caller, not a new state to tolerate.
/// </summary>
public static class JobStateMachine
{
    public static bool CanTransition(JobState from, JobState to) => (from, to) switch
    {
        // Claimed by the executor.
        (JobState.Pending, JobState.InFlight) => true,

        // The three outcomes of a processed item, plus recovery when the
        // process died mid-item (also used when a claim is abandoned).
        (JobState.InFlight, JobState.Completed) => true,
        (JobState.InFlight, JobState.TransientFailure) => true,
        (JobState.InFlight, JobState.Failed) => true,
        (JobState.InFlight, JobState.Pending) => true,

        // A retry (or the watcher noticing the locked file is now free), and
        // exhausting the retries.
        (JobState.TransientFailure, JobState.Pending) => true,
        (JobState.TransientFailure, JobState.Failed) => true,

        // A permanently failed file is only ever reconsidered because it
        // changed on disk, which the repository checks before transitioning.
        (JobState.Failed, JobState.Pending) => true,

        // The same rule for a completed item: changed bytes mean new work.
        // EnqueueAsync is the only caller and checks the fingerprint first.
        (JobState.Completed, JobState.Pending) => true,

        _ => false,
    };

    public static void EnsureTransition(JobState from, JobState to, string key)
    {
        if (!CanTransition(from, to))
            throw new InvalidJobTransitionException(key, from, to);
    }
}

/// <summary>A caller tried a transition the state machine forbids.</summary>
public sealed class InvalidJobTransitionException : Exception
{
    public InvalidJobTransitionException(string key, JobState from, JobState to)
        : base($"Job '{key}' cannot move from {JobStates.ToStorage(from)} to {JobStates.ToStorage(to)}.")
    {
        Key = key;
        From = from;
        To = to;
    }

    public string Key { get; }
    public JobState From { get; }
    public JobState To { get; }
}
