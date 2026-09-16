using CallCenter.Domain.Agents;

namespace CallCenter.Domain.Calls;

/// <summary>
/// One call, from arriving to being filed.
///
/// Durations are worked out and stored as the call moves, not recalculated when somebody opens a
/// report. Reports are read far more often than calls are made.
/// </summary>
public class Call
{
    /// <summary>For the persistence layer only.</summary>
    private Call()
    {
    }

    /// <summary>A customer has called in and is waiting for somebody to become free.</summary>
    public static Call ArriveFromCustomer(string fromNumber, string toNumber, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            FromNumber = string.IsNullOrWhiteSpace(fromNumber) ? "Unknown" : fromNumber.Trim(),
            ToNumber = string.IsNullOrWhiteSpace(toNumber) ? "Unknown" : toNumber.Trim(),
            Status = CallStatus.Queued,
            QueuedAt = now
        };

    public Guid Id { get; private set; }

    public string FromNumber { get; private set; } = string.Empty;

    public string ToNumber { get; private set; } = string.Empty;

    public CallStatus Status { get; private set; }

    public DateTimeOffset QueuedAt { get; private set; }

    public DateTimeOffset? AnsweredAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public Guid? AgentId { get; private set; }

    /// <summary>Copied at the time of the call, so a later rename does not rewrite history.</summary>
    public string? AgentName { get; private set; }

    public string? Disposition { get; private set; }

    public string? Notes { get; private set; }

    public int WaitSeconds { get; private set; }

    public int TalkSeconds { get; private set; }

    public bool IsWaiting => Status == CallStatus.Queued;

    /// <summary>
    /// An agent may only act on the call the platform gave them. Without this, anybody who can
    /// guess a call's id can hang up on somebody else's customer.
    /// </summary>
    public bool BelongsTo(Guid agentId) => AgentId == agentId;

    public void OfferTo(Agent agent, DateTimeOffset now)
    {
        RequireStatus(CallStatus.Queued, "be offered to an agent");

        agent.OfferCall(Id, now);

        AgentId = agent.Id;
        AgentName = agent.Name;
        Status = CallStatus.Ringing;
    }

    public void Answer(DateTimeOffset now)
    {
        RequireStatus(CallStatus.Ringing, "be answered");

        Status = CallStatus.Connected;
        AnsweredAt = now;
        WaitSeconds = SecondsBetween(QueuedAt, now);
    }

    /// <summary>Nobody picked up. The call goes back to the front of the queue.</summary>
    public void ReturnToQueue()
    {
        RequireStatus(CallStatus.Ringing, "be returned to the queue");

        Status = CallStatus.Queued;
        AgentId = null;
        AgentName = null;
    }

    public void End(DateTimeOffset now)
    {
        RequireStatus(CallStatus.Connected, "be ended");

        Status = CallStatus.WrapUp;
        EndedAt = now;
        TalkSeconds = SecondsBetween(AnsweredAt ?? now, now);
    }

    /// <summary>Wrap-up is part of the call, not an afterthought: no disposition, no completion.</summary>
    public void File(string disposition, string? notes)
    {
        RequireStatus(CallStatus.WrapUp, "be filed");

        if (string.IsNullOrWhiteSpace(disposition))
        {
            throw new DomainException("Choose a disposition before finishing.");
        }

        Status = CallStatus.Completed;
        Disposition = disposition.Trim();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    /// <summary>The caller gave up while waiting — the number supervisors actually watch.</summary>
    public void Abandon(DateTimeOffset now)
    {
        RequireStatus(CallStatus.Queued, "be abandoned");

        Status = CallStatus.Abandoned;
        EndedAt = now;
        WaitSeconds = SecondsBetween(QueuedAt, now);
    }

    /// <summary>
    /// Called when the platform restarts. The agent it was with is no longer signed in, so the
    /// caller goes back into the queue rather than sitting on a desktop that no longer exists.
    /// </summary>
    public void ReturnToQueueAfterRestart()
    {
        Status = CallStatus.Queued;
        AgentId = null;
        AgentName = null;
    }

    private void RequireStatus(CallStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new DomainException($"A call that is {Status} cannot {action}.");
        }
    }

    private static int SecondsBetween(DateTimeOffset from, DateTimeOffset to) =>
        Math.Max(0, (int)(to - from).TotalSeconds);
}
