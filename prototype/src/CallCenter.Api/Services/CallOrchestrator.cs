using System.Collections.Concurrent;
using System.Text.Json;
using CallCenter.Api.Data;
using CallCenter.Api.Realtime;
using CallCenter.Domain;
using CallCenter.Telephony;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Api.Services;

/// <summary>DID → entry point → queue (FR-C1). Ops-editable configuration in production.</summary>
public sealed class DidRoutingTable
{
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Map(string did, string queueName) => _map[did] = queueName;

    public string? Resolve(string did) => _map.TryGetValue(did, out var q) ? q : null;

    public IReadOnlyDictionary<string, string> All => _map;
}

public sealed record CommandResult(bool Success, string? Error = null, object? Data = null)
{
    public static readonly CommandResult Ok = new(true);
    public static CommandResult Fail(string error) => new(false, error);
    public static CommandResult With(object data) => new(true, null, data);
}

/// <summary>
/// Call lifecycle and agent commands. This is the layer that translates "agent clicked Answer" into
/// validated state changes, telephony commands and events — and the layer that refuses to do so
/// when the request is not legitimate.
/// </summary>
public sealed class CallOrchestrator(
    PlatformState state,
    RoutingEngine routing,
    InteractionEventStore events,
    ITelephonyProvider telephony,
    CrmClient crm,
    SuppressionService suppression,
    DidRoutingTable dids,
    IDbContextFactory<CallCenterDbContext> dbFactory,
    IHubContext<CallCenterHub> hub,
    ILogger<CallOrchestrator> logger)
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _wrapStartedAt = new();

    /// <summary>
    /// Guards call termination. A hang-up produces both a local command result and a provider event
    /// for the same call, and carriers additionally retry their callbacks — so "this call ended"
    /// must be processed exactly once or the agent is double-counted and the CDR is written twice.
    /// This is the in-process form of the idempotency key described in docs §4.3.1.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, byte> _terminated = new();

    // ============================================================ telephony events in

    /// <summary>
    /// Canonical events from the provider. Note there is no vendor vocabulary anywhere below this
    /// method — that is the whole point of the port (docs/04-system-design.md §4.3.1).
    /// </summary>
    public async Task HandleTelephonyEventAsync(TelephonyEvent e, CancellationToken ct = default)
    {
        try
        {
            switch (e.Type)
            {
                case TelephonyEventTypes.InboundCallReceived:
                    await OnInboundAsync(e, ct);
                    break;

                case TelephonyEventTypes.CallerAbandoned:
                    await OnAbandonedAsync(e, ct);
                    break;

                case TelephonyEventTypes.CallAnswered when e.Data.GetValueOrDefault("answeredBy") == "customer":
                    await OnOutboundAnsweredAsync(e, ct);
                    break;

                case TelephonyEventTypes.OutboundNoAnswer:
                    await OnOutboundNoAnswerAsync(e, ct);
                    break;

                case TelephonyEventTypes.CallEnded:
                    await OnCallEndedAsync(e.CallId, e.Data.GetValueOrDefault("reason") ?? CallEndReasons.CallerHangup, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed handling telephony event {Type} for call {CallId}", e.Type, e.CallId);
        }
    }

    private async Task OnInboundAsync(TelephonyEvent e, CancellationToken ct)
    {
        var from = e.Data.GetValueOrDefault("from") ?? "unknown";
        var to = e.Data.GetValueOrDefault("to") ?? "unknown";
        var correlationId = e.CallId.ToString("N");

        var call = new Call
        {
            Id = e.CallId,
            Direction = CallDirection.Inbound,
            FromNumber = from,
            ToNumber = to,
            InitiatedAt = e.OccurredAt
        };
        call.AttachProvider(telephony.Name, e.CallId.ToString("N"));
        state.AddCall(call);

        await events.AppendAsync(call.Id, "CallInitiated", correlationId, new { from, to, direction = "Inbound" }, ct);

        var queueName = dids.Resolve(to);
        var queue = queueName is null ? null : state.GetQueueByName(queueName);

        if (queue is null)
        {
            // Degraded path: an unmapped DID must get a treatment, never silence (FR-I3).
            logger.LogWarning("No queue mapped for DID {Did} — applying fallback treatment.", to);
            call.Fail(CallEndReasons.NoAgentsAvailable, DateTimeOffset.UtcNow);
            await events.AppendAsync(call.Id, "FallbackTreatmentApplied", correlationId, new { did = to }, ct);
            await PersistAsync(call, ct);
            return;
        }

        await routing.EnqueueAsync(call, queue, correlationId, ct);
    }

    private async Task OnAbandonedAsync(TelephonyEvent e, CancellationToken ct)
    {
        var call = state.GetCall(e.CallId);
        if (call is null || !call.IsLive) return;

        if (!_terminated.TryAdd(call.Id, 0)) return;

        state.DequeueEverywhere(call.Id);
        call.End(CallEndReasons.Abandoned, DateTimeOffset.UtcNow);

        await events.AppendAsync(call.Id, "CallAbandoned", call.Id.ToString("N"), new
        {
            waitSeconds = call.WaitSeconds,
            // Short abandons are excluded from service level by convention (FR-C8, Q22).
            shortAbandon = call.WaitSeconds < 5
        }, ct);

        await PersistAsync(call, ct);
    }

    private async Task OnOutboundAnsweredAsync(TelephonyEvent e, CancellationToken ct)
    {
        var call = state.GetCall(e.CallId);
        if (call is null || call.Status == CallStatus.Connected) return;

        call.Answer(DateTimeOffset.UtcNow);
        await events.AppendAsync(call.Id, "CallAnswered", call.Id.ToString("N"), new { answeredBy = "customer" }, ct);

        if (call.AgentId is { } agentId)
            await hub.Clients.Group(CallCenterHub.AgentGroup(agentId))
                .SendAsync("callConnected", new { callId = call.Id, answeredAt = call.AnsweredAt }, ct);
    }

    private async Task OnOutboundNoAnswerAsync(TelephonyEvent e, CancellationToken ct)
    {
        var call = state.GetCall(e.CallId);
        if (call is null || !call.IsLive) return;

        call.End("NoAnswer", DateTimeOffset.UtcNow);
        await events.AppendAsync(call.Id, "OutboundNoAnswer", call.Id.ToString("N"), null, ct);
        await MoveAgentToWrapAsync(call, ct);
    }

    private async Task OnCallEndedAsync(Guid callId, string reason, CancellationToken ct)
    {
        var call = state.GetCall(callId);
        if (call is null || !call.IsLive) return;

        // Exactly-once, even when the command path and the provider event race each other.
        if (!_terminated.TryAdd(callId, 0)) return;

        var wasConnected = call.AnsweredAt is not null;
        state.DequeueEverywhere(callId);
        call.End(reason, DateTimeOffset.UtcNow);

        await events.AppendAsync(callId, "CallEnded", callId.ToString("N"), new
        {
            reason,
            talkSeconds = call.TalkSeconds,
            waitSeconds = call.WaitSeconds
        }, ct);

        if (wasConnected) await MoveAgentToWrapAsync(call, ct);
        else await PersistAsync(call, ct);
    }

    private async Task MoveAgentToWrapAsync(Call call, CancellationToken ct)
    {
        if (call.AgentId is not { } agentId || state.GetAgent(agentId) is not { } agent) return;

        AgentStateMachine.Force(agent, AgentState.AfterCallWork, DateTimeOffset.UtcNow);
        agent.AttachCall(call.Id);
        agent.RecordHandledCall();
        _wrapStartedAt[call.Id] = DateTimeOffset.UtcNow;

        await hub.Clients.Group(CallCenterHub.AgentGroup(agentId)).SendAsync("callEnded", new
        {
            callId = call.Id,
            reason = call.EndReason,
            talkSeconds = call.TalkSeconds,
            requiresDisposition = true
        }, ct);
    }

    // ============================================================ agent commands

    public async Task<CommandResult> AnswerAsync(Guid agentId, Guid callId, string token, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // The security control that makes "offered to exactly one agent" real (NFR-SEC3).
        // Without this, any authenticated agent could answer any ringing call by guessing an id.
        if (!state.ValidateReservation(agentId, callId, token, now))
        {
            await events.AppendAsync(callId, "AnswerRejected", callId.ToString("N"), new
            {
                agentId,
                reason = "InvalidOrExpiredReservation"
            }, ct);
            return CommandResult.Fail("This call is no longer reserved for you.");
        }

        var call = state.GetCall(callId);
        var agent = state.GetAgent(agentId);
        if (call is null || agent is null) return CommandResult.Fail("Call not found.");

        await telephony.AnswerAsync(callId, ct);

        call.Answer(now);
        AgentStateMachine.Force(agent, AgentState.OnCall, now);
        agent.AttachCall(callId);
        state.ClearReservation(agentId);

        if (call.QueueId is { } qid && state.GetQueue(qid) is { RecordCalls: true })
        {
            await telephony.SetRecordingAsync(callId, RecordingCommand.Start, ct);
            call.SetRecording(true, false);
        }

        await events.AppendAsync(callId, "CallAnswered", callId.ToString("N"), new
        {
            agentId,
            agentName = agent.DisplayName,
            waitSeconds = call.WaitSeconds,
            // The SLA measurement point (FR-H4, Q22).
            withinSla = call.QueueId is { } q && state.GetQueue(q) is { } def
                        && call.WaitSeconds <= def.SlaThresholdSeconds
        }, ct);

        await hub.Clients.Group(CallCenterHub.AgentGroup(agentId)).SendAsync("callConnected", new
        {
            callId,
            answeredAt = now,
            recording = call.RecordingActive
        }, ct);

        return CommandResult.Ok;
    }

    public async Task<CommandResult> RejectAsync(Guid agentId, Guid callId, string token, CancellationToken ct = default)
    {
        if (!state.ValidateReservation(agentId, callId, token, DateTimeOffset.UtcNow))
            return CommandResult.Fail("This call is no longer reserved for you.");

        var call = state.GetCall(callId);
        var agent = state.GetAgent(agentId);
        if (call is null || agent is null) return CommandResult.Fail("Call not found.");

        await routing.HandleRingNoAnswerAsync(call, agent, "AgentRejected", ct);
        return CommandResult.Ok;
    }

    public async Task<CommandResult> HoldAsync(Guid agentId, Guid callId, bool hold, CancellationToken ct = default)
    {
        var call = RequireOwnedCall(agentId, callId);
        if (call is null) return CommandResult.Fail("You are not on that call.");

        await telephony.HoldAsync(callId, hold, ct);
        call.SetHold(hold);

        await events.AppendAsync(callId, hold ? "HoldStarted" : "HoldEnded", callId.ToString("N"), new { agentId }, ct);
        return CommandResult.With(new { onHold = hold });
    }

    public async Task<CommandResult> SendDtmfAsync(Guid agentId, Guid callId, string digits, CancellationToken ct = default)
    {
        var call = RequireOwnedCall(agentId, callId);
        if (call is null) return CommandResult.Fail("You are not on that call.");

        await telephony.SendDtmfAsync(callId, digits, ct);

        // PCI: the digits themselves are never written to the event stream or the logs (NFR-SEC6).
        await events.AppendAsync(callId, "DtmfSent", callId.ToString("N"), new { agentId, digitCount = digits.Length }, ct);
        return CommandResult.Ok;
    }

    /// <summary>
    /// PCI pause/resume (FR-G3). The paused ranges are journalled so we can *prove* the gap to an
    /// auditor rather than assert it.
    /// </summary>
    public async Task<CommandResult> SetRecordingAsync(Guid agentId, Guid callId, bool paused, CancellationToken ct = default)
    {
        var call = RequireOwnedCall(agentId, callId);
        if (call is null) return CommandResult.Fail("You are not on that call.");
        if (!call.RecordingActive && !paused) return CommandResult.Fail("This call is not being recorded.");

        await telephony.SetRecordingAsync(callId,
            paused ? RecordingCommand.Pause : RecordingCommand.Resume, ct);

        call.SetRecording(true, paused);

        await events.AppendAsync(callId, paused ? "RecordingPaused" : "RecordingResumed", callId.ToString("N"), new
        {
            agentId,
            offsetSeconds = call.TalkSeconds,
            reason = paused ? "PciCardCapture" : "Resumed"
        }, ct);

        return CommandResult.With(new { recordingPaused = paused });
    }

    /// <summary>
    /// Ends the call, then tells the provider.
    /// </summary>
    /// <remarks>
    /// The order matters. Instructing the provider first makes it echo a <c>CallEnded</c> event
    /// back, which is handled on a background thread — so the two paths race, and if the background
    /// one wins, this command returns <i>before</i> the agent has actually moved to wrap-up. A
    /// client that hangs up and immediately reads its own state then sees the old one.
    ///
    /// Applying the outcome first removes the race: the command only returns once the state change
    /// is real, and the provider's echoed event is dropped by the idempotency guard. Telling the
    /// carrier second is safe — the agent has pressed hang up, so ending the call locally is the
    /// decision, and the carrier instruction is how we carry it out.
    /// </remarks>
    public async Task<CommandResult> HangupAsync(Guid agentId, Guid callId, CancellationToken ct = default)
    {
        var call = RequireOwnedCall(agentId, callId);
        if (call is null) return CommandResult.Fail("You are not on that call.");

        await OnCallEndedAsync(callId, CallEndReasons.AgentHangup, ct);
        await telephony.HangupAsync(callId, CallEndReasons.AgentHangup, ct);
        return CommandResult.Ok;
    }

    public async Task<CommandResult> TransferAsync(Guid agentId, Guid callId, string? toNumber, Guid? toAgentId,
        CancellationToken ct = default)
    {
        var call = RequireOwnedCall(agentId, callId);
        if (call is null) return CommandResult.Fail("You are not on that call.");

        await telephony.TransferAsync(callId, new TransferTarget(toNumber, toAgentId, null), TransferMode.Blind, ct);

        await events.AppendAsync(callId, "TransferInitiated", callId.ToString("N"), new
        {
            fromAgentId = agentId,
            toNumber,
            toAgentId,
            mode = "Blind"
        }, ct);

        // Same ordering rule as HangupAsync: settle our own state first, then release the leg.
        await OnCallEndedAsync(callId, CallEndReasons.Transferred, ct);
        await telephony.HangupAsync(callId, CallEndReasons.Transferred, ct);
        return CommandResult.Ok;
    }

    /// <summary>Wrap-up. Mandatory disposition before returning to Available (Q21).</summary>
    public async Task<CommandResult> SubmitDispositionAsync(Guid agentId, Guid callId, string disposition, string? notes,
        CancellationToken ct = default)
    {
        var call = state.GetCall(callId);
        var agent = state.GetAgent(agentId);
        if (call is null || agent is null) return CommandResult.Fail("Call not found.");
        if (call.AgentId != agentId) return CommandResult.Fail("That is not your call.");
        if (string.IsNullOrWhiteSpace(disposition)) return CommandResult.Fail("A disposition is required.");

        call.Complete(disposition, notes);

        var wrapSeconds = _wrapStartedAt.TryRemove(callId, out var startedAt)
            ? (int)(DateTimeOffset.UtcNow - startedAt).TotalSeconds
            : 0;

        await events.AppendAsync(callId, "DispositionSet", callId.ToString("N"), new
        {
            agentId,
            disposition,
            hasNotes = !string.IsNullOrWhiteSpace(notes),
            wrapSeconds
        }, ct);

        await PersistAsync(call, ct, wrapSeconds);

        // Asynchronous, idempotent by CallId — a CRM outage delays this, it never blocks the agent.
        crm.QueueActivityWriteBack(callId,
            $"{call.Direction} call, {call.TalkSeconds}s, outcome: {disposition}");

        if (agent.State == AgentState.AfterCallWork)
            AgentStateMachine.Transition(agent, AgentState.Available, DateTimeOffset.UtcNow);

        await BroadcastAgentStateAsync(agent, ct);
        routing.Trigger();

        return CommandResult.Ok;
    }

    // ============================================================ outbound

    public async Task<CommandResult> PlaceOutboundAsync(Guid agentId, string rawNumber, string callerId,
        CancellationToken ct = default)
    {
        var agent = state.GetAgent(agentId);
        if (agent is null) return CommandResult.Fail("Agent not found.");
        if (agent.State != AgentState.Available)
            return CommandResult.Fail($"You must be Available to place a call (currently {agent.State}).");

        var e164 = PhoneNumber.ToE164(rawNumber);
        if (!PhoneNumber.IsValid(e164)) return CommandResult.Fail($"'{rawNumber}' is not a valid number.");

        var callId = Guid.NewGuid();
        var correlationId = callId.ToString("N");

        // The regulatory gate: synchronous, in the call path, fails closed (FR-D4, R-07).
        var check = await suppression.CheckAsync(e164, ct);
        if (check.Blocked)
        {
            await events.AppendAsync(callId, "OutboundBlocked", correlationId, new
            {
                agentId,
                number = PhoneNumber.Mask(e164),
                reason = check.Reason,
                source = check.Source
            }, ct);

            await events.AuditAsync(agent.DisplayName, "OutboundBlocked", PhoneNumber.Mask(e164), check.Reason, ct);
            return CommandResult.Fail(check.Reason ?? "Blocked.");
        }

        var call = new Call
        {
            Id = callId,
            Direction = CallDirection.Outbound,
            FromNumber = callerId,
            ToNumber = e164,
            InitiatedAt = DateTimeOffset.UtcNow
        };
        state.AddCall(call);

        // Outbound occupies agent state identically to inbound, so routing and reporting stay
        // consistent (FR-D5) — an agent on an outbound call is not offered a queue call.
        AgentStateMachine.Force(agent, AgentState.OnCall, DateTimeOffset.UtcNow);
        call.AttachAgentDirect(agent, DateTimeOffset.UtcNow);
        agent.AttachCall(callId);

        await events.AppendAsync(callId, "CallInitiated", correlationId, new
        {
            direction = "Outbound",
            agentId,
            to = PhoneNumber.Mask(e164),
            callerId
        }, ct);

        var handle = await telephony.PlaceCallAsync(new PlaceCallRequest(callId, callerId, e164, true), ct);
        call.AttachProvider(handle.ProviderName, handle.ProviderCallId);

        // Screen-pop on outbound too — the agent should see who they called.
        var lookup = await crm.LookupByNumberAsync(e164, ct);
        if (lookup.Contact is not null) call.AttachContact(lookup.Contact.ExternalId, lookup.Contact.DisplayName);

        await BroadcastAgentStateAsync(agent, ct);

        return CommandResult.With(new
        {
            callId,
            to = e164,
            contact = lookup.Contact,
            status = call.Status.ToString()
        });
    }

    // ============================================================ agent state

    public async Task<CommandResult> SetAgentStateAsync(Guid agentId, AgentState requested, string? reason,
        CancellationToken ct = default)
    {
        var agent = state.GetAgent(agentId);
        if (agent is null) return CommandResult.Fail("Agent not found.");

        var previousState = agent.State;
        var previousSince = agent.StateSince;

        var result = AgentStateMachine.Transition(agent, requested, DateTimeOffset.UtcNow, reason, agentInitiated: true);
        if (!result.Allowed) return CommandResult.Fail(result.Error!);

        await LogAgentStateAsync(agent, previousState, previousSince, ct);
        await BroadcastAgentStateAsync(agent, ct);

        if (agent.State == AgentState.Available) routing.Trigger();

        return CommandResult.With(new { state = agent.State.ToString(), reason = agent.NotReadyReason });
    }

    private async Task LogAgentStateAsync(Agent agent, AgentState previous, DateTimeOffset since, CancellationToken ct)
    {
        if (previous == AgentState.LoggedOut && since == DateTimeOffset.MinValue) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AgentStateLog.Add(new AgentStateLogRecord
        {
            AgentId = agent.Id,
            AgentName = agent.DisplayName,
            State = previous.ToString(),
            ReasonCode = agent.NotReadyReason,
            StartedAt = since,
            EndedAt = DateTimeOffset.UtcNow,
            DurationSeconds = (int)(DateTimeOffset.UtcNow - since).TotalSeconds
        });
        await db.SaveChangesAsync(ct);
    }

    public Task BroadcastAgentStateAsync(Agent agent, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync("agentStateChanged", new
        {
            agentId = agent.Id,
            name = agent.DisplayName,
            state = agent.State.ToString(),
            reason = agent.NotReadyReason,
            currentCallId = agent.CurrentCallId
        }, ct);

    // ============================================================ helpers

    private Call? RequireOwnedCall(Guid agentId, Guid callId)
    {
        var call = state.GetCall(callId);
        return call is not null && call.AgentId == agentId && call.IsLive ? call : null;
    }

    /// <summary>
    /// Write the CDR. In production this is a projection of the event stream rather than a direct
    /// write, but the shape is the same: computed once on completion so reports never aggregate raw
    /// events at query time (§4.7.2).
    /// </summary>
    private async Task PersistAsync(Call call, CancellationToken ct, int wrapSeconds = 0)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var record = await db.Calls.FirstOrDefaultAsync(c => c.Id == call.Id, ct);
        if (record is null)
        {
            record = new CallRecord { Id = call.Id, TenantId = state.TenantId };
            db.Calls.Add(record);
        }

        record.Direction = call.Direction.ToString();
        record.FromNumber = call.FromNumber;
        record.ToNumber = call.ToNumber;
        record.QueueName = call.QueueName;
        record.Priority = call.Priority;
        record.Status = call.Status.ToString();
        record.InitiatedAt = call.InitiatedAt;
        record.QueuedAt = call.QueuedAt;
        record.AnsweredAt = call.AnsweredAt;
        record.EndedAt = call.EndedAt;
        record.WaitSeconds = call.WaitSeconds;
        record.TalkSeconds = call.TalkSeconds;
        record.WrapSeconds = wrapSeconds;
        record.RnaCount = call.RnaCount;
        record.AgentId = call.AgentId;
        record.AgentName = call.AgentName;
        record.EndReason = call.EndReason;
        record.Disposition = call.Disposition;
        record.Notes = call.Notes;
        record.ExternalContactId = call.ExternalContactId;
        record.ProviderName = call.ProviderName;
        record.ProviderCallId = call.ProviderCallId;
        record.Recorded = call.RecordingActive || call.AnsweredAt is not null;
        record.RecordingPausedRanges = call.RecordingPaused ? JsonSerializer.Serialize(new[] { call.TalkSeconds }) : null;

        await db.SaveChangesAsync(ct);
    }
}
