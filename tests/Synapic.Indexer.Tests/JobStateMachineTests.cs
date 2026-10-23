using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class JobStateMachineTests
{
    private static readonly JobState[] All = Enum.GetValues<JobState>();

    public static TheoryData<JobState, JobState> Permitted => new()
    {
        { JobState.Pending, JobState.InFlight },
        { JobState.InFlight, JobState.Completed },
        { JobState.InFlight, JobState.TransientFailure },
        { JobState.InFlight, JobState.Failed },
        { JobState.InFlight, JobState.Pending },
        { JobState.TransientFailure, JobState.Pending },
        { JobState.TransientFailure, JobState.Failed },
        { JobState.Failed, JobState.Pending },
        { JobState.Completed, JobState.Pending },
    };

    [Theory]
    [MemberData(nameof(Permitted))]
    public void A_permitted_transition_is_allowed(JobState from, JobState to)
    {
        Assert.True(JobStateMachine.CanTransition(from, to));
        JobStateMachine.EnsureTransition(from, to, "local|c:\\a.jpg");
    }

    public static TheoryData<JobState, JobState> Forbidden
    {
        get
        {
            var data = new TheoryData<JobState, JobState>();
            foreach (var from in All)
            foreach (var to in All)
            {
                var permitted = (from, to) is
                    (JobState.Pending, JobState.InFlight) or
                    (JobState.InFlight, JobState.Completed) or
                    (JobState.InFlight, JobState.TransientFailure) or
                    (JobState.InFlight, JobState.Failed) or
                    (JobState.InFlight, JobState.Pending) or
                    (JobState.TransientFailure, JobState.Pending) or
                    (JobState.TransientFailure, JobState.Failed) or
                    (JobState.Failed, JobState.Pending) or
                    (JobState.Completed, JobState.Pending);
                if (!permitted) data.Add(from, to);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Forbidden))]
    public void Every_other_pair_throws(JobState from, JobState to)
    {
        Assert.False(JobStateMachine.CanTransition(from, to));
        var error = Assert.Throws<InvalidJobTransitionException>(
            () => JobStateMachine.EnsureTransition(from, to, "daminion|7"));
        Assert.Equal(from, error.From);
        Assert.Equal(to, error.To);
        Assert.Equal("daminion|7", error.Key);
    }

    [Fact]
    public void Completing_a_completed_job_is_not_a_transition()
    {
        Assert.False(JobStateMachine.CanTransition(JobState.Completed, JobState.Completed));
        Assert.Throws<InvalidJobTransitionException>(
            () => JobStateMachine.EnsureTransition(JobState.Completed, JobState.Completed, "local|c:\\a.jpg"));
    }

    [Fact]
    public void Storage_names_round_trip_case_insensitively()
    {
        foreach (var state in All)
        {
            var stored = JobStates.ToStorage(state);
            Assert.True(JobStates.TryParse(stored, out var parsed));
            Assert.Equal(state, parsed);
            Assert.True(JobStates.TryParse(stored.ToLowerInvariant(), out var lower));
            Assert.Equal(state, lower);
        }

        Assert.False(JobStates.TryParse("running", out _));
    }
}
