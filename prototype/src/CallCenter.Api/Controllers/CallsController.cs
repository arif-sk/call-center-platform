using CallCenter.Api.Models;
using CallCenter.Application.Contracts;
using CallCenter.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

/// <summary>
/// Call handling: everything an agent does to a call, and the two events that stand in for the
/// telephone network.
///
/// The agent id travels in the route because there is no sign-in token in this prototype. The
/// server still refuses any command for a call that was not offered to that agent, so the rule
/// holds even though the identity does not — that check belongs in the domain either way.
/// </summary>
[ApiController]
[Route("api/calls")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
public class CallsController : ControllerBase
{
    private readonly ICallCenterService _callCenterService;
    private readonly ILogger<CallsController> _logger;

    public CallsController(ICallCenterService callCenterService, ILogger<CallsController> logger)
    {
        _callCenterService = callCenterService;
        _logger = logger;
    }

    /// <summary>
    /// A customer calls in. This stands in for the telephone network: in production it is the
    /// carrier's webhook, and it is the only action in this controller that would change.
    /// </summary>
    [HttpPost("inbound", Name = nameof(ReceiveInboundCall))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReceiveInboundCall(
        [FromBody] InboundCallRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Inbound call from {From}.", request.From);

        var snapshot = await _callCenterService.ReceiveInboundCallAsync(
            request.From ?? string.Empty,
            request.To ?? string.Empty,
            cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>The agent picked up. Refused unless the call is still ringing on their desk.</summary>
    [HttpPost("{callId:guid}/answer/{agentId:guid}", Name = nameof(Answer))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<IActionResult> Answer(
        Guid callId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent {AgentId} answered call {CallId}.", agentId, callId);

        var snapshot = await _callCenterService.AnswerAsync(agentId, callId, cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>
    /// The agent did not pick up. The call goes back into the queue and the agent is made
    /// not-ready, so the platform does not immediately offer them the same call again.
    /// </summary>
    [HttpPost("{callId:guid}/decline/{agentId:guid}", Name = nameof(Decline))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<IActionResult> Decline(
        Guid callId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent {AgentId} declined call {CallId}.", agentId, callId);

        var snapshot = await _callCenterService.DeclineAsync(agentId, callId, cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>Ends the conversation and moves the agent into wrap-up.</summary>
    [HttpPost("{callId:guid}/hang-up/{agentId:guid}", Name = nameof(HangUp))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<IActionResult> HangUp(
        Guid callId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent {AgentId} hung up call {CallId}.", agentId, callId);

        var snapshot = await _callCenterService.HangUpAsync(agentId, callId, cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>
    /// Files the call and returns the agent to the queue. Refused without a disposition —
    /// wrap-up is part of the call, not an afterthought.
    /// </summary>
    [HttpPost("{callId:guid}/wrap-up/{agentId:guid}", Name = nameof(CompleteWrapUp))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<IActionResult> CompleteWrapUp(
        Guid callId,
        Guid agentId,
        [FromBody] WrapUpRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Agent {AgentId} filed call {CallId} as {Disposition}.", agentId, callId, request.Disposition);

        var snapshot = await _callCenterService.CompleteWrapUpAsync(
            agentId, callId, request.Disposition, request.Notes, cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>
    /// The waiting caller hung up before anyone answered. Driven by a button for the same reason
    /// as the inbound action above.
    /// </summary>
    [HttpPost("{callId:guid}/abandon", Name = nameof(Abandon))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<IActionResult> Abandon(Guid callId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Caller abandoned call {CallId}.", callId);

        var snapshot = await _callCenterService.AbandonAsync(callId, cancellationToken);

        return Ok(snapshot);
    }
}
