using System.Collections.Concurrent;

namespace CallCenter.Telephony;

public sealed class SimulationOptions
{
    /// <summary>How long a simulated caller waits in queue before hanging up.</summary>
    public int CallerPatienceSeconds { get; set; } = 90;

    /// <summary>Probability an outbound call is answered by a human.</summary>
    public double OutboundAnswerProbability { get; set; } = 0.75;

    /// <summary>Seconds before an outbound call is answered (or gives up).</summary>
    public int OutboundRingSeconds { get; set; } = 4;

    /// <summary>Simulated round-trip latency to the "carrier" control API.</summary>
    public int ProviderLatencyMs { get; set; } = 40;
}

/// <summary>
/// The second implementation of the port — and the reason capacity testing, CI and local
/// development cost nothing (docs/03-mvp-definition.md §3.5, docs/07-deployment-strategy.md §7.1).
///
/// It is not a stub. It models the things that actually break routing logic: callers who abandon
/// while queued, outbound calls nobody answers, and provider latency. Those are precisely the
/// scenarios that are almost impossible to reproduce on demand with a real carrier.
/// </summary>
public sealed class SimulatedTelephonyProvider : ITelephonyProvider, IDisposable
{
    private enum SimState { Queued, RingingAgent, Connected, Ended, OutboundRinging }

    private sealed class SimCall
    {
        public Guid CallId { get; init; }
        public SimState State { get; set; }
        public DateTimeOffset StateSince { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public int PatienceSeconds { get; init; }
        public bool WillAnswer { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, SimCall> _calls = new();
    private readonly SimulationOptions _options;
    private readonly Random _random;
    private readonly Timer _timer;
    private int _sequence;

    public string Name => "simulated";

    public event Action<TelephonyEvent>? EventReceived;

    public SimulatedTelephonyProvider(SimulationOptions? options = null, int? seed = null)
    {
        _options = options ?? new SimulationOptions();
        _random = seed is null ? new Random() : new Random(seed.Value);
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    /// <summary>Test hook: drive the clock deterministically instead of waiting for the timer.</summary>
    public void Tick() => Tick(DateTimeOffset.UtcNow);

    public void Tick(DateTimeOffset now)
    {
        foreach (var call in _calls.Values.ToArray())
        {
            switch (call.State)
            {
                // The caller gives up while waiting in queue (FR-C8).
                case SimState.Queued when (now - call.CreatedAt).TotalSeconds >= call.PatienceSeconds:
                    call.State = SimState.Ended;
                    Raise(TelephonyEvent.Create(TelephonyEventTypes.CallerAbandoned, call.CallId,
                        ("waitedSeconds", ((int)(now - call.CreatedAt).TotalSeconds).ToString())));
                    break;

                // An outbound attempt resolves into answered or no-answer.
                case SimState.OutboundRinging when (now - call.StateSince).TotalSeconds >= _options.OutboundRingSeconds:
                    if (call.WillAnswer)
                    {
                        call.State = SimState.Connected;
                        call.StateSince = now;
                        Raise(TelephonyEvent.Create(TelephonyEventTypes.CallAnswered, call.CallId,
                            ("answeredBy", "customer")));
                    }
                    else
                    {
                        call.State = SimState.Ended;
                        Raise(TelephonyEvent.Create(TelephonyEventTypes.OutboundNoAnswer, call.CallId));
                    }
                    break;
            }
        }
    }

    /// <summary>Entry point used by the demo console to inject an inbound call.</summary>
    public Task<CallHandle> SimulateInboundAsync(Guid callId, string from, string to, int? patienceSeconds = null)
    {
        var call = new SimCall
        {
            CallId = callId,
            From = from,
            To = to,
            State = SimState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            StateSince = DateTimeOffset.UtcNow,
            PatienceSeconds = patienceSeconds ?? _options.CallerPatienceSeconds
        };
        _calls[callId] = call;

        Raise(TelephonyEvent.Create(TelephonyEventTypes.InboundCallReceived, callId,
            ("from", from), ("to", to)));

        return Task.FromResult(new CallHandle($"sim-{Interlocked.Increment(ref _sequence):D6}", Name));
    }

    /// <summary>The simulated caller hangs up while connected or queued.</summary>
    public void SimulateCallerHangup(Guid callId)
    {
        if (!_calls.TryGetValue(callId, out var call) || call.State == SimState.Ended) return;
        call.State = SimState.Ended;
        Raise(TelephonyEvent.Create(TelephonyEventTypes.CallEnded, callId, ("reason", "CallerHangup")));
    }

    public async Task<CallHandle> PlaceCallAsync(PlaceCallRequest request, CancellationToken ct = default)
    {
        await SimulateLatency(ct);

        var call = new SimCall
        {
            CallId = request.CallId,
            From = request.From,
            To = request.To,
            State = SimState.OutboundRinging,
            CreatedAt = DateTimeOffset.UtcNow,
            StateSince = DateTimeOffset.UtcNow,
            PatienceSeconds = int.MaxValue,
            WillAnswer = _random.NextDouble() < _options.OutboundAnswerProbability
        };
        _calls[request.CallId] = call;

        Raise(TelephonyEvent.Create(TelephonyEventTypes.OutboundRinging, request.CallId,
            ("from", request.From), ("to", request.To)));

        return new CallHandle($"sim-{Interlocked.Increment(ref _sequence):D6}", Name);
    }

    public async Task<CallHandle> RingAgentAsync(Guid callId, Guid agentId, TimeSpan ringTimeout, CancellationToken ct = default)
    {
        await SimulateLatency(ct);

        if (_calls.TryGetValue(callId, out var call))
        {
            call.State = SimState.RingingAgent;
            call.StateSince = DateTimeOffset.UtcNow;
        }

        Raise(TelephonyEvent.Create(TelephonyEventTypes.AgentRinging, callId,
            ("agentId", agentId.ToString()),
            ("ringTimeoutSeconds", ((int)ringTimeout.TotalSeconds).ToString())));

        return new CallHandle($"sim-{Interlocked.Increment(ref _sequence):D6}", Name);
    }

    public async Task AnswerAsync(Guid callId, CancellationToken ct = default)
    {
        await SimulateLatency(ct);
        if (_calls.TryGetValue(callId, out var call))
        {
            call.State = SimState.Connected;
            call.StateSince = DateTimeOffset.UtcNow;
        }
        Raise(TelephonyEvent.Create(TelephonyEventTypes.CallAnswered, callId, ("answeredBy", "agent")));
    }

    public async Task HangupAsync(Guid callId, string reason, CancellationToken ct = default)
    {
        await SimulateLatency(ct);
        if (_calls.TryRemove(callId, out var call)) call.State = SimState.Ended;
        Raise(TelephonyEvent.Create(TelephonyEventTypes.CallEnded, callId, ("reason", reason)));
    }

    public Task HoldAsync(Guid callId, bool hold, CancellationToken ct = default) => SimulateLatency(ct);

    public Task SendDtmfAsync(Guid callId, string digits, CancellationToken ct = default) => SimulateLatency(ct);

    public async Task SetRecordingAsync(Guid callId, RecordingCommand command, CancellationToken ct = default)
    {
        await SimulateLatency(ct);
        Raise(TelephonyEvent.Create(TelephonyEventTypes.RecordingStateChanged, callId,
            ("command", command.ToString())));
    }

    public Task TransferAsync(Guid callId, TransferTarget target, TransferMode mode, CancellationToken ct = default)
        => SimulateLatency(ct);

    /// <summary>No signature to verify — the simulator is in-process and never exposed.</summary>
    public bool VerifyWebhook(string url, IReadOnlyDictionary<string, string> form, string signature) => true;

    private Task SimulateLatency(CancellationToken ct) =>
        _options.ProviderLatencyMs <= 0 ? Task.CompletedTask : Task.Delay(_options.ProviderLatencyMs, ct);

    private void Raise(TelephonyEvent e) => EventReceived?.Invoke(e);

    public void Dispose() => _timer.Dispose();
}
