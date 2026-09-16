using System.ComponentModel.DataAnnotations;
using CallCenter.Domain;

namespace CallCenter.Api.Contracts;

// ============================================================ requests
//
// With [ApiController], these are validated before the action runs — a missing reservation
// token or an empty disposition is rejected at the edge with RFC-7807 problem details rather
// than being re-checked by hand in every method.

public sealed record LoginRequest
{
    [Required] public Guid AgentId { get; init; }

    /// <summary>Agent | Supervisor | Admin. Becomes the role claim on the session.</summary>
    public string? Role { get; init; }
}

public sealed record StateChangeRequest
{
    [Required, MinLength(1)] public string State { get; init; } = "";
    public string? Reason { get; init; }
}

public sealed record ReservationRequest
{
    /// <summary>
    /// Issued with the offer and validated server-side before any telephony command is sent.
    /// This is what makes "an agent cannot answer a call they were not offered" a rule rather
    /// than a UI convention (NFR-SEC3).
    /// </summary>
    [Required, MinLength(1)] public string ReservationToken { get; init; } = "";
}

public sealed record HoldRequest
{
    public bool Hold { get; init; }
}

public sealed record DtmfRequest
{
    [Required, MinLength(1), MaxLength(32)] public string Digits { get; init; } = "";
}

public sealed record RecordingRequest
{
    public bool Paused { get; init; }
}

public sealed record TransferRequest
{
    public string? ToNumber { get; init; }
    public Guid? ToAgentId { get; init; }
}

public sealed record DispositionRequest
{
    [Required, MinLength(1)] public string Code { get; init; } = "";
    [MaxLength(2000)] public string? Notes { get; init; }
}

public sealed record OutboundCallRequest
{
    [Required, MinLength(3)] public string To { get; init; } = "";
}

public sealed record SimulateInboundRequest
{
    [Required] public string Did { get; init; } = "";
    [Required] public string From { get; init; } = "";
    [Range(1, 3600)] public int? PatienceSeconds { get; init; }
}

public sealed record SuppressionRequest
{
    [Required, MinLength(3)] public string Number { get; init; } = "";
    public string? Source { get; init; }
}

public sealed record CrmOutageRequest
{
    public bool Enabled { get; init; }
}

// ============================================================ responses

/// <summary>Uniform error body. The clients read <c>error</c>; Swagger documents the shape.</summary>
public sealed record ApiError(string Error);

public sealed record SessionResponse(string Token, string Role, AgentProfile Agent);

public sealed record AgentProfile(
    Guid Id,
    string Name,
    string Extension,
    string Team,
    IReadOnlyDictionary<string, int> Skills,
    string State)
{
    public static AgentProfile Project(Agent agent) =>
        new(agent.Id, agent.DisplayName, agent.Extension, agent.TeamName, agent.Skills, agent.State.ToString());
}

public sealed record QueueSummary(
    Guid Id,
    string Name,
    string DisplayName,
    IReadOnlyList<string> RequiredSkills,
    int BasePriority,
    int RingTimeoutSeconds,
    int SlaThresholdSeconds,
    string SelectionPolicy)
{
    public static QueueSummary Project(QueueDefinition q) =>
        new(q.Id, q.Name, q.DisplayName, q.RequiredSkills, q.BasePriority,
            q.RingTimeoutSeconds, q.SlaThresholdSeconds, q.SelectionPolicy);
}

public sealed record BootstrapResponse(
    string Provider,
    Guid SupervisorAgentId,
    IReadOnlyList<AgentProfile> Agents,
    IReadOnlyList<QueueSummary> Queues,
    IReadOnlyDictionary<string, string> Dids,
    IReadOnlyList<string> Dispositions,
    IReadOnlyList<string> NotReadyReasons);

public sealed record AgentStateResponse(string State, string? Reason);

/// <remarks>
/// <c>ReservationToken</c> is the agent's own live reservation, returned so a reconnecting client
/// can reattach to an in-flight offer without it being re-issued (docs §4.3.3).
/// </remarks>
public sealed record AgentDetailResponse(
    Guid Id,
    string Name,
    string State,
    string? Reason,
    int CallsHandled,
    int ConsecutiveRna,
    string? ReservationToken,
    DateTimeOffset? ReservationExpiresAt,
    CallView? CurrentCall);

/// <summary>
/// The call projection every client reads. A named type rather than an anonymous object so the
/// shape is documented in Swagger and cannot drift between endpoints.
/// </summary>
public sealed record CallView(
    Guid Id,
    string Direction,
    string Status,
    string From,
    string To,
    string? Queue,
    int Priority,
    string? Agent,
    string? Contact,
    int WaitSeconds,
    int TalkSeconds,
    int RnaCount,
    bool OnHold,
    bool Recording,
    bool RecordingPaused,
    string? Disposition,
    DateTimeOffset InitiatedAt,
    DateTimeOffset? EndedAt,
    string? EndReason)
{
    public static CallView Project(Call call) =>
        new(call.Id,
            call.Direction.ToString(),
            call.Status.ToString(),
            call.FromNumber,
            call.ToNumber,
            call.QueueName,
            call.Priority,
            call.AgentName,
            call.ContactDisplayName,
            call.WaitSeconds,
            call.TalkSeconds,
            call.RnaCount,
            call.OnHold,
            call.RecordingActive,
            call.RecordingPaused,
            call.Disposition,
            call.InitiatedAt,
            call.EndedAt,
            call.EndReason);
}

public sealed record CallEventView(long Seq, string Type, DateTimeOffset OccurredAt, string CorrelationId, string Payload);

public sealed record OutboundCallResponse(Guid CallId, string To, string Status, object? Contact);

public sealed record HoldResponse(bool OnHold);

public sealed record RecordingResponse(bool RecordingPaused);

public sealed record AcceptedCallResponse(Guid CallId);

public sealed record CrmOutageResponse(bool CrmFailing, string Note);

public sealed record SuppressionAddedResponse(string Number);
