using CallCenter.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Api.Services;

public sealed record AgentView(
    Guid Id, string Name, string Extension, bool IsSupervisor,
    string State, Guid? CurrentCallId, DateTimeOffset StateChangedAt);

public sealed record CallView(
    Guid Id, string From, string To, string Status,
    DateTimeOffset QueuedAt, DateTimeOffset? AnsweredAt, DateTimeOffset? EndedAt,
    Guid? AgentId, string? AgentName, string? Disposition, int WaitSeconds, int TalkSeconds);

public sealed record Stats(int Waiting, int Available, int OnCall, int Completed, int LongestWaitSeconds);

/// <summary>Everything a screen needs, in one object. See the note on broadcasting in the service.</summary>
public sealed record Snapshot(
    IReadOnlyList<AgentView> Agents,
    IReadOnlyList<CallView> Queue,
    IReadOnlyList<CallView> Recent,
    Stats Stats,
    DateTimeOffset ServerTime);

/// <summary>Thrown for a rejected command (wrong state, call not yours). Surfaces as HTTP 400.</summary>
public sealed class CallCenterException(string message) : Exception(message);

/// <summary>
/// The whole domain: who is available, what is queued, and who gets the next call.
///
/// Two deliberate simplifications for this prototype, both discussed in the design document:
/// the platform runs as a single instance, so one in-process lock is enough to make routing
/// safe; and there is no telephony vendor, so calls are created by a button instead of by a
/// carrier webhook.
/// </summary>
public sealed class CallCenterService(
    IDbContextFactory<CallCenterDbContext> dbFactory,
    ISnapshotPublisher publisher) : ICallCenterService
{
    /// <summary>
    /// Routing must not interleave, or two agents get the same call. One instance, one lock.
    /// The design document covers what replaces this when the platform runs on several servers.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<Snapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await BuildSnapshotAsync(db, ct);
    }

    public Task<Snapshot> SignInAsync(Guid agentId, CancellationToken ct = default) =>
        MutateAsync(async (db, now) =>
        {
            // Signed in but not yet taking calls — the agent presses "Ready" when they are set up.
            var agent = await RequireAgentAsync(db, agentId, ct);
            SetState(agent, AgentState.NotReady, now);
        }, ct);

    public Task<Snapshot> SignOutAsync(Guid agentId, CancellationToken ct = default) =>
        MutateAsync(async (db, now) =>
        {
            var agent = await RequireAgentAsync(db, agentId, ct);
            if (agent.CurrentCallId is not null)
                throw new CallCenterException("Finish the current call before signing out.");

            SetState(agent, AgentState.Offline, now);
        }, ct);

    /// <summary>Ready / not ready. Anything else is set by the platform, never asked for by the client.</summary>
    public Task<Snapshot> SetReadyAsync(Guid agentId, bool ready, CancellationToken ct = default) =>
        MutateAsync(async (db, now) =>
        {
            var agent = await RequireAgentAsync(db, agentId, ct);
            if (agent.State is AgentState.Ringing or AgentState.OnCall or AgentState.WrapUp)
                throw new CallCenterException($"Cannot change availability while {agent.State}.");

            SetState(agent, ready ? AgentState.Available : AgentState.NotReady, now);
        }, ct);

    /// <summary>Stands in for the carrier telling us a customer has called in.</summary>
    public Task<Snapshot> ReceiveInboundCallAsync(string from, string to, CancellationToken ct = default) =>
        MutateAsync((db, now) =>
        {
            db.Calls.Add(new Call
            {
                Id = Guid.NewGuid(),
                FromNumber = string.IsNullOrWhiteSpace(from) ? "+10000000000" : from.Trim(),
                ToNumber = string.IsNullOrWhiteSpace(to) ? "+18005550100" : to.Trim(),
                Status = CallStatus.Queued,
                QueuedAt = now
            });
            return Task.CompletedTask;
        }, ct);

    public Task<Snapshot> AnswerAsync(Guid agentId, Guid callId, CancellationToken ct = default) =>
        MutateAsync(async (db, now) =>
        {
            var (agent, call) = await RequireOwnedCallAsync(db, agentId, callId, ct);
            if (call.Status != CallStatus.Ringing)
                throw new CallCenterException("That call is no longer ringing.");

            call.Status = CallStatus.Connected;
            call.AnsweredAt = now;
            call.WaitSeconds = Seconds(call.QueuedAt, now);
            SetState(agent, AgentState.OnCall, now);
        }, ct);

    /// <summary>
    /// The agent did not pick up. The call goes back to the front of the queue and the agent is
    /// made not-ready, so the platform does not immediately offer them the same call again.
    /// </summary>
    public Task<Snapshot> DeclineAsync(Guid agentId, Guid callId, CancellationToken ct = default) =>
        MutateAsync(async (db, now) =>
        {
            var (agent, call) = await RequireOwnedCallAsync(db, agentId, callId, ct);
            if (call.Status != CallStatus.Ringing)
                throw new CallCenterException("That call is no longer ringing.");

            call.Status = CallStatus.Queued;
            call.AgentId = null;
            call.AgentName = null;
            agent.CurrentCallId = null;
            SetState(agent, AgentState.NotReady, now);
        }, ct);

    public Task<Snapshot> HangUpAsync(Guid agentId, Guid callId, CancellationToken ct = default) =>
        MutateAsync(async (db, now) =>
        {
            var (agent, call) = await RequireOwnedCallAsync(db, agentId, callId, ct);
            if (call.Status != CallStatus.Connected)
                throw new CallCenterException("That call is not connected.");

            call.Status = CallStatus.WrapUp;
            call.EndedAt = now;
            call.TalkSeconds = Seconds(call.AnsweredAt ?? now, now);
            SetState(agent, AgentState.WrapUp, now);
        }, ct);

    /// <summary>Wrap-up is part of the call, not an afterthought: no disposition, no completion.</summary>
    public Task<Snapshot> CompleteWrapUpAsync(
        Guid agentId, Guid callId, string disposition, string? notes, CancellationToken ct = default) =>
        MutateAsync(async (db, now) =>
        {
            var (agent, call) = await RequireOwnedCallAsync(db, agentId, callId, ct);
            if (call.Status != CallStatus.WrapUp)
                throw new CallCenterException("That call is not in wrap-up.");
            if (string.IsNullOrWhiteSpace(disposition))
                throw new CallCenterException("Choose a disposition before finishing.");

            call.Status = CallStatus.Completed;
            call.Disposition = disposition.Trim();
            call.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

            agent.CurrentCallId = null;
            SetState(agent, AgentState.Available, now); // straight back into rotation
        }, ct);

    /// <summary>The caller gave up while waiting. Abandoned calls are the number supervisors watch.</summary>
    public Task<Snapshot> AbandonAsync(Guid callId, CancellationToken ct = default) =>
        MutateAsync(async (db, now) =>
        {
            var call = await db.Calls.FirstOrDefaultAsync(c => c.Id == callId, ct)
                ?? throw new CallCenterException("Unknown call.");
            if (call.Status != CallStatus.Queued)
                throw new CallCenterException("Only a waiting call can be abandoned.");

            call.Status = CallStatus.Abandoned;
            call.EndedAt = now;
            call.WaitSeconds = Seconds(call.QueuedAt, now);
        }, ct);

    // ---------------------------------------------------------------- routing

    /// <summary>
    /// Longest-waiting call to the agent who has been available longest. Runs after every change,
    /// which is what makes a queued call connect the moment somebody presses Ready.
    /// </summary>
    private static async Task RouteQueuedCallsAsync(CallCenterDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var waiting = await db.Calls
            .Where(c => c.Status == CallStatus.Queued)
            .OrderBy(c => c.QueuedAt)
            .ToListAsync(ct);

        var free = await db.Agents
            .Where(a => a.State == AgentState.Available)
            .OrderBy(a => a.StateChangedAt)
            .ToListAsync(ct);

        var next = 0;
        foreach (var call in waiting)
        {
            if (next >= free.Count) break; // everyone is busy; the rest keep waiting
            var agent = free[next++];

            call.Status = CallStatus.Ringing;
            call.AgentId = agent.Id;
            call.AgentName = agent.Name;

            agent.CurrentCallId = call.Id;
            SetState(agent, AgentState.Ringing, now);
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// Every command follows the same shape: take the lock, apply the change, route whatever is
    /// waiting, save, then push the new picture to every open screen.
    /// </summary>
    private async Task<Snapshot> MutateAsync(
        Func<CallCenterDbContext, DateTimeOffset, Task> apply, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;

            await apply(db, now);
            await db.SaveChangesAsync(ct); // so routing sees the change we just made

            await RouteQueuedCallsAsync(db, now, ct);
            await db.SaveChangesAsync(ct);

            var snapshot = await BuildSnapshotAsync(db, ct);
            await publisher.PublishAsync(snapshot, ct);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One snapshot for every screen, pushed on every change. At 50 agents this is a few kilobytes
    /// a second and it keeps the browser free of state it could get wrong. The design document
    /// explains why a larger deployment sends targeted events instead.
    /// </summary>
    private static async Task<Snapshot> BuildSnapshotAsync(CallCenterDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var agents = await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        var live = await db.Calls.AsNoTracking()
            .Where(c => c.Status != CallStatus.Completed && c.Status != CallStatus.Abandoned)
            .OrderBy(c => c.QueuedAt).ToListAsync(ct);
        var recent = await db.Calls.AsNoTracking()
            .Where(c => c.Status == CallStatus.Completed || c.Status == CallStatus.Abandoned)
            .OrderByDescending(c => c.EndedAt).Take(20).ToListAsync(ct);

        var queued = live.Where(c => c.Status == CallStatus.Queued).ToList();

        return new Snapshot(
            agents.Select(Project).ToList(),
            live.Select(Project).ToList(),
            recent.Select(Project).ToList(),
            new Stats(
                Waiting: queued.Count,
                Available: agents.Count(a => a.State == AgentState.Available),
                OnCall: agents.Count(a => a.State == AgentState.OnCall),
                Completed: await db.Calls.CountAsync(c => c.Status == CallStatus.Completed, ct),
                LongestWaitSeconds: queued.Count == 0 ? 0 : Seconds(queued.Min(c => c.QueuedAt), now)),
            now);
    }

    private static AgentView Project(Agent a) =>
        new(a.Id, a.Name, a.Extension, a.IsSupervisor, a.State.ToString(), a.CurrentCallId, a.StateChangedAt);

    private static CallView Project(Call c) =>
        new(c.Id, c.FromNumber, c.ToNumber, c.Status.ToString(), c.QueuedAt, c.AnsweredAt, c.EndedAt,
            c.AgentId, c.AgentName, c.Disposition, c.WaitSeconds, c.TalkSeconds);

    private static void SetState(Agent agent, AgentState state, DateTimeOffset now)
    {
        if (agent.State == state) return; // keep the clock running on an unchanged state
        agent.State = state;
        agent.StateChangedAt = now;
    }

    private static int Seconds(DateTimeOffset from, DateTimeOffset to) =>
        Math.Max(0, (int)(to - from).TotalSeconds);

    private static async Task<Agent> RequireAgentAsync(CallCenterDbContext db, Guid agentId, CancellationToken ct) =>
        await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct)
        ?? throw new CallCenterException("Unknown agent.");

    /// <summary>
    /// An agent may only act on the call the server gave them. Without this check, anyone who can
    /// guess a call id can hang up someone else's customer.
    /// </summary>
    private static async Task<(Agent Agent, Call Call)> RequireOwnedCallAsync(
        CallCenterDbContext db, Guid agentId, Guid callId, CancellationToken ct)
    {
        var agent = await RequireAgentAsync(db, agentId, ct);
        var call = await db.Calls.FirstOrDefaultAsync(c => c.Id == callId, ct)
            ?? throw new CallCenterException("Unknown call.");

        if (call.AgentId != agentId)
            throw new CallCenterException("That call belongs to another agent.");

        return (agent, call);
    }
}
