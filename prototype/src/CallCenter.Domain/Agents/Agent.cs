namespace CallCenter.Domain.Agents;

/// <summary>
/// A person taking calls, and the one state they are in right now.
///
/// Every state change goes through a method on this class, and every method refuses a transition
/// that does not make sense. That is the point of putting the rules here rather than in a service:
/// there is no way to reach an <see cref="Agent"/> and simply assign <see cref="State"/>, so the
/// state machine cannot be bypassed by a new caller written next year.
/// </summary>
public class Agent
{
    /// <summary>For the persistence layer only.</summary>
    private Agent()
    {
    }

    public static Agent Create(string name, string extension, bool isSupervisor, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("An agent needs a name.");
        }

        return new Agent
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Extension = extension.Trim(),
            IsSupervisor = isSupervisor,
            State = AgentState.Offline,
            StateChangedAt = now
        };
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Extension { get; private set; } = string.Empty;

    public bool IsSupervisor { get; private set; }

    public AgentState State { get; private set; }

    /// <summary>The call currently on this agent's desk, whether ringing, connected or being filed.</summary>
    public Guid? CurrentCallId { get; private set; }

    public DateTimeOffset StateChangedAt { get; private set; }

    public bool IsAvailable => State == AgentState.Available;

    /// <summary>True from the moment a call is offered until it has been filed.</summary>
    public bool IsHandlingCall => State is AgentState.Ringing or AgentState.OnCall or AgentState.WrapUp;

    /// <summary>
    /// Takes a seat. The agent starts not-ready deliberately, so signing in never puts a call
    /// through to somebody who is not at their desk yet.
    /// </summary>
    public void SignIn(DateTimeOffset now) => ChangeState(AgentState.NotReady, now);

    public void SignOut(DateTimeOffset now)
    {
        if (CurrentCallId is not null)
        {
            throw new DomainException("Finish the current call before signing out.");
        }

        ChangeState(AgentState.Offline, now);
    }

    /// <summary>The only state change an agent is allowed to ask for.</summary>
    public void SetReady(bool ready, DateTimeOffset now)
    {
        if (IsHandlingCall)
        {
            throw new DomainException($"Cannot change availability while {State}.");
        }

        ChangeState(ready ? AgentState.Available : AgentState.NotReady, now);
    }

    /// <summary>Hands a call to this agent and starts their phone ringing.</summary>
    public void OfferCall(Guid callId, DateTimeOffset now)
    {
        if (!IsAvailable)
        {
            throw new DomainException("Only an available agent can be offered a call.");
        }

        CurrentCallId = callId;
        ChangeState(AgentState.Ringing, now);
    }

    public void AnswerCall(DateTimeOffset now)
    {
        RequireState(AgentState.Ringing, "answer a call");
        ChangeState(AgentState.OnCall, now);
    }

    /// <summary>
    /// The agent let it ring out. They are made not-ready as well as released, so the platform
    /// does not immediately offer them the same call again.
    /// </summary>
    public void DeclineCall(DateTimeOffset now)
    {
        RequireState(AgentState.Ringing, "decline a call");
        CurrentCallId = null;
        ChangeState(AgentState.NotReady, now);
    }

    public void EndCall(DateTimeOffset now)
    {
        RequireState(AgentState.OnCall, "hang up");
        ChangeState(AgentState.WrapUp, now);
    }

    /// <summary>The call is filed; the agent goes straight back into the queue for the next one.</summary>
    public void FinishWrapUp(DateTimeOffset now)
    {
        RequireState(AgentState.WrapUp, "finish wrap-up");
        CurrentCallId = null;
        ChangeState(AgentState.Available, now);
    }

    /// <summary>
    /// Called when the platform restarts. Nobody is signed in any more, so whatever they were
    /// doing is no longer true.
    /// </summary>
    public void ResetForRestart()
    {
        State = AgentState.Offline;
        CurrentCallId = null;
    }

    private void RequireState(AgentState expected, string action)
    {
        if (State != expected)
        {
            throw new DomainException($"An agent who is {State} cannot {action}.");
        }
    }

    private void ChangeState(AgentState state, DateTimeOffset now)
    {
        // An unchanged state keeps its original clock, so "not ready for 12 minutes" stays true
        // when the same state is set again.
        if (State == state)
        {
            return;
        }

        State = state;
        StateChangedAt = now;
    }
}
