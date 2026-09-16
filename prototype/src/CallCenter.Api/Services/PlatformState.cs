using System.Collections.Concurrent;
using CallCenter.Domain;

namespace CallCenter.Api.Services;

public sealed record AgentSession(string Token, Guid AgentId, string Role, DateTimeOffset IssuedAt);

public enum ReserveOutcome { Reserved, AgentNotAvailable, AgentNotFound }

public sealed record ReserveResult(ReserveOutcome Outcome, Reservation? Reservation)
{
    public bool Success => Outcome == ReserveOutcome.Reserved && Reservation is not null;
}

/// <summary>
/// The Redis stand-in (docs/04-system-design.md §4.7.3). Everything here is ephemeral, rebuildable
/// runtime state: agent presence, queue order, reservations, live calls.
///
/// The one thing worth reading carefully is <see cref="TryReserve"/>. In production that is a Lua
/// script executed atomically inside Redis; here it is a lock on the agent. Either way the
/// guarantee is the same and it is the guarantee the whole platform rests on: <b>an agent goes from
/// Available to Reserved exactly once, so a call is never offered to two agents.</b>
/// </summary>
public sealed class PlatformState
{
    private readonly ConcurrentDictionary<Guid, Agent> _agents = new();
    private readonly ConcurrentDictionary<Guid, QueueDefinition> _queues = new();
    private readonly ConcurrentDictionary<Guid, Call> _calls = new();
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, List<QueuedCall>> _queueLines = new();
    private readonly object _reservationLock = new();

    public Guid TenantId { get; } = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    public IReadOnlyCollection<Agent> Agents => _agents.Values.ToArray();
    public IReadOnlyCollection<QueueDefinition> Queues => _queues.Values.ToArray();
    public IReadOnlyCollection<Call> Calls => _calls.Values.ToArray();

    public void AddAgent(Agent agent)
    {
        _agents[agent.Id] = agent;
    }

    public void AddQueue(QueueDefinition queue)
    {
        _queues[queue.Id] = queue;
        _queueLines.TryAdd(queue.Id, []);
    }

    public Agent? GetAgent(Guid id) => _agents.TryGetValue(id, out var a) ? a : null;

    public QueueDefinition? GetQueue(Guid id) => _queues.TryGetValue(id, out var q) ? q : null;

    public QueueDefinition? GetQueueByName(string name) =>
        _queues.Values.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    public Call? GetCall(Guid id) => _calls.TryGetValue(id, out var c) ? c : null;

    public void AddCall(Call call) => _calls[call.Id] = call;

    /// <summary>Live calls plus a rolling window of completed ones, so the demo UI has history.</summary>
    public IReadOnlyList<Call> RecentCalls(int take = 50) =>
        _calls.Values.OrderByDescending(c => c.InitiatedAt).Take(take).ToArray();

    public void TrimCalls(int keep = 400)
    {
        if (_calls.Count <= keep) return;
        foreach (var call in _calls.Values
                     .Where(c => !c.IsLive)
                     .OrderBy(c => c.InitiatedAt)
                     .Take(_calls.Count - keep))
        {
            _calls.TryRemove(call.Id, out _);
        }
    }

    // ---------------- queue lines ----------------

    public void Enqueue(QueuedCall queued)
    {
        var line = _queueLines.GetOrAdd(queued.QueueId, _ => []);
        lock (line)
        {
            line.RemoveAll(q => q.CallId == queued.CallId);
            line.Add(queued);
        }
    }

    public void Dequeue(Guid queueId, Guid callId)
    {
        if (!_queueLines.TryGetValue(queueId, out var line)) return;
        lock (line)
        {
            line.RemoveAll(q => q.CallId == callId);
        }
    }

    public void DequeueEverywhere(Guid callId)
    {
        foreach (var queueId in _queueLines.Keys) Dequeue(queueId, callId);
    }

    /// <summary>
    /// Queue order: highest priority first, then oldest first. In production this is a Redis sorted
    /// set, so ordering survives a routing-worker restart (§7.3).
    /// </summary>
    public IReadOnlyList<QueuedCall> PeekOrdered(Guid queueId)
    {
        if (!_queueLines.TryGetValue(queueId, out var line)) return [];
        lock (line)
        {
            return line
                .OrderByDescending(q => q.Priority)
                .ThenBy(q => q.EnqueuedAt)
                .ToArray();
        }
    }

    public int QueueDepth(Guid queueId) =>
        _queueLines.TryGetValue(queueId, out var line) ? CountSafe(line) : 0;

    private static int CountSafe(List<QueuedCall> line)
    {
        lock (line) return line.Count;
    }

    // ---------------- reservations ----------------

    /// <summary>
    /// Atomic compare-and-set: Available → Reserved, plus a signed token. This is the routing
    /// engine's correctness core (docs/04-system-design.md §4.3.2, FR-C6).
    /// </summary>
    public ReserveResult TryReserve(Guid agentId, Guid callId, DateTimeOffset now, TimeSpan ttl)
    {
        lock (_reservationLock)
        {
            if (!_agents.TryGetValue(agentId, out var agent))
                return new ReserveResult(ReserveOutcome.AgentNotFound, null);

            if (agent.State != AgentState.Available)
                return new ReserveResult(ReserveOutcome.AgentNotAvailable, null);

            var transition = AgentStateMachine.Transition(agent, AgentState.Reserved, now);
            if (!transition.Allowed)
                return new ReserveResult(ReserveOutcome.AgentNotAvailable, null);

            var reservation = Reservation.Issue(callId, agentId, now, ttl);
            agent.SetReservation(reservation);
            return new ReserveResult(ReserveOutcome.Reserved, reservation);
        }
    }

    /// <summary>
    /// Server-side validation of the token the agent presents when answering (NFR-SEC3). Without
    /// this, "an agent cannot answer a call they were not offered" is a UI convention, not a rule.
    /// </summary>
    public bool ValidateReservation(Guid agentId, Guid callId, string token, DateTimeOffset now)
    {
        lock (_reservationLock)
        {
            if (!_agents.TryGetValue(agentId, out var agent)) return false;
            var reservation = agent.Reservation;
            return reservation is not null
                   && reservation.CallId == callId
                   && reservation.AgentId == agentId
                   && !reservation.IsExpired(now)
                   && string.Equals(reservation.Token, token, StringComparison.Ordinal);
        }
    }

    public IReadOnlyList<Agent> ExpiredReservations(DateTimeOffset now)
    {
        lock (_reservationLock)
        {
            return _agents.Values
                .Where(a => a.Reservation is not null && a.Reservation.IsExpired(now))
                .ToArray();
        }
    }

    public void ClearReservation(Guid agentId)
    {
        lock (_reservationLock)
        {
            if (_agents.TryGetValue(agentId, out var agent)) agent.ClearReservation();
        }
    }

    // ---------------- sessions ----------------

    public AgentSession StartSession(Guid agentId, string role, DateTimeOffset now)
    {
        // Single session per agent (FR-B5): a new login evicts the old one.
        foreach (var stale in _sessions.Values.Where(s => s.AgentId == agentId).ToArray())
            _sessions.TryRemove(stale.Token, out _);

        var session = new AgentSession(Guid.NewGuid().ToString("N"), agentId, role, now);
        _sessions[session.Token] = session;
        return session;
    }

    public AgentSession? GetSession(string? token) =>
        token is not null && _sessions.TryGetValue(token, out var s) ? s : null;

    public void EndSession(string token) => _sessions.TryRemove(token, out _);
}
