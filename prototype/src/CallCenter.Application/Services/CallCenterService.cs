using CallCenter.Application.Abstractions;
using CallCenter.Application.Contracts;
using CallCenter.Domain;
using CallCenter.Domain.Agents;
using CallCenter.Domain.Calls;

namespace CallCenter.Application.Services;

/// <summary>
/// The use cases. Each one does the same three things: apply the change, route whatever is now
/// waiting, and tell the screens.
///
/// Notice what is *not* here. Whether an agent may go ready, whether a call may be answered, how
/// long somebody waited — those are rules about agents and calls, and they live on the entities.
/// This class decides *which* call goes to *which* agent, and in what order things happen. That
/// division is why these methods stay a handful of lines each as the domain grows.
/// </summary>
public class CallCenterService : ICallCenterService
{
    /// <summary>
    /// Routing must not interleave, or two agents are handed the same call. One instance, one
    /// lock. The design document covers what replaces this when the platform runs on several
    /// servers — it becomes the one piece of this class that changes.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ISnapshotPublisher _publisher;
    private readonly IClock _clock;

    public CallCenterService(
        IUnitOfWorkFactory unitOfWorkFactory,
        ISnapshotPublisher publisher,
        IClock clock)
    {
        _unitOfWorkFactory = unitOfWorkFactory;
        _publisher = publisher;
        _clock = clock;
    }

    public async Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var work = await _unitOfWorkFactory.CreateAsync(cancellationToken);

        return await BuildSnapshotAsync(work, cancellationToken);
    }

    public Task<Snapshot> SignInAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (work, now) =>
        {
            var agent = await RequireAgentAsync(work, agentId, cancellationToken);

            agent.SignIn(now);
        }, cancellationToken);

    public Task<Snapshot> SignOutAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (work, now) =>
        {
            var agent = await RequireAgentAsync(work, agentId, cancellationToken);

            agent.SignOut(now);
        }, cancellationToken);

    public Task<Snapshot> SetReadyAsync(Guid agentId, bool ready, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (work, now) =>
        {
            var agent = await RequireAgentAsync(work, agentId, cancellationToken);

            agent.SetReady(ready, now);
        }, cancellationToken);

    public Task<Snapshot> ReceiveInboundCallAsync(
        string from, string to, CancellationToken cancellationToken = default) =>
        ExecuteAsync((work, now) =>
        {
            work.Calls.Add(Call.ArriveFromCustomer(from, to, now));

            return Task.CompletedTask;
        }, cancellationToken);

    public Task<Snapshot> AnswerAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (work, now) =>
        {
            var (agent, call) = await RequireOwnedCallAsync(work, agentId, callId, cancellationToken);

            call.Answer(now);
            agent.AnswerCall(now);
        }, cancellationToken);

    public Task<Snapshot> DeclineAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (work, now) =>
        {
            var (agent, call) = await RequireOwnedCallAsync(work, agentId, callId, cancellationToken);

            call.ReturnToQueue();
            agent.DeclineCall(now);
        }, cancellationToken);

    public Task<Snapshot> HangUpAsync(Guid agentId, Guid callId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (work, now) =>
        {
            var (agent, call) = await RequireOwnedCallAsync(work, agentId, callId, cancellationToken);

            call.End(now);
            agent.EndCall(now);
        }, cancellationToken);

    public Task<Snapshot> CompleteWrapUpAsync(
        Guid agentId, Guid callId, string disposition, string? notes,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (work, now) =>
        {
            var (agent, call) = await RequireOwnedCallAsync(work, agentId, callId, cancellationToken);

            call.File(disposition, notes);
            agent.FinishWrapUp(now);
        }, cancellationToken);

    public Task<Snapshot> AbandonAsync(Guid callId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (work, now) =>
        {
            var call = await work.Calls.FindAsync(callId, cancellationToken)
                ?? throw new DomainException("Unknown call.");

            call.Abandon(now);
        }, cancellationToken);

    // ---------------------------------------------------------------- routing

    /// <summary>
    /// The longest-waiting call goes to the agent who has been free longest. It runs after every
    /// change, which is what makes a waiting call connect the moment somebody presses "ready"
    /// instead of on the next tick of a timer.
    /// </summary>
    private static async Task RouteWaitingCallsAsync(
        IUnitOfWork work, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var waiting = await work.Calls.ListWaitingLongestFirstAsync(cancellationToken);
        var available = await work.Agents.ListAvailableLongestWaitingFirstAsync(cancellationToken);

        var next = 0;

        foreach (var call in waiting)
        {
            if (next >= available.Count)
            {
                break; // everybody is busy; the rest keep waiting
            }

            call.OfferTo(available[next], now);
            next++;
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// The shape every command shares: take the lock, apply the change, route whatever is
    /// waiting, save, then push the new picture to every open screen.
    /// </summary>
    private async Task<Snapshot> ExecuteAsync(
        Func<IUnitOfWork, DateTimeOffset, Task> command, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await using var work = await _unitOfWorkFactory.CreateAsync(cancellationToken);
            var now = _clock.UtcNow;

            await command(work, now);

            // Saved before routing, because the repositories read through the database: a call
            // added a line ago, or an agent who has just become available, is not visible to the
            // next query until it is written. The lock above is what makes the two saves safe.
            await work.SaveChangesAsync(cancellationToken);

            await RouteWaitingCallsAsync(work, now, cancellationToken);
            await work.SaveChangesAsync(cancellationToken);

            var snapshot = await BuildSnapshotAsync(work, cancellationToken);
            await _publisher.PublishAsync(snapshot, cancellationToken);

            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One snapshot for every screen, sent after every change. At fifty agents this is a few
    /// kilobytes a second, and it keeps the browser free of state it could get wrong. The design
    /// document explains why a larger deployment sends targeted events instead.
    /// </summary>
    private async Task<Snapshot> BuildSnapshotAsync(IUnitOfWork work, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        var agents = await work.Agents.ListAllAsync(cancellationToken);
        var inProgress = await work.Calls.ListInProgressAsync(cancellationToken);
        var recent = await work.Calls.ListRecentlyFinishedAsync(20, cancellationToken);
        var completed = await work.Calls.CountCompletedAsync(cancellationToken);

        var waiting = inProgress.Where(call => call.IsWaiting).ToList();

        var stats = new Stats(
            Waiting: waiting.Count,
            Available: agents.Count(agent => agent.IsAvailable),
            OnCall: agents.Count(agent => agent.State == AgentState.OnCall),
            Completed: completed,
            LongestWaitSeconds: waiting.Count == 0
                ? 0
                : Math.Max(0, (int)(now - waiting.Min(call => call.QueuedAt)).TotalSeconds));

        return new Snapshot(
            agents.Select(AgentView.Project).ToList(),
            inProgress.Select(CallView.Project).ToList(),
            recent.Select(CallView.Project).ToList(),
            stats,
            now);
    }

    private static async Task<Agent> RequireAgentAsync(
        IUnitOfWork work, Guid agentId, CancellationToken cancellationToken) =>
        await work.Agents.FindAsync(agentId, cancellationToken)
        ?? throw new DomainException("Unknown agent.");

    private static async Task<(Agent Agent, Call Call)> RequireOwnedCallAsync(
        IUnitOfWork work, Guid agentId, Guid callId, CancellationToken cancellationToken)
    {
        var agent = await RequireAgentAsync(work, agentId, cancellationToken);

        var call = await work.Calls.FindAsync(callId, cancellationToken)
            ?? throw new DomainException("Unknown call.");

        if (!call.BelongsTo(agentId))
        {
            throw new DomainException("That call belongs to another agent.");
        }

        return (agent, call);
    }
}
