namespace Synapic.Indexer.State;

/// <summary>Counts per state plus the newest failures — everything <c>--status</c> prints.</summary>
public sealed record QueueSummary
{
    public int Pending { get; init; }
    public int InFlight { get; init; }
    public int Completed { get; init; }
    public int TransientFailure { get; init; }
    public int Failed { get; init; }

    public IReadOnlyList<QueueFailure> RecentFailures { get; init; } = Array.Empty<QueueFailure>();

    public int Total => Pending + InFlight + Completed + TransientFailure + Failed;

    public int CountOf(JobState state) => state switch
    {
        JobState.Pending => Pending,
        JobState.InFlight => InFlight,
        JobState.Completed => Completed,
        JobState.TransientFailure => TransientFailure,
        JobState.Failed => Failed,
        _ => 0,
    };
}

/// <summary>One of the newest failed or transient rows, with the error that stopped it.</summary>
public sealed record QueueFailure(long Id, string Key, string? LastError, int Attempts);
