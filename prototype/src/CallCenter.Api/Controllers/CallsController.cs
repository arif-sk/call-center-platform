using CallCenter.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

public sealed record InboundCallRequest(string? From, string? To);

public sealed record WrapUpRequest(string Disposition, string? Notes);

/// <summary>
/// Call handling.
///
/// The agent id travels in the route because there is no sign-in token in this prototype. The
/// server still refuses any command for a call that was not offered to that agent, so the rule
/// holds even though the identity does not — that check belongs in the domain either way.
/// </summary>
[Route("api/calls")]
public sealed class CallsController(CallCenterService service) : CallCenterControllerBase(service)
{
    /// <summary>
    /// Stands in for the telephone network. In production this is the carrier's webhook, and it is
    /// the only action here that would change.
    /// </summary>
    [HttpPost("inbound", Name = nameof(ReceiveInboundCall))]
    public Task<ActionResult<Snapshot>> ReceiveInboundCall(InboundCallRequest request, CancellationToken ct) =>
        RunAsync(() => Service.ReceiveInboundCallAsync(request.From ?? "", request.To ?? "", ct));

    [HttpPost("{callId:guid}/answer/{agentId:guid}", Name = nameof(Answer))]
    public Task<ActionResult<Snapshot>> Answer(Guid callId, Guid agentId, CancellationToken ct) =>
        RunAsync(() => Service.AnswerAsync(agentId, callId, ct));

    /// <summary>The agent did not pick up: the call is re-queued and they are made not-ready.</summary>
    [HttpPost("{callId:guid}/decline/{agentId:guid}", Name = nameof(Decline))]
    public Task<ActionResult<Snapshot>> Decline(Guid callId, Guid agentId, CancellationToken ct) =>
        RunAsync(() => Service.DeclineAsync(agentId, callId, ct));

    [HttpPost("{callId:guid}/hang-up/{agentId:guid}", Name = nameof(HangUp))]
    public Task<ActionResult<Snapshot>> HangUp(Guid callId, Guid agentId, CancellationToken ct) =>
        RunAsync(() => Service.HangUpAsync(agentId, callId, ct));

    /// <summary>Files the call. Refused without a disposition — wrap-up is part of the call.</summary>
    [HttpPost("{callId:guid}/wrap-up/{agentId:guid}", Name = nameof(CompleteWrapUp))]
    public Task<ActionResult<Snapshot>> CompleteWrapUp(
        Guid callId, Guid agentId, WrapUpRequest request, CancellationToken ct) =>
        RunAsync(() => Service.CompleteWrapUpAsync(agentId, callId, request.Disposition, request.Notes, ct));

    /// <summary>The waiting caller hung up. Driven by a button, for the same reason as inbound.</summary>
    [HttpPost("{callId:guid}/abandon", Name = nameof(Abandon))]
    public Task<ActionResult<Snapshot>> Abandon(Guid callId, CancellationToken ct) =>
        RunAsync(() => Service.AbandonAsync(callId, ct));
}
