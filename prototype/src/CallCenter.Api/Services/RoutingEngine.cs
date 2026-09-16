using System.Threading.Channels;
using CallCenter.Api.Realtime;
using CallCenter.Domain;
using CallCenter.Domain.Routing;
using CallCenter.Telephony;
using Microsoft.AspNetCore.SignalR;

namespace CallCenter.Api.Services;

public sealed class RoutingOptions
{
    /// <summary>Consecutive ring-no-answers before the agent is logged out (Q20).</summary>
    public int MaxConsecutiveRna { get; set; } = 3;

    /// <summary>Priority boost applied when a call is re-queued after an RNA, so it does not go to
    /// the back of the line through no fault of the caller.</summary>
    public int RnaPriorityBoost { get; set; } = 2;

    /// <summary>Seconds without a heartbeat before an agent is treated as gone (FR-B4).</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 45;

    /// <summary>How often the engine sweeps for expired reservations and dead sessions.</summary>
    public int SweepIntervalMs { get; set; } = 500;
}

/// <summary>
/// The routing engine (docs/04-system-design.md §4.3.2).
///
/// This is the only genuinely stateful component and the only one whose correctness directly
/// determines whether the business works. In production it runs as its own deployable, owning a set
/// of queue partitions leased from Redis, with single-writer semantics per partition — which is why
/// it is the one module extracted from day one (§5.4).
///
/// Three invariants are worth naming, because they are what the tests assert:
/// <list type="number">
/// <item>A call is offered to <b>exactly one</b> agent at a time (atomic reserve).</item>
/// <item>An agent can only answer a call they were <b>actually offered</b> (token validation).</item>
/// <item>A reservation <b>always</b> resolves — answered, rejected, or expired by TTL. An agent is
/// never left stranded in Reserved because a push was missed or a provider call timed out.</item>
/// </list>
/// </summary>
public sealed class RoutingEngine(
    PlatformState state,
    InteractionEventStore events,
    ITelephonyProvider telephony,
    CrmClient crm,
    IHubContext<CallCenterHub> hub,
    RoutingOptions options,
    ILogger<RoutingEngine> logger) : BackgroundService
{
    private readonly Channel<byte> _wakeups = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Wake the matching loop. Called on call arrival, agent-available and reservation expiry.</summary>
    public void Trigger() => _wakeups.Writer.TryWrite(0);

    // ------------------------------------------------------------------ enqueue

    public async Task EnqueueAsync(Call call, QueueDefinition queue, string correlationId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        call.Enqueue(queue.Id, queue.Name, queue.BasePriority, queue.RequiredSkills, now);
        state.Enqueue(new QueuedCall(call.Id, queue.Id, call.Priority, call.QueuedAt ?? now, queue.RequiredSkills));

        await events.AppendAsync(call.Id, "CallQueued", correlationId, new
        {
            queue = queue.Name,
            priority = call.Priority,
            requiredSkills = queue.RequiredSkills
        }, ct);

        Trigger();
    }

    // ------------------------------------------------------------------ matching

    /// <summary>
    /// One matching cycle. Public so tests can drive it deterministically instead of racing a timer.
    ///
    /// The work here is bounded by *this queue's* depth and the eligible-agent set, not by the size
    /// of the centre — which is what keeps routing cost effectively flat from 50 to 500 agents
    /// (docs/05-scalability-plan.md §5.2).
    /// </summary>
    public async Task<int> MatchCycleAsync(CancellationToken ct = default)
    {
        var offers = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var queue in state.Queues)
        {
            var policy = SelectionPolicyFactory.Resolve(queue.SelectionPolicy);

            foreach (var queued in state.PeekOrdered(queue.Id))
            {
                ct.ThrowIfCancellationRequested();

                var call = state.GetCall(queued.CallId);
                if (call is null || call.Status != CallStatus.Queued)
                {
                    state.Dequeue(queue.Id, queued.CallId);
                    continue;
                }

                var candidates = state.Agents
                    .Where(a => a.IsRoutable
                                && a.Queues.Contains(queue.Id)
                                && a.HasSkills(queued.RequiredSkills))
                    .ToList();

                if (candidates.Count == 0) continue;   // nobody eligible — leave it queued

                var eligible = candidates.Where(a => !call.OfferedTo.Contains(a.Id)).ToList();

                // Everyone eligible has already had a turn and not answered. Rather than strand the
                // caller forever, clear the history and start again — otherwise a queue with one
                // distracted agent silently black-holes calls.
                if (eligible.Count == 0)
                {
                    call.OfferedTo.Clear();
                    eligible = candidates;
                }

                var offered = await TryOfferAsync(call, queue, eligible, policy, now, ct);
                if (offered) offers++;
            }
        }

        return offers;
    }

    private async Task<bool> TryOfferAsync(
        Call call,
        QueueDefinition queue,
        List<Agent> eligible,
        IAgentSelectionPolicy policy,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var queued = new QueuedCall(call.Id, queue.Id, call.Priority, call.QueuedAt ?? now, queue.RequiredSkills);
        var remaining = eligible.ToList();

        while (remaining.Count > 0)
        {
            var chosen = policy.Select(remaining, queued);
            if (chosen is null) return false;

            var ttl = TimeSpan.FromSeconds(queue.RingTimeoutSeconds);
            var reserved = state.TryReserve(chosen.Id, call.Id, now, ttl);

            if (!reserved.Success)
            {
                // Lost the race to another cycle or another queue — try the next agent.
                remaining.Remove(chosen);
                continue;
            }

            await OfferAsync(call, queue, chosen, reserved.Reservation!, ct);
            return true;
        }

        return false;
    }

    private async Task OfferAsync(Call call, QueueDefinition queue, Agent agent, Reservation reservation, CancellationToken ct)
    {
        var correlationId = call.Id.ToString("N");
        var now = DateTimeOffset.UtcNow;

        call.Offer(agent, now);
        state.Dequeue(queue.Id, call.Id);

        await events.AppendAsync(call.Id, "CallOffered", correlationId, new
        {
            agentId = agent.Id,
            agentName = agent.DisplayName,
            queue = queue.Name,
            waitSeconds = call.WaitSeconds,
            expiresAt = reservation.ExpiresAt
        }, ct);

        // Push the offer, look the customer up, and ring the phone — all in parallel.
        // The CRM lookup is deliberately NOT on the critical path to ringing (NFR-P1 vs NFR-P2):
        // the phone must ring in under a second whether or not the CRM answers.
        var push = hub.Clients.Group(CallCenterHub.AgentGroup(agent.Id)).SendAsync("callOffered", new
        {
            callId = call.Id,
            reservationToken = reservation.Token,
            expiresAt = reservation.ExpiresAt,
            direction = call.Direction.ToString(),
            from = call.FromNumber,
            to = call.ToNumber,
            queue = queue.DisplayName,
            waitSeconds = call.WaitSeconds,
            ringTimeoutSeconds = queue.RingTimeoutSeconds
        }, ct);

        var screenPop = ScreenPopAsync(call, agent, correlationId, ct);

        var ring = RingAsync(call, agent, queue, ct);

        await Task.WhenAll(push, screenPop, ring);
    }

    private async Task RingAsync(Call call, Agent agent, QueueDefinition queue, CancellationToken ct)
    {
        try
        {
            await telephony.RingAgentAsync(call.Id, agent.Id, TimeSpan.FromSeconds(queue.RingTimeoutSeconds), ct);

            if (agent.State == AgentState.Reserved)
                AgentStateMachine.Transition(agent, AgentState.Ringing, DateTimeOffset.UtcNow);

            call.Ring();
        }
        catch (Exception ex)
        {
            // The provider failed to bridge. Release the agent immediately — we never leave someone
            // stuck in Reserved because a vendor API timed out (§4.3.1).
            logger.LogError(ex, "Provider failed to ring agent {AgentId} for call {CallId}", agent.Id, call.Id);
            await ReleaseAndRequeueAsync(call, agent, "ProviderRingFailure", CancellationToken.None);
        }
    }

    private async Task ScreenPopAsync(Call call, Agent agent, string correlationId, CancellationToken ct)
    {
        var lookup = await crm.LookupByNumberAsync(
            call.Direction == CallDirection.Inbound ? call.FromNumber : call.ToNumber, ct);

        if (lookup.Outcome == ScreenPopOutcome.Found && lookup.Contact is not null)
            call.AttachContact(lookup.Contact.ExternalId, lookup.Contact.DisplayName);

        await hub.Clients.Group(CallCenterHub.AgentGroup(agent.Id)).SendAsync("screenPop", new
        {
            callId = call.Id,
            outcome = lookup.Outcome.ToString(),
            contact = lookup.Contact,
            candidates = lookup.Candidates,
            elapsedMs = lookup.ElapsedMs
        }, ct);

        await events.AppendAsync(call.Id, "ScreenPopDelivered", correlationId, new
        {
            outcome = lookup.Outcome.ToString(),
            elapsedMs = lookup.ElapsedMs,
            crmCircuitOpen = crm.CircuitOpen
        }, ct);
    }

    // ------------------------------------------------------------------ RNA / expiry

    /// <summary>
    /// Ring-no-answer (FR-C7). Two things must happen and both matter: the caller goes back into
    /// the queue with a priority boost (it was not their fault), and the agent is taken out of
    /// routing so the next call does not hit the same unattended desk.
    /// </summary>
    public async Task HandleRingNoAnswerAsync(Call call, Agent agent, string cause, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var correlationId = call.Id.ToString("N");

        state.ClearReservation(agent.Id);

        var rnaCount = agent.RecordRna();
        var loggedOut = rnaCount >= options.MaxConsecutiveRna;

        AgentStateMachine.Force(agent,
            loggedOut ? AgentState.LoggedOut : AgentState.NotReady,
            now,
            loggedOut ? null : NotReadyReasons.RingNoAnswer);

        await events.AppendAsync(call.Id, "RingNoAnswer", correlationId, new
        {
            agentId = agent.Id,
            agentName = agent.DisplayName,
            cause,
            consecutiveRna = rnaCount,
            agentLoggedOut = loggedOut
        }, ct);

        await hub.Clients.Group(CallCenterHub.AgentGroup(agent.Id)).SendAsync("callRevoked", new
        {
            callId = call.Id,
            reason = "RingNoAnswer",
            agentState = agent.State.ToString(),
            loggedOut
        }, ct);

        await RequeueAsync(call, correlationId, ct);
    }

    private async Task ReleaseAndRequeueAsync(Call call, Agent agent, string reason, CancellationToken ct)
    {
        state.ClearReservation(agent.Id);
        AgentStateMachine.Force(agent, AgentState.Available, DateTimeOffset.UtcNow);
        await RequeueAsync(call, call.Id.ToString("N"), ct);
    }

    private async Task RequeueAsync(Call call, string correlationId, CancellationToken ct)
    {
        if (call.QueueId is not { } queueId || state.GetQueue(queueId) is not { } queue)
            return;

        // Caller already hung up while we were dealing with the RNA.
        if (call.Status is CallStatus.Abandoned or CallStatus.Completed or CallStatus.Failed)
            return;

        call.RecordRna();

        var boosted = Math.Min(10, queue.BasePriority + options.RnaPriorityBoost * call.RnaCount);
        call.Enqueue(queue.Id, queue.Name, boosted, queue.RequiredSkills, DateTimeOffset.UtcNow);
        state.Enqueue(new QueuedCall(call.Id, queue.Id, boosted, call.QueuedAt ?? DateTimeOffset.UtcNow, queue.RequiredSkills));

        await events.AppendAsync(call.Id, "CallRequeued", correlationId, new
        {
            queue = queue.Name,
            newPriority = boosted,
            rnaCount = call.RnaCount
        }, ct);

        Trigger();
    }

    /// <summary>
    /// The reservation sweeper. In production the Redis TTL expires the reservation whether or not
    /// any process is alive to notice; this loop is the reconciliation pass that turns that
    /// expiry into an RNA (§4.10, "reservation leaked").
    /// </summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var agent in state.ExpiredReservations(now))
        {
            var reservation = agent.Reservation;
            if (reservation is null) continue;

            var call = state.GetCall(reservation.CallId);
            if (call is null)
            {
                state.ClearReservation(agent.Id);
                AgentStateMachine.Force(agent, AgentState.Available, now);
                continue;
            }

            logger.LogInformation("Reservation expired: call={CallId} agent={AgentId}", call.Id, agent.Id);
            await HandleRingNoAnswerAsync(call, agent, "ReservationTimeout", ct);
        }

        await SweepDeadSessionsAsync(now, ct);
    }

    /// <summary>
    /// Zombie-agent detection (FR-B4). An agent whose laptop slept while Available will silently
    /// swallow every call routed to them. This is the cheapest high-value control in the platform.
    /// </summary>
    private async Task SweepDeadSessionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var deadline = now.AddSeconds(-options.HeartbeatTimeoutSeconds);

        foreach (var agent in state.Agents)
        {
            if (agent.State is AgentState.LoggedOut or AgentState.OnCall or AgentState.Ringing) continue;
            if (agent.LastHeartbeat > deadline) continue;

            logger.LogWarning("Agent {AgentId} heartbeat lost — removing from routing.", agent.Id);
            AgentStateMachine.Force(agent, AgentState.LoggedOut, now, NotReadyReasons.ConnectionLost);

            await hub.Clients.Group(CallCenterHub.AgentGroup(agent.Id))
                .SendAsync("sessionEnded", new { reason = "HeartbeatLost" }, ct);
        }
    }

    // ------------------------------------------------------------------ loop

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Routing engine started (provider={Provider}).", telephony.Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Event-driven with a sweep floor: we match on arrival and on agent-available, and
                // additionally sweep for expiries. We do not poll every queue on a tight timer —
                // that is the design that stops scaling at a few hundred agents (§5.2).
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(options.SweepIntervalMs);

                try { await _wakeups.Reader.ReadAsync(cts.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* sweep tick */ }

                await SweepAsync(stoppingToken);
                await MatchCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A routing loop that dies takes the phones with it. Log and keep going.
                logger.LogError(ex, "Matching cycle failed; continuing.");
                await Task.Delay(250, stoppingToken);
            }
        }

        logger.LogInformation("Routing engine stopped.");
    }
}
