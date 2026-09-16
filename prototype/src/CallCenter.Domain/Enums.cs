namespace CallCenter.Domain;

/// <summary>
/// Server-authoritative agent state. See docs/04-system-design.md §4.3.3.
/// The client may *request* a transition; only the server decides.
/// </summary>
public enum AgentState
{
    LoggedOut = 0,
    Available = 1,
    /// <summary>A call has been reserved for this agent but the phone has not rung yet.</summary>
    Reserved = 2,
    Ringing = 3,
    OnCall = 4,
    AfterCallWork = 5,
    NotReady = 6
}

public enum CallDirection
{
    Inbound = 0,
    Outbound = 1
}

public enum CallStatus
{
    Initiated = 0,
    Queued = 1,
    Offered = 2,
    Ringing = 3,
    Connected = 4,
    Wrapping = 5,
    Completed = 6,
    Abandoned = 7,
    Failed = 8,
    Blocked = 9
}

/// <summary>
/// Reason codes are configurable in the real product (FR-B2); fixed here for the prototype.
/// </summary>
public static class NotReadyReasons
{
    public const string Break = "Break";
    public const string Lunch = "Lunch";
    public const string Training = "Training";
    public const string Meeting = "Meeting";
    public const string Admin = "Admin";

    /// <summary>Set automatically by the platform when an agent fails to answer (FR-C7).</summary>
    public const string RingNoAnswer = "RNA";

    /// <summary>Set automatically when heartbeats stop arriving (FR-B4).</summary>
    public const string ConnectionLost = "ConnectionLost";

    public static readonly string[] AgentSelectable = [Break, Lunch, Training, Meeting, Admin];
}

public static class CallEndReasons
{
    public const string CallerHangup = "CallerHangup";
    public const string AgentHangup = "AgentHangup";
    public const string Abandoned = "Abandoned";
    public const string Transferred = "Transferred";
    public const string ProviderFailure = "ProviderFailure";
    public const string NoAgentsAvailable = "NoAgentsAvailable";
}
