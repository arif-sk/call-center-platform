using CallCenter.Application.Contracts;

namespace CallCenter.Application.Services;

/// <summary>
/// The call centre, as the outside world sees it: every command that can be issued and the
/// picture that can be read. The API depends on this; it has no idea what implements it.
///
/// Every command returns the new <see cref="Snapshot"/>, so a caller always gets the resulting
/// state back rather than having to ask for it again.
/// </summary>
public interface ICallCenterService
{
    Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes a seat. The agent starts not-ready.</summary>
    Task<Snapshot> SignInAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>Gives up the seat. Refused while the agent still has a call in hand.</summary>
    Task<Snapshot> SignOutAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>Starts or stops taking calls — the only state change an agent may ask for.</summary>
    Task<Snapshot> SetReadyAsync(Guid agentId, bool ready, CancellationToken cancellationToken = default);

    /// <summary>A customer calls in. Stands in for the carrier's webhook.</summary>
    Task<Snapshot> ReceiveInboundCallAsync(string from, string to, CancellationToken cancellationToken = default);

    Task<Snapshot> AnswerAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default);

    /// <summary>The agent did not pick up: the call is re-queued and they are made not-ready.</summary>
    Task<Snapshot> DeclineAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default);

    Task<Snapshot> HangUpAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default);

    /// <summary>Files the call and returns the agent to the queue.</summary>
    Task<Snapshot> CompleteWrapUpAsync(
        Guid agentId, Guid callId, string disposition, string? notes, CancellationToken cancellationToken = default);

    /// <summary>The waiting caller gave up before anyone answered.</summary>
    Task<Snapshot> AbandonAsync(Guid callId, CancellationToken cancellationToken = default);
}
