using CallCenter.Api.Contracts;
using CallCenter.Api.Security;
using CallCenter.Api.Services;
using CallCenter.Domain;
using CallCenter.Telephony;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

[AllowAnonymous]
[Tags("Session")]
public sealed class SessionController(
    PlatformState state,
    DidRoutingTable dids,
    ITelephonyProvider telephony) : ApiControllerBase
{
    /// <summary>
    /// Starts an agent session. Demo stand-in for OIDC authorization-code + PKCE (NFR-SEC2).
    /// What matters architecturally is everything downstream: the session is server-held, there is
    /// exactly one per agent, and every request re-validates it through the auth scheme.
    /// </summary>
    [HttpPost("session")]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public ActionResult<SessionResponse> Login([FromBody] LoginRequest request)
    {
        var agent = state.GetAgent(request.AgentId);
        if (agent is null) return NotFound(new ApiError("Unknown agent."));

        var role = request.Role switch
        {
            AgentSessionDefaults.SupervisorRole => AgentSessionDefaults.SupervisorRole,
            AgentSessionDefaults.AdminRole => AgentSessionDefaults.AdminRole,
            _ => AgentSessionDefaults.AgentRole
        };

        // Single session per agent (FR-B5): this evicts any previous one.
        var session = state.StartSession(agent.Id, role, DateTimeOffset.UtcNow);
        agent.StartSession(session.Token, DateTimeOffset.UtcNow);

        return Ok(new SessionResponse(session.Token, session.Role, AgentProfile.Project(agent)));
    }

    [HttpDelete("session")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Logout()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            state.EndSession(header[7..].Trim());

        return NoContent();
    }

    /// <summary>Everything a client needs to render itself, in one round trip.</summary>
    [HttpGet("bootstrap")]
    [ProducesResponseType<BootstrapResponse>(StatusCodes.Status200OK)]
    public ActionResult<BootstrapResponse> Bootstrap() =>
        Ok(new BootstrapResponse(
            telephony.Name,
            DemoSeeder.SupervisorAgentId,
            state.Agents.Where(a => a.TakesCalls).OrderBy(a => a.DisplayName).Select(AgentProfile.Project).ToArray(),
            state.Queues.OrderBy(q => q.Name).Select(QueueSummary.Project).ToArray(),
            dids.All,
            DemoSeeder.Dispositions,
            NotReadyReasons.AgentSelectable));
}
