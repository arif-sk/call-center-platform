using CallCenter.Domain.Agents;
using CallCenter.Domain.Calls;

namespace CallCenter.Application.Contracts;

/// <summary>
/// What the screens are told, as opposed to what the domain holds. Keeping these separate is what
/// lets an entity gain a field, or a rule, without changing the shape of the API.
/// </summary>
public sealed record Snapshot(
    IReadOnlyList<AgentView> Agents,
    IReadOnlyList<CallView> Queue,
    IReadOnlyList<CallView> Recent,
    Stats Stats,
    DateTimeOffset ServerTime);

public sealed record AgentView(
    Guid Id,
    string Name,
    string Extension,
    bool IsSupervisor,
    string State,
    Guid? CurrentCallId,
    DateTimeOffset StateChangedAt)
{
    public static AgentView Project(Agent agent) => new(
        agent.Id, agent.Name, agent.Extension, agent.IsSupervisor,
        agent.State.ToString(), agent.CurrentCallId, agent.StateChangedAt);
}

public sealed record CallView(
    Guid Id,
    string From,
    string To,
    string Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? EndedAt,
    Guid? AgentId,
    string? AgentName,
    string? Disposition,
    int WaitSeconds,
    int TalkSeconds)
{
    public static CallView Project(Call call) => new(
        call.Id, call.FromNumber, call.ToNumber, call.Status.ToString(),
        call.QueuedAt, call.AnsweredAt, call.EndedAt,
        call.AgentId, call.AgentName, call.Disposition, call.WaitSeconds, call.TalkSeconds);
}

/// <summary>The headline numbers on the supervisor's console.</summary>
public sealed record Stats(int Waiting, int Available, int OnCall, int Completed, int LongestWaitSeconds);
