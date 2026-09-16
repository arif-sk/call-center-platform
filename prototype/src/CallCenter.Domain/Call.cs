namespace CallCenter.Domain;

/// <summary>
/// A reservation is the correctness core of the routing engine (docs/04-system-design.md §4.3.2).
///
/// The engine offers a call to exactly one agent and issues a token. The agent's Answer request
/// must present that token; the server validates it before any telephony command is sent. This is
/// what makes "offered to exactly one agent, exactly once" true rather than aspirational.
/// </summary>
public sealed record Reservation(
    Guid CallId,
    Guid AgentId,
    string Token,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public static Reservation Issue(Guid callId, Guid agentId, DateTimeOffset now, TimeSpan ttl) =>
        new(callId, agentId, Guid.NewGuid().ToString("N"), now, now + ttl);
}

/// <summary>
/// The interaction aggregate. Named <c>Call</c> for readability in the prototype, but everything
/// routing touches goes through the channel-agnostic surface (queue, priority, required skills) so
/// that adding chat/email later is additive — see docs/01-requirement-analysis.md §1.10.
/// </summary>
public sealed class Call
{
    public required Guid Id { get; init; }
    public required CallDirection Direction { get; init; }
    public required string FromNumber { get; init; }
    public required string ToNumber { get; init; }

    public Guid? QueueId { get; private set; }
    public string? QueueName { get; private set; }
    public int Priority { get; private set; }
    public IReadOnlyList<string> RequiredSkills { get; private set; } = [];

    public CallStatus Status { get; private set; } = CallStatus.Initiated;

    public DateTimeOffset InitiatedAt { get; init; }
    public DateTimeOffset? QueuedAt { get; private set; }
    public DateTimeOffset? OfferedAt { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public Guid? AgentId { get; private set; }
    public string? AgentName { get; private set; }
    public string? EndReason { get; private set; }
    public string? Disposition { get; private set; }
    public string? Notes { get; private set; }
    public bool OnHold { get; private set; }
    public bool RecordingActive { get; private set; }
    public bool RecordingPaused { get; private set; }

    /// <summary>CRM reference only — we never copy customer PII (docs/04-system-design.md §4.7.2).</summary>
    public string? ExternalContactId { get; private set; }
    public string? ContactDisplayName { get; private set; }

    public string? ProviderCallId { get; private set; }
    public string ProviderName { get; private set; } = "unknown";

    /// <summary>Agents this call has already been offered to — prevents an RNA ping-pong loop.</summary>
    public HashSet<Guid> OfferedTo { get; } = [];

    public int RnaCount { get; private set; }

    public int WaitSeconds => QueuedAt is null
        ? 0
        : (int)((AnsweredAt ?? EndedAt ?? DateTimeOffset.UtcNow) - QueuedAt.Value).TotalSeconds;

    public int TalkSeconds => AnsweredAt is null
        ? 0
        : (int)((EndedAt ?? DateTimeOffset.UtcNow) - AnsweredAt.Value).TotalSeconds;

    public bool IsLive => Status is CallStatus.Queued or CallStatus.Offered
        or CallStatus.Ringing or CallStatus.Connected;

    // ---- lifecycle ----

    public void Enqueue(Guid queueId, string queueName, int priority, IReadOnlyList<string> skills, DateTimeOffset now)
    {
        QueueId = queueId;
        QueueName = queueName;
        Priority = priority;
        RequiredSkills = skills;
        QueuedAt ??= now;
        OfferedAt = null;
        AgentId = null;
        AgentName = null;
        Status = CallStatus.Queued;
    }

    public void Offer(Agent agent, DateTimeOffset now)
    {
        AgentId = agent.Id;
        AgentName = agent.DisplayName;
        OfferedAt = now;
        OfferedTo.Add(agent.Id);
        Status = CallStatus.Offered;
    }

    public void Ring() => Status = CallStatus.Ringing;

    public void Answer(DateTimeOffset now)
    {
        AnsweredAt = now;
        Status = CallStatus.Connected;
        OnHold = false;
    }

    public void RecordRna()
    {
        RnaCount++;
        AgentId = null;
        AgentName = null;
        OfferedAt = null;
        Status = CallStatus.Queued;
    }

    public void End(string reason, DateTimeOffset now)
    {
        EndedAt = now;
        EndReason = reason;
        OnHold = false;
        RecordingActive = false;
        Status = reason == CallEndReasons.Abandoned
            ? CallStatus.Abandoned
            : AnsweredAt is null ? CallStatus.Abandoned : CallStatus.Wrapping;
    }

    public void Complete(string disposition, string? notes)
    {
        Disposition = disposition;
        Notes = notes;
        Status = CallStatus.Completed;
    }

    public void Block(string reason, DateTimeOffset now)
    {
        Status = CallStatus.Blocked;
        EndReason = reason;
        EndedAt = now;
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        Status = CallStatus.Failed;
        EndReason = reason;
        EndedAt = now;
    }

    public void SetHold(bool hold) => OnHold = hold;
    public void SetRecording(bool active, bool paused) { RecordingActive = active; RecordingPaused = paused; }
    public void AttachProvider(string name, string providerCallId) { ProviderName = name; ProviderCallId = providerCallId; }
    public void AttachContact(string? externalId, string? displayName)
    {
        ExternalContactId = externalId;
        ContactDisplayName = displayName;
    }

    public void AttachAgentDirect(Agent agent, DateTimeOffset now)
    {
        AgentId = agent.Id;
        AgentName = agent.DisplayName;
        OfferedAt = now;
        Status = CallStatus.Ringing;
    }
}

/// <summary>
/// An append-only interaction event. This is the system of record (FR-I1): CDR fields, reports and
/// every future AI feature are derived from it. See docs/06-ai-readiness.md §6.2.
/// </summary>
public sealed record CallEvent(
    long Seq,
    Guid CallId,
    string Type,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    IReadOnlyDictionary<string, string?> Payload);
