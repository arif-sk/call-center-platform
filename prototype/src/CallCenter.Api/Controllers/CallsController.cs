using CallCenter.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

public sealed record InboundCallRequest(string? From, string? To);

public sealed record WrapUpRequest(string Disposition, string? Notes);

/// <summary>
/// Call handling. The agent id travels in the route rather than in a token because there is no
/// sign-in token in this prototype; the server still refuses any command for a call that was not
/// offered to that agent.
/// </summary>
[Route("api/calls")]
public sealed class CallsController(CallCenterService service) : CallCenterControllerBase
{
    /// <summary>
    /// Stands in for the telephone network. In production this is the carrier's webhook, and it is
    /// the only part of this controller that would change.
    /// </summary>
    [HttpPost("inbound")]
    public Task<ActionResult<Snapshot>> Inbound(InboundCallRequest request, CancellationToken ct) =>
        RunAsync(() => service.ReceiveInboundCallAsync(request.From ?? "", request.To ?? "", ct));

    [HttpPost("{callId:guid}/answer/{agentId:guid}")]
    public Task<ActionResult<Snapshot>> Answer(Guid callId, Guid agentId, CancellationToken ct) =>
        RunAsync(() => service.AnswerAsync(agentId, callId, ct));

    [HttpPost("{callId:guid}/decline/{agentId:guid}")]
    public Task<ActionResult<Snapshot>> Decline(Guid callId, Guid agentId, CancellationToken ct) =>
        RunAsync(() => service.DeclineAsync(agentId, callId, ct));

    [HttpPost("{callId:guid}/hang-up/{agentId:guid}")]
    public Task<ActionResult<Snapshot>> HangUp(Guid callId, Guid agentId, CancellationToken ct) =>
        RunAsync(() => service.HangUpAsync(agentId, callId, ct));

    [HttpPost("{callId:guid}/wrap-up/{agentId:guid}")]
    public Task<ActionResult<Snapshot>> WrapUp(
        Guid callId, Guid agentId, WrapUpRequest request, CancellationToken ct) =>
        RunAsync(() => service.CompleteWrapUpAsync(agentId, callId, request.Disposition, request.Notes, ct));

    /// <summary>The waiting caller hung up. Also driven by a button, for the same reason as inbound.</summary>
    [HttpPost("{callId:guid}/abandon")]
    public Task<ActionResult<Snapshot>> Abandon(Guid callId, CancellationToken ct) =>
        RunAsync(() => service.AbandonAsync(callId, ct));
}
