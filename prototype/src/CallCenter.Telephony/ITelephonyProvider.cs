using CallCenter.Domain;

namespace CallCenter.Telephony;

public sealed record CallHandle(string ProviderCallId, string ProviderName);

public sealed record PlaceCallRequest(Guid CallId, string From, string To, bool Record);

public enum RecordingCommand { Start, Pause, Resume, Stop }

public enum TransferMode { Blind, Consult }

public sealed record TransferTarget(string? Number, Guid? AgentId, Guid? QueueId);

/// <summary>
/// Canonical, provider-neutral events. The whole point of this type is that nothing above the
/// telephony layer ever sees a vendor payload — see docs/04-system-design.md §4.3.1.
/// </summary>
public sealed record TelephonyEvent(
    string Type,
    Guid CallId,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string?> Data)
{
    public static TelephonyEvent Create(string type, Guid callId, params (string Key, string? Value)[] data) =>
        new(type, callId, DateTimeOffset.UtcNow,
            data.ToDictionary(d => d.Key, d => d.Value, StringComparer.OrdinalIgnoreCase));
}

public static class TelephonyEventTypes
{
    public const string InboundCallReceived = "InboundCallReceived";
    public const string CallerAbandoned     = "CallerAbandoned";
    public const string AgentRinging        = "AgentRinging";
    public const string CallAnswered        = "CallAnswered";
    public const string CallEnded           = "CallEnded";
    public const string OutboundRinging     = "OutboundRinging";
    public const string OutboundNoAnswer    = "OutboundNoAnswer";
    public const string RecordingStateChanged = "RecordingStateChanged";
}

/// <summary>
/// The port (docs/04-system-design.md §4.3.1, MVP §3.5).
///
/// This interface is the only defence against re-creating the vendor lock-in the whole programme
/// exists to escape. An abstraction with one implementation is a guess; this one has two — a
/// simulated provider used by every test and the local dev loop, and a real HTTP adapter.
/// </summary>
public interface ITelephonyProvider
{
    string Name { get; }

    /// <summary>Canonical events flowing up from the carrier.</summary>
    event Action<TelephonyEvent>? EventReceived;

    Task<CallHandle> PlaceCallAsync(PlaceCallRequest request, CancellationToken ct = default);

    /// <summary>Bridge an existing call leg to an agent's softphone — the phone starts ringing.</summary>
    Task<CallHandle> RingAgentAsync(Guid callId, Guid agentId, TimeSpan ringTimeout, CancellationToken ct = default);

    Task AnswerAsync(Guid callId, CancellationToken ct = default);
    Task HangupAsync(Guid callId, string reason, CancellationToken ct = default);
    Task HoldAsync(Guid callId, bool hold, CancellationToken ct = default);
    Task SendDtmfAsync(Guid callId, string digits, CancellationToken ct = default);
    Task SetRecordingAsync(Guid callId, RecordingCommand command, CancellationToken ct = default);
    Task TransferAsync(Guid callId, TransferTarget target, TransferMode mode, CancellationToken ct = default);

    /// <summary>
    /// Verify a carrier webhook. Returns false for a bad signature, a stale timestamp, or a replay
    /// (NFR-SEC10). Implemented for real in the Twilio adapter.
    /// </summary>
    bool VerifyWebhook(string url, IReadOnlyDictionary<string, string> form, string signature);
}
