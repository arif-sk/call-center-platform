using System.Security.Claims;
using CallCenter.Api.Security;
using CallCenter.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace CallCenter.Api.Realtime;

/// <summary>
/// Push, not polling (NFR-P5). At 500 agents polling would be self-inflicted load; at 50 it would
/// simply be slow. In production this runs with a Redis backplane so any instance can push to any
/// client (docs/04-system-design.md §4.3.4).
///
/// The hub shares the API's authentication scheme, so identity arrives as claims — the same
/// <c>[Authorize]</c> pipeline rejects an unauthenticated negotiate before any hub code runs, and
/// there is no token parsing here at all.
///
/// Group discipline matters more than it looks: supervisor statistics are aggregated and pushed at
/// 1 Hz per queue rather than forwarded per event. The naive design is ~25,000 messages/second at
/// 500 agents, carrying data no human can read (docs/05-scalability-plan.md §5.2).
/// </summary>
[Authorize]
public sealed class CallCenterHub(PlatformState state) : Hub
{
    public const string SupervisorGroup = "supervisors";

    public static string AgentGroup(Guid agentId) => $"agent:{agentId:N}";

    private Guid AgentId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, AgentGroup(AgentId));

        if (Context.User!.IsInRole(AgentSessionDefaults.SupervisorRole))
            await Groups.AddToGroupAsync(Context.ConnectionId, SupervisorGroup);

        state.GetAgent(AgentId)?.Heartbeat(DateTimeOffset.UtcNow);

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Liveness (FR-B4). A browser that closed, a laptop that slept or a network that died leaves an
    /// agent looking Available while silently swallowing every call routed to them — in my
    /// experience the number-one cause of "the queue is backing up and nobody knows why".
    /// </summary>
    public Task Heartbeat()
    {
        state.GetAgent(AgentId)?.Heartbeat(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reconnection reconciles rather than resets (§4.3.3). An agent who refreshes the browser
    /// mid-call gets their call back, not a blank screen.
    /// </summary>
    public object Reconcile()
    {
        var agent = state.GetAgent(AgentId);
        if (agent is null) return new { authenticated = false };

        var call = agent.CurrentCallId is { } id ? state.GetCall(id) : null;

        return new
        {
            authenticated = true,
            state = agent.State.ToString(),
            reason = agent.NotReadyReason,
            call = call is null ? null : new
            {
                id = call.Id,
                status = call.Status.ToString(),
                from = call.FromNumber,
                to = call.ToNumber,
                contact = call.ContactDisplayName,
                onHold = call.OnHold,
                answeredAt = call.AnsweredAt
            }
        };
    }
}
