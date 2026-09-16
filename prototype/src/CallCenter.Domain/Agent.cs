namespace CallCenter.Domain;

/// <summary>
/// An agent's runtime aggregate. In production the *current* value of this lives in Redis
/// (sub-millisecond reads, many writes per minute) while the *journal* of state changes is
/// written to Postgres — see docs/04-system-design.md §4.7.3.
/// </summary>
public sealed class Agent
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Extension { get; init; }
    public string TeamName { get; init; } = "Default";

    /// <summary>
    /// False for supervisor/admin accounts. They authenticate and observe, but are never eligible
    /// for routing and do not belong on an agent wallboard.
    /// </summary>
    public bool TakesCalls { get; init; } = true;

    /// <summary>Skill name → proficiency 1..5 (FR-A3).</summary>
    public Dictionary<string, int> Skills { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Queues this agent serves.</summary>
    public HashSet<Guid> Queues { get; init; } = [];

    public AgentState State { get; private set; } = AgentState.LoggedOut;
    public string? NotReadyReason { get; private set; }
    public DateTimeOffset StateSince { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>When the agent last became Available — the input to the longest-idle policy.</summary>
    public DateTimeOffset AvailableSince { get; private set; } = DateTimeOffset.MaxValue;

    public Guid? CurrentCallId { get; private set; }
    public Reservation? Reservation { get; private set; }

    public int CallsHandledToday { get; private set; }
    public int ConsecutiveRna { get; private set; }
    public DateTimeOffset LastHeartbeat { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>Set when a newer session evicts this one (FR-B5).</summary>
    public string? SessionId { get; private set; }

    public bool IsRoutable => State == AgentState.Available;

    public int Proficiency(string skill) => Skills.TryGetValue(skill, out var p) ? p : 0;

    public bool HasSkills(IEnumerable<string> required) =>
        required.All(s => Skills.ContainsKey(s));

    /// <summary>Average proficiency across the skills a queue requires — used as a tiebreaker.</summary>
    public double AverageProficiency(IReadOnlyCollection<string> required) =>
        required.Count == 0 ? 0 : required.Average(Proficiency);

    // ---- state mutation (only via AgentStateMachine, which validates the transition) ----

    internal void ApplyState(AgentState next, string? reason, DateTimeOffset now)
    {
        State = next;
        NotReadyReason = next == AgentState.NotReady ? reason : null;
        StateSince = now;

        if (next == AgentState.Available)
        {
            AvailableSince = now;
            ConsecutiveRna = 0;
        }
        else
        {
            AvailableSince = DateTimeOffset.MaxValue;
        }

        if (next is AgentState.LoggedOut or AgentState.Available or AgentState.NotReady)
        {
            CurrentCallId = null;
            Reservation = null;
        }
    }

    public void AttachCall(Guid callId) => CurrentCallId = callId;

    public void SetReservation(Reservation reservation)
    {
        Reservation = reservation;
        CurrentCallId = reservation.CallId;
    }

    /// <summary>
    /// Releases the reservation only. It deliberately does NOT detach <see cref="CurrentCallId"/>:
    /// the reservation is consumed the moment the agent answers, and clearing the call there would
    /// orphan a live conversation. The RNA and log-out paths clear the call via the state machine.
    /// </summary>
    public void ClearReservation() => Reservation = null;

    public void RecordHandledCall() => CallsHandledToday++;

    public int RecordRna()
    {
        ConsecutiveRna++;
        return ConsecutiveRna;
    }

    public void Heartbeat(DateTimeOffset now) => LastHeartbeat = now;

    public void StartSession(string sessionId, DateTimeOffset now)
    {
        SessionId = sessionId;
        LastHeartbeat = now;
    }

    public void ResetDailyCounters() => CallsHandledToday = 0;
}
