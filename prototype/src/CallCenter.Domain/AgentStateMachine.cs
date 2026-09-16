namespace CallCenter.Domain;

public readonly record struct TransitionResult(bool Allowed, string? Error = null)
{
    public static readonly TransitionResult Ok = new(true);
    public static TransitionResult Denied(string reason) => new(false, reason);
}

/// <summary>
/// The agent state machine (docs/04-system-design.md §4.3.3).
///
/// Every transition is validated here, server-side. This is deliberately a pure, dependency-free
/// class: it is the single highest-value thing to unit-test in the whole platform, because an
/// invalid transition means either a call offered to someone who cannot take it, or an agent
/// silently removed from routing.
/// </summary>
public static class AgentStateMachine
{
    private static readonly Dictionary<AgentState, AgentState[]> Allowed = new()
    {
        [AgentState.LoggedOut]      = [AgentState.Available, AgentState.NotReady],
        [AgentState.Available]      = [AgentState.Reserved, AgentState.NotReady, AgentState.LoggedOut, AgentState.OnCall],
        [AgentState.Reserved]       = [AgentState.Ringing, AgentState.Available, AgentState.NotReady, AgentState.LoggedOut],
        [AgentState.Ringing]        = [AgentState.OnCall, AgentState.Available, AgentState.NotReady, AgentState.LoggedOut],
        [AgentState.OnCall]         = [AgentState.AfterCallWork, AgentState.LoggedOut],
        [AgentState.AfterCallWork]  = [AgentState.Available, AgentState.NotReady, AgentState.LoggedOut],
        [AgentState.NotReady]       = [AgentState.Available, AgentState.LoggedOut]
    };

    /// <summary>States an agent may ask for themselves. Everything else is platform-driven.</summary>
    private static readonly HashSet<AgentState> AgentRequestable =
        [AgentState.Available, AgentState.NotReady, AgentState.LoggedOut];

    public static bool CanTransition(AgentState from, AgentState to) =>
        from != to && Allowed.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>
    /// Validate and apply. <paramref name="agentInitiated"/> distinguishes an agent clicking a
    /// button from the platform driving the transition — an agent cannot put themselves OnCall.
    /// </summary>
    public static TransitionResult Transition(
        Agent agent,
        AgentState to,
        DateTimeOffset now,
        string? reason = null,
        bool agentInitiated = false)
    {
        if (agent.State == to)
            return TransitionResult.Denied($"Agent is already {to}.");

        if (agentInitiated && !AgentRequestable.Contains(to))
            return TransitionResult.Denied($"Agents may not put themselves into {to}.");

        // An agent asking to go Available or NotReady while on a call is a common client bug
        // (and a common way to strand a live customer). Reject it explicitly.
        if (agentInitiated && agent.State is AgentState.OnCall or AgentState.Ringing && to != AgentState.LoggedOut)
            return TransitionResult.Denied("Finish or release the current call first.");

        if (!CanTransition(agent.State, to))
            return TransitionResult.Denied($"Illegal transition {agent.State} → {to}.");

        if (to == AgentState.NotReady && string.IsNullOrWhiteSpace(reason))
            return TransitionResult.Denied("A reason code is required for Not Ready.");

        agent.ApplyState(to, reason, now);
        return TransitionResult.Ok;
    }

    /// <summary>
    /// Forced transition used by supervisors and by platform recovery paths (heartbeat loss,
    /// reservation sweeper). Bypasses the legality table on purpose — but still goes through one
    /// function so that every forced change is auditable in exactly one place.
    /// </summary>
    public static void Force(Agent agent, AgentState to, DateTimeOffset now, string? reason = null)
        => agent.ApplyState(to, reason, now);
}
