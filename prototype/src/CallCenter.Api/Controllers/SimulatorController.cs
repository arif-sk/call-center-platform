using CallCenter.Api.Contracts;
using CallCenter.Api.Services;
using CallCenter.Telephony;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

/// <summary>
/// Demo-only surface: it stands in for the carrier webhooks a real deployment would receive.
///
/// It is deliberately anonymous so the demo needs no ceremony, and it is <b>registered only when the
/// simulated provider is active</b> — see <c>Program.cs</c>. In a real deployment this controller
/// does not exist in the running application at all, which is the only safe way to ship an endpoint
/// that can conjure calls out of nothing.
/// </summary>
[AllowAnonymous]
[Route("api/v1/simulator")]
[Tags("Simulator (demo only)")]
public sealed class SimulatorController(
    ITelephonyProvider telephony,
    CrmClient crm) : ApiControllerBase
{
    /// <summary>Injects an inbound call, as a carrier webhook would.</summary>
    [HttpPost("inbound")]
    [ProducesResponseType<AcceptedCallResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Inbound([FromBody] SimulateInboundRequest request)
    {
        if (telephony is not SimulatedTelephonyProvider simulator)
            return BadRequest(new ApiError("The simulator is only available with the simulated provider."));

        var callId = Guid.NewGuid();
        await simulator.SimulateInboundAsync(callId, request.From, request.Did, request.PatienceSeconds);

        return Accepted($"/api/v1/calls/{callId}/trace", new AcceptedCallResponse(callId));
    }

    /// <summary>The caller hangs up.</summary>
    [HttpPost("hangup/{callId:guid}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public IActionResult Hangup(Guid callId)
    {
        if (telephony is not SimulatedTelephonyProvider simulator)
            return BadRequest(new ApiError("Simulator not active."));

        simulator.SimulateCallerHangup(callId);
        return Accepted();
    }

    /// <summary>
    /// Breaks the CRM on purpose. Calls keep routing and connecting; only the screen-pop degrades
    /// (FR-F5) — the rule the whole integration is designed around.
    /// </summary>
    [HttpPost("crm-outage")]
    [ProducesResponseType<CrmOutageResponse>(StatusCodes.Status200OK)]
    public ActionResult<CrmOutageResponse> CrmOutage([FromBody] CrmOutageRequest request)
    {
        crm.Options.ForceFailure = request.Enabled;

        return Ok(new CrmOutageResponse(
            request.Enabled,
            "Calls keep flowing; the screen-pop degrades and the circuit breaker opens (FR-F5)."));
    }
}
