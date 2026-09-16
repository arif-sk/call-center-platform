using CallCenter.Domain;
using CallCenter.Domain.Agents;
using CallCenter.Domain.Calls;

namespace CallCenter.Tests.Domain;

/// <summary>
/// The rules, on their own. No database, no web server, no test doubles — the domain project
/// references nothing, so nothing is needed to test it. This is the payoff of keeping the rules
/// on the entities rather than in a service.
/// </summary>
public class DomainRuleTests
{
    private static readonly DateTimeOffset Nine = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- agents

    [Fact]
    public void An_agent_who_signs_in_is_not_yet_taking_calls()
    {
        var agent = NewAgent();

        agent.SignIn(Nine);

        Assert.Equal(AgentState.NotReady, agent.State);
    }

    [Fact]
    public void An_agent_cannot_sign_out_while_holding_a_call()
    {
        var agent = ReadyAgent();
        agent.OfferCall(Guid.NewGuid(), Nine);

        var error = Assert.Throws<DomainException>(() => agent.SignOut(Nine));

        Assert.Equal("Finish the current call before signing out.", error.Message);
    }

    [Fact]
    public void An_agent_cannot_go_not_ready_in_the_middle_of_a_call()
    {
        var agent = ReadyAgent();
        agent.OfferCall(Guid.NewGuid(), Nine);
        agent.AnswerCall(Nine);

        Assert.Throws<DomainException>(() => agent.SetReady(false, Nine));
    }

    [Fact]
    public void A_call_cannot_be_offered_to_an_agent_who_is_not_available()
    {
        var agent = NewAgent();
        agent.SignIn(Nine);

        Assert.Throws<DomainException>(() => agent.OfferCall(Guid.NewGuid(), Nine));
    }

    [Fact]
    public void Finishing_wrap_up_frees_the_agent_and_puts_them_back_in_the_queue()
    {
        var agent = ReadyAgent();
        agent.OfferCall(Guid.NewGuid(), Nine);
        agent.AnswerCall(Nine);
        agent.EndCall(Nine);

        agent.FinishWrapUp(Nine);

        Assert.Equal(AgentState.Available, agent.State);
        Assert.Null(agent.CurrentCallId);
    }

    [Fact]
    public void Setting_the_same_state_again_does_not_restart_the_clock()
    {
        var agent = ReadyAgent();

        agent.SetReady(true, Nine.AddMinutes(12));

        Assert.Equal(Nine, agent.StateChangedAt);
    }

    // ---------------------------------------------------------------- calls

    [Fact]
    public void A_call_cannot_be_answered_before_it_is_offered()
    {
        var call = Call.ArriveFromCustomer("+15551234567", "+18005550100", Nine);

        var error = Assert.Throws<DomainException>(() => call.Answer(Nine));

        Assert.Contains("Queued", error.Message);
    }

    [Fact]
    public void Answering_records_how_long_the_customer_waited()
    {
        var call = Call.ArriveFromCustomer("+15551234567", "+18005550100", Nine);
        call.OfferTo(ReadyAgent(), Nine);

        call.Answer(Nine.AddSeconds(42));

        Assert.Equal(42, call.WaitSeconds);
    }

    [Fact]
    public void A_call_cannot_be_filed_without_saying_how_it_ended()
    {
        var call = ConnectedCall(out _);
        call.End(Nine.AddMinutes(3));

        Assert.Throws<DomainException>(() => call.File("   ", "notes"));
    }

    [Fact]
    public void Filing_a_call_records_the_disposition_and_the_talk_time()
    {
        var call = ConnectedCall(out _);
        call.End(Nine.AddMinutes(3));

        call.File("Resolved", "  Reset the password.  ");

        Assert.Equal(CallStatus.Completed, call.Status);
        Assert.Equal("Resolved", call.Disposition);
        Assert.Equal("Reset the password.", call.Notes);
        Assert.Equal(180, call.TalkSeconds);
    }

    [Fact]
    public void Only_a_waiting_caller_can_give_up()
    {
        var call = ConnectedCall(out _);

        Assert.Throws<DomainException>(() => call.Abandon(Nine));
    }

    [Fact]
    public void A_call_knows_which_agent_it_was_given_to()
    {
        var call = ConnectedCall(out var agent);

        Assert.True(call.BelongsTo(agent.Id));
        Assert.False(call.BelongsTo(Guid.NewGuid()));
    }

    [Fact]
    public void Returning_a_call_to_the_queue_forgets_the_agent_it_was_offered_to()
    {
        var agent = ReadyAgent();
        var call = Call.ArriveFromCustomer("+15551234567", "+18005550100", Nine);
        call.OfferTo(agent, Nine);

        call.ReturnToQueue();

        Assert.Equal(CallStatus.Queued, call.Status);
        Assert.Null(call.AgentId);
        Assert.Null(call.AgentName);
    }

    // ---------------------------------------------------------------- helpers

    private static Agent NewAgent(string name = "Amina") => Agent.Create(name, "1001", false, Nine);

    private static Agent ReadyAgent(string name = "Amina")
    {
        var agent = NewAgent(name);
        agent.SignIn(Nine);
        agent.SetReady(true, Nine);
        return agent;
    }

    private static Call ConnectedCall(out Agent agent)
    {
        agent = ReadyAgent();
        var call = Call.ArriveFromCustomer("+15551234567", "+18005550100", Nine);
        call.OfferTo(agent, Nine);
        call.Answer(Nine);
        agent.AnswerCall(Nine);
        return call;
    }
}
