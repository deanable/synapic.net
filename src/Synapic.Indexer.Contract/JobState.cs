namespace Synapic.Indexer.Contract;

/// <summary>
/// The five queue states from the spec (§5.1) and the transitions between them
/// (§8). Stored as these exact names: a state a human reads in <c>--status</c>
/// or a SQL client should be the same word the code uses.
/// </summary>
public enum JobState
{
    Pending,
    InFlight,
    Completed,
    TransientFailure,
    Failed,
}

/// <summary>Storage-string mapping for <see cref="JobState"/>.</summary>
public static class JobStates
{
    public static string ToStorage(JobState state) => state switch
    {
        JobState.Pending => "Pending",
        JobState.InFlight => "InFlight",
        JobState.Completed => "Completed",
        JobState.TransientFailure => "TransientFailure",
        JobState.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown job state"),
    };

    public static bool TryParse(string? value, out JobState state)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "pending": state = JobState.Pending; return true;
            case "inflight": state = JobState.InFlight; return true;
            case "completed": state = JobState.Completed; return true;
            case "transientfailure": state = JobState.TransientFailure; return true;
            case "failed": state = JobState.Failed; return true;
            default: state = default; return false;
        }
    }

    public static bool IsOpen(JobState state) =>
        state is JobState.Pending or JobState.InFlight or JobState.TransientFailure;
}
