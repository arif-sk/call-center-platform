namespace CallCenter.Domain.Routing;

/// <summary>
/// "Which of the eligible agents gets this call?" is business policy, not an algorithm choice
/// (stakeholder question Q19). It is a strategy so Ops can change it per queue without a release,
/// and so we can unit-test fairness — an unfair distribution is a morale and payroll problem that
/// aggregate metrics hide.
/// </summary>
public interface IAgentSelectionPolicy
{
    string Name { get; }
    Agent? Select(IReadOnlyList<Agent> eligible, QueuedCall call);
}

/// <summary>Default: the agent who has been Available the longest. Fairest, and the industry norm.</summary>
public sealed class LongestIdlePolicy : IAgentSelectionPolicy
{
    public string Name => SelectionPolicies.LongestIdle;

    public Agent? Select(IReadOnlyList<Agent> eligible, QueuedCall call) =>
        eligible
            .OrderBy(a => a.AvailableSince)
            .ThenBy(a => a.Id)                 // deterministic tiebreak — important for tests
            .FirstOrDefault();
}

/// <summary>Best skill match first, longest-idle as the tiebreaker. For technical queues.</summary>
public sealed class HighestProficiencyPolicy : IAgentSelectionPolicy
{
    public string Name => SelectionPolicies.HighestProficiency;

    public Agent? Select(IReadOnlyList<Agent> eligible, QueuedCall call)
    {
        var required = call.RequiredSkills;
        return eligible
            .OrderByDescending(a => a.AverageProficiency(required.ToArray()))
            .ThenBy(a => a.AvailableSince)
            .ThenBy(a => a.Id)
            .FirstOrDefault();
    }
}

/// <summary>Load-levelling across a shift — useful when agents work different hours.</summary>
public sealed class LeastCallsTodayPolicy : IAgentSelectionPolicy
{
    public string Name => SelectionPolicies.LeastCallsToday;

    public Agent? Select(IReadOnlyList<Agent> eligible, QueuedCall call) =>
        eligible
            .OrderBy(a => a.CallsHandledToday)
            .ThenBy(a => a.AvailableSince)
            .ThenBy(a => a.Id)
            .FirstOrDefault();
}

public static class SelectionPolicyFactory
{
    private static readonly IAgentSelectionPolicy[] All =
    [
        new LongestIdlePolicy(),
        new HighestProficiencyPolicy(),
        new LeastCallsTodayPolicy()
    ];

    public static IAgentSelectionPolicy Resolve(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
