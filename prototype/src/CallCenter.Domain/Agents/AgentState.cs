namespace CallCenter.Domain.Agents;

/// <summary>
/// Where an agent is in their working day. The server owns this value: a client may ask to become
/// ready or not-ready, but Ringing, OnCall and WrapUp are set by the platform as calls move.
/// </summary>
public enum AgentState
{
    /// <summary>Not signed in.</summary>
    Offline = 0,

    /// <summary>Signed in and waiting for the next call.</summary>
    Available = 1,

    /// <summary>Signed in but not taking calls.</summary>
    NotReady = 2,

    /// <summary>A call has been offered and their phone is ringing.</summary>
    Ringing = 3,

    /// <summary>Talking to a customer.</summary>
    OnCall = 4,

    /// <summary>The call has ended; they are filing it.</summary>
    WrapUp = 5
}
