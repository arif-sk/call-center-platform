using CallCenter.Api.Contracts;
using CallCenter.Api.Services;
using CallCenter.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

[Authorize]
[Tags("Agent")]
public sealed class AgentsController(
    PlatformState state,
    CallOrchestrator orchestrator) : ApiControllerBase
{
    /// <summary>
    /// Requests an agent state change. The client asks; the server decides (FR-B1). Transitions the
    /// agent is not allowed to make themselves — <c>OnCall</c>, <c>Reserved</c> — are refused here,
    /// not hidden in the UI.
    /// </summary>
    [HttpPut("agents/me/state")]
    [ProducesResponseType<AgentStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetState([FromBody] StateChangeRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<AgentState>(request.State, ignoreCase: true, out var requested))
            return BadRequest(new ApiError($"Unknown state '{request.State}'."));

        var result = await orchestrator.SetAgentStateAsync(CurrentAgentId, requested, request.Reason, ct);
        return Respond(result);
    }

    [HttpGet("agents/me")]
    [ProducesResponseType<AgentDetailResponse>(StatusCodes.Status200OK)]
    public ActionResult<AgentDetailResponse> Me()
    {
        var agent = state.GetAgent(CurrentAgentId);
        if (agent is null) return NotFound(new ApiError("Agent not found."));

        var call = agent.CurrentCallId is { } id ? state.GetCall(id) : null;

        return Ok(new AgentDetailResponse(
            agent.Id,
            agent.DisplayName,
            agent.State.ToString(),
            agent.NotReadyReason,
            agent.CallsHandledToday,
            agent.ConsecutiveRna,
            agent.Reservation?.Token,
            agent.Reservation?.ExpiresAt,
            call is null ? null : CallView.Project(call)));
    }
}
