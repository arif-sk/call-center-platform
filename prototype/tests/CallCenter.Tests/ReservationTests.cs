using CallCenter.Api.Services;
using CallCenter.Domain;

namespace CallCenter.Tests;

/// <summary>
/// The reservation protocol is the correctness core of the routing engine
/// (docs/04-system-design.md §4.3.2). These tests assert the three invariants the whole platform
/// rests on — and the concurrency one is the reason this is a compare-and-set rather than a
/// read-then-write.
/// </summary>
public class ReservationTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private static (PlatformState State, Agent Agent) Available()
    {
        var state = new PlatformState();
        var agent = new Agent { Id = Guid.NewGuid(), DisplayName = "Amara", Extension = "1001" };
        AgentStateMachine.Force(agent, AgentState.Available, DateTimeOffset.UtcNow);
        state.AddAgent(agent);
        return (state, agent);
    }

    [Fact]
    public void Reserving_an_available_agent_succeeds_and_moves_them_out_of_routing()
    {
        var (state, agent) = Available();

        var result = state.TryReserve(agent.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, Ttl);

        Assert.True(result.Success);
        Assert.Equal(AgentState.Reserved, agent.State);
        Assert.False(agent.IsRoutable);
    }

    [Fact]
    public void An_agent_cannot_be_reserved_twice()
    {
        var (state, agent) = Available();
        var now = DateTimeOffset.UtcNow;

        var first = state.TryReserve(agent.Id, Guid.NewGuid(), now, Ttl);
        var second = state.TryReserve(agent.Id, Guid.NewGuid(), now, Ttl);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal(ReserveOutcome.AgentNotAvailable, second.Outcome);
    }

    [Fact]
    public void Concurrent_reservations_of_the_same_agent_produce_exactly_one_winner()
    {
        // The scenario this guards against: two queues, or two matching cycles, racing for the same
        // newly-available agent. Exactly one must win, or a customer is bridged to a busy agent.
        var (state, agent) = Available();
        var now = DateTimeOffset.UtcNow;

        var results = new ReserveResult[64];
        Parallel.For(0, results.Length, i =>
            results[i] = state.TryReserve(agent.Id, Guid.NewGuid(), now, Ttl));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(63, results.Count(r => r.Outcome == ReserveOutcome.AgentNotAvailable));
    }

    [Fact]
    public void A_valid_token_is_accepted()
    {
        var (state, agent) = Available();
        var callId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var reservation = state.TryReserve(agent.Id, callId, now, Ttl).Reservation!;

        Assert.True(state.ValidateReservation(agent.Id, callId, reservation.Token, now));
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("")]
    public void A_wrong_token_is_rejected(string token)
    {
        // This is what stops an authenticated agent answering a call they were never offered
        // (NFR-SEC3) — the difference between a UI convention and a rule.
        var (state, agent) = Available();
        var callId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        state.TryReserve(agent.Id, callId, now, Ttl);

        Assert.False(state.ValidateReservation(agent.Id, callId, token, now));
    }

    [Fact]
    public void A_token_for_a_different_call_is_rejected()
    {
        var (state, agent) = Available();
        var now = DateTimeOffset.UtcNow;
        var reservation = state.TryReserve(agent.Id, Guid.NewGuid(), now, Ttl).Reservation!;

        Assert.False(state.ValidateReservation(agent.Id, Guid.NewGuid(), reservation.Token, now));
    }

    [Fact]
    public void An_expired_reservation_is_rejected_and_surfaces_for_sweeping()
    {
        var (state, agent) = Available();
        var callId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var reservation = state.TryReserve(agent.Id, callId, now, Ttl).Reservation!;

        var afterTimeout = now.Add(Ttl).AddMilliseconds(1);

        Assert.False(state.ValidateReservation(agent.Id, callId, reservation.Token, afterTimeout));
        Assert.Contains(state.ExpiredReservations(afterTimeout), a => a.Id == agent.Id);
    }

    [Fact]
    public void A_not_ready_agent_cannot_be_reserved()
    {
        var state = new PlatformState();
        var agent = new Agent { Id = Guid.NewGuid(), DisplayName = "Ben", Extension = "1002" };
        AgentStateMachine.Force(agent, AgentState.NotReady, DateTimeOffset.UtcNow, NotReadyReasons.Break);
        state.AddAgent(agent);

        var result = state.TryReserve(agent.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, Ttl);

        Assert.False(result.Success);
    }
}
