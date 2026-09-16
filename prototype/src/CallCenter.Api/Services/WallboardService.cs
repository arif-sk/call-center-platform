using CallCenter.Api.Realtime;
using CallCenter.Domain;
using Microsoft.AspNetCore.SignalR;

namespace CallCenter.Api.Services;

public sealed record QueueStats(
    string Name,
    string DisplayName,
    int Waiting,
    int LongestWaitSeconds,
    int SlaThresholdSeconds,
    double ServiceLevelPct,
    int OfferedToday,
    int AnsweredToday,
    int AbandonedToday,
    int AverageWaitSeconds);

public sealed record AgentSnapshot(
    Guid Id, string Name, string Team, string State, string? Reason,
    int SecondsInState, int CallsHandled, string[] Skills);

public sealed record WallboardSnapshot(
    DateTimeOffset At,
    IReadOnlyList<QueueStats> Queues,
    IReadOnlyList<AgentSnapshot> Agents,
    IReadOnlyDictionary<string, int> AgentStateCounts,
    int LiveCalls,
    bool CrmCircuitOpen,
    int PendingCrmWriteBacks,
    string Provider);

/// <summary>
/// Live wallboard numbers (FR-H1).
///
/// Two design points from the docs are visible here:
/// <list type="bullet">
/// <item><b>Aggregated server-side and pushed at 1 Hz</b>, not forwarded per event. The naive design
/// is ~25,000 messages/second at 500 agents, carrying data no human can read
/// (docs/05-scalability-plan.md §5.2).</item>
/// <item><b>Live numbers are approximate; historical numbers are exact.</b> Live figures come from
/// counters, reports come from the event stream. Saying so up front prevents the classic
/// "the wallboard says 47 and the report says 46" trust incident (R-13).</item>
/// </list>
/// </summary>
public sealed class WallboardService(PlatformState state, CrmClient crm, Telephony.ITelephonyProvider telephony)
{
    public WallboardSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var calls = state.Calls;

        var queues = state.Queues.Select(q =>
        {
            var queueCalls = calls.Where(c => c.QueueName == q.Name).ToArray();
            var answered = queueCalls.Where(c => c.AnsweredAt is not null).ToArray();
            var withinSla = answered.Count(c => c.WaitSeconds <= q.SlaThresholdSeconds);

            // Short abandons are excluded from service level by convention (FR-C8, Q22).
            var abandoned = queueCalls.Count(c => c.Status == CallStatus.Abandoned && c.WaitSeconds >= 5);

            var waitingCalls = state.PeekOrdered(q.Id);
            var longestWait = waitingCalls.Count == 0 ? 0 : waitingCalls.Max(w => w.WaitSeconds(now));

            return new QueueStats(
                q.Name,
                q.DisplayName,
                waitingCalls.Count,
                longestWait,
                q.SlaThresholdSeconds,
                answered.Length == 0 ? 100 : Math.Round(withinSla * 100.0 / answered.Length, 1),
                queueCalls.Length,
                answered.Length,
                abandoned,
                answered.Length == 0 ? 0 : (int)answered.Average(c => c.WaitSeconds));
        }).ToArray();

        var agents = state.Agents
            .Where(a => a.TakesCalls)
            .OrderBy(a => a.DisplayName)
            .Select(a => new AgentSnapshot(
                a.Id,
                a.DisplayName,
                a.TeamName,
                a.State.ToString(),
                a.NotReadyReason,
                a.StateSince == DateTimeOffset.MinValue ? 0 : (int)(now - a.StateSince).TotalSeconds,
                a.CallsHandledToday,
                a.Skills.Keys.OrderBy(s => s).ToArray()))
            .ToArray();

        var counts = agents
            .GroupBy(a => a.State)
            .ToDictionary(g => g.Key, g => g.Count());

        return new WallboardSnapshot(
            now,
            queues,
            agents,
            counts,
            calls.Count(c => c.IsLive),
            crm.CircuitOpen,
            crm.PendingWriteBacks,
            telephony.Name);
    }
}

/// <summary>Pushes the wallboard at a fixed 1 Hz to the supervisor group only.</summary>
public sealed class WallboardBroadcaster(
    WallboardService wallboard,
    IHubContext<CallCenterHub> hub,
    PlatformState state,
    ILogger<WallboardBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await hub.Clients.Group(CallCenterHub.SupervisorGroup)
                    .SendAsync("wallboard", wallboard.Snapshot(), stoppingToken);

                state.TrimCalls();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Wallboard broadcast failed.");
            }
        }
    }
}

/// <summary>
/// Drains queued CRM activity write-backs (FR-F3). Separated from the call path on purpose: a CRM
/// outage delays these, it never blocks a call and never loses one (§4.3.5).
/// </summary>
public sealed class CrmWriteBackWorker(CrmClient crm, ILogger<CrmWriteBackWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (crm.CircuitOpen) continue;

            while (crm.TryDrainOne(out var callId, out var summary))
            {
                logger.LogInformation("CRM activity written back for call={CallId}: {Summary}", callId, summary);
            }
        }
    }
}
