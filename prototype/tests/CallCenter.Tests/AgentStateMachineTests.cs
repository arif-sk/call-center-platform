using CallCenter.Domain;

namespace CallCenter.Tests;

/// <summary>
/// The state machine is the single highest-value thing to unit-test in the platform: an invalid
/// transition means either a call offered to someone who cannot take it, or an agent silently
/// removed from routing. Both are invisible in aggregate metrics and very visible on the floor.
/// </summary>
public class AgentStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    private static Agent NewAgent(AgentState state = AgentState.LoggedOut)
    {
        var agent = new Agent { Id = Guid.NewGuid(), DisplayName = "Test Agent", Extension = "9999" };
        if (state != AgentState.LoggedOut) AgentStateMachine.Force(agent, state, Now);
        return agent;
    }

    [Theory]
    [InlineData(AgentState.LoggedOut, AgentState.Available)]
    [InlineData(AgentState.Available, AgentState.Reserved)]
    [InlineData(AgentState.Reserved, AgentState.Ringing)]
    [InlineData(AgentState.Ringing, AgentState.OnCall)]
    [InlineData(AgentState.OnCall, AgentState.AfterCallWork)]
    [InlineData(AgentState.AfterCallWork, AgentState.Available)]
    [InlineData(AgentState.Available, AgentState.NotReady)]
    public void Legal_transitions_are_allowed(AgentState from, AgentState to)
    {
        var agent = NewAgent(from);
        var result = AgentStateMachine.Transition(agent, to, Now, reason: NotReadyReasons.Break);

        Assert.True(result.Allowed, result.Error);
        Assert.Equal(to, agent.State);
    }

    [Theory]
    [InlineData(AgentState.LoggedOut, AgentState.OnCall)]
    [InlineData(AgentState.OnCall, AgentState.Available)]      // must wrap up first
    [InlineData(AgentState.NotReady, AgentState.Reserved)]     // not routable
    [InlineData(AgentState.AfterCallWork, AgentState.OnCall)]
    public void Illegal_transitions_are_refused(AgentState from, AgentState to)
    {
        var agent = NewAgent(from);
        var result = AgentStateMachine.Transition(agent, to, Now);

        Assert.False(result.Allowed);
        Assert.Equal(from, agent.State);
    }

    [Fact]
    public void Agent_cannot_put_themselves_on_a_call()
    {
        // A client that can assert "I am OnCall" can also assert "I am Available" while asleep.
        var agent = NewAgent(AgentState.Available);

        var result = AgentStateMachine.Transition(agent, AgentState.OnCall, Now, agentInitiated: true);

        Assert.False(result.Allowed);
        Assert.Equal(AgentState.Available, agent.State);
    }

    [Fact]
    public void Agent_cannot_go_not_ready_while_on_a_live_call()
    {
        var agent = NewAgent(AgentState.OnCall);

        var result = AgentStateMachine.Transition(agent, AgentState.NotReady, Now,
            reason: NotReadyReasons.Break, agentInitiated: true);

        Assert.False(result.Allowed);
        Assert.Contains("Finish or release", result.Error);
    }

    [Fact]
    public void Not_ready_requires_a_reason_code()
    {
        // Without this, "Not Ready" is unreportable and adherence measurement is impossible.
        var agent = NewAgent(AgentState.Available);

        var result = AgentStateMachine.Transition(agent, AgentState.NotReady, Now, reason: null);

        Assert.False(result.Allowed);
        Assert.Equal(AgentState.Available, agent.State);
    }

    [Fact]
    public void Becoming_available_resets_the_idle_clock_and_rna_counter()
    {
        var agent = NewAgent(AgentState.Available);
        agent.RecordRna();
        agent.RecordRna();
        Assert.Equal(2, agent.ConsecutiveRna);

        AgentStateMachine.Force(agent, AgentState.NotReady, Now, NotReadyReasons.RingNoAnswer);
        AgentStateMachine.Transition(agent, AgentState.Available, Now.AddMinutes(1));

        Assert.Equal(0, agent.ConsecutiveRna);
        Assert.Equal(Now.AddMinutes(1), agent.AvailableSince);
    }

    [Fact]
    public void Leaving_a_routable_state_clears_the_call_and_reservation()
    {
        var agent = NewAgent(AgentState.Available);
        var callId = Guid.NewGuid();
        agent.SetReservation(Reservation.Issue(callId, agent.Id, Now, TimeSpan.FromSeconds(15)));

        AgentStateMachine.Force(agent, AgentState.NotReady, Now, NotReadyReasons.Break);

        Assert.Null(agent.Reservation);
        Assert.Null(agent.CurrentCallId);
    }

    [Fact]
    public void Only_available_agents_are_routable()
    {
        foreach (var state in Enum.GetValues<AgentState>())
        {
            var agent = NewAgent(state);
            Assert.Equal(state == AgentState.Available, agent.IsRoutable);
        }
    }
}
