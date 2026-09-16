namespace CallCenter.Api.Services;

/// <summary>
/// The call centre, as the controllers see it: every command they can issue and the picture they
/// can read. The controllers depend on this rather than on the implementation, so the HTTP layer
/// knows nothing about SQL Server, locking, or how a call is routed.
///
/// Every command returns the new <see cref="Snapshot"/>, so a caller always gets the resulting
/// state back rather than having to ask for it again.
/// </summary>
public interface ICallCenterService
{
    /// <summary>The current picture: every agent, every live call, and the headline numbers.</summary>
    Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes a seat. The agent starts not-ready.</summary>
    Task<Snapshot> SignInAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>Gives up the seat. Refused while the agent still has a call in hand.</summary>
    Task<Snapshot> SignOutAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>Starts or stops taking calls — the only state change an agent may ask for.</summary>
    Task<Snapshot> SetReadyAsync(Guid agentId, bool ready, CancellationToken cancellationToken = default);

    /// <summary>A customer calls in. Stands in for the carrier's webhook.</summary>
    Task<Snapshot> ReceiveInboundCallAsync(string from, string to, CancellationToken cancellationToken = default);

    /// <summary>The agent picked up. Refused unless the call is still ringing on their desk.</summary>
    Task<Snapshot> AnswerAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default);

    /// <summary>The agent did not pick up: the call is re-queued and they are made not-ready.</summary>
    Task<Snapshot> DeclineAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default);

    /// <summary>Ends the conversation and moves the agent into wrap-up.</summary>
    Task<Snapshot> HangUpAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default);

    /// <summary>Files the call and returns the agent to the queue. Refused without a disposition.</summary>
    Task<Snapshot> CompleteWrapUpAsync(
        Guid agentId, Guid callId, string disposition, string? notes, CancellationToken cancellationToken = default);

    /// <summary>The waiting caller gave up before anyone answered.</summary>
    Task<Snapshot> AbandonAsync(Guid callId, CancellationToken cancellationToken = default);
}
