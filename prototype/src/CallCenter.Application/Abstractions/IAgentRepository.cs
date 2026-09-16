using CallCenter.Domain.Agents;

namespace CallCenter.Application.Abstractions;

/// <summary>
/// How the application layer reaches agents. The queries it needs are named for what they are
/// for, so the ordering rule that matters — the agent free longest goes next — is stated here
/// rather than hidden in a LINQ expression somewhere.
/// </summary>
public interface IAgentRepository
{
    Task<Agent?> FindAsync(Guid agentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Agent>> ListAllAsync(CancellationToken cancellationToken);

    /// <summary>Available agents, the one waiting longest first.</summary>
    Task<IReadOnlyList<Agent>> ListAvailableLongestWaitingFirstAsync(CancellationToken cancellationToken);

    void Add(Agent agent);
}
