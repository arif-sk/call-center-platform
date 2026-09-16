namespace CallCenter.Domain.Calls;

/// <summary>The life of a call, from arriving to being filed.</summary>
public enum CallStatus
{
    /// <summary>Waiting for somebody to become free.</summary>
    Queued = 0,

    /// <summary>Offered to an agent, whose phone is ringing.</summary>
    Ringing = 1,

    /// <summary>The agent picked up.</summary>
    Connected = 2,

    /// <summary>The conversation is over; the agent is filing it.</summary>
    WrapUp = 3,

    /// <summary>Filed, with a disposition.</summary>
    Completed = 4,

    /// <summary>The caller gave up before anybody answered.</summary>
    Abandoned = 5
}
