using CallCenter.Api.Contracts;
using CallCenter.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

[Authorize]
[Route("api/v1/calls")]
[Tags("Calls")]
public sealed class CallsController(CallOrchestrator orchestrator) : ApiControllerBase
{
    /// <summary>
    /// Answers an offered call. Requires the reservation token issued with the offer, validated
    /// server-side before any telephony command is sent (NFR-SEC3).
    /// </summary>
    /// <remarks>
    /// A stale token — the reservation timed out and the call has already moved on — is a
    /// <b>409 Conflict</b>, not a 400 and not a 500. The distinction matters: the client shows
    /// "this call is no longer yours" instead of "something went wrong", and the attempt itself is
    /// written to the event stream as <c>AnswerRejected</c>.
    /// </remarks>
    [HttpPost("{callId:guid}/answer")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Answer(Guid callId, [FromBody] ReservationRequest request, CancellationToken ct)
    {
        var result = await orchestrator.AnswerAsync(CurrentAgentId, callId, request.ReservationToken, ct);

        return result.Success
            ? Ok(result.Data ?? new { ok = true })
            : Conflict(new ApiError(result.Error!));
    }

    /// <summary>Declines an offered call — takes the RNA path (FR-C7).</summary>
    [HttpPost("{callId:guid}/reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(Guid callId, [FromBody] ReservationRequest request, CancellationToken ct)
    {
        var result = await orchestrator.RejectAsync(CurrentAgentId, callId, request.ReservationToken, ct);

        return result.Success
            ? Ok(new { ok = true })
            : Conflict(new ApiError(result.Error!));
    }

    [HttpPost("{callId:guid}/hold")]
    [ProducesResponseType<HoldResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Hold(Guid callId, [FromBody] HoldRequest request, CancellationToken ct) =>
        Respond(await orchestrator.HoldAsync(CurrentAgentId, callId, request.Hold, ct));

    /// <summary>
    /// Sends DTMF to the far end. The digits are never written to the event stream or the logs —
    /// only the count is (NFR-SEC6).
    /// </summary>
    [HttpPost("{callId:guid}/dtmf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendDtmf(Guid callId, [FromBody] DtmfRequest request, CancellationToken ct) =>
        Respond(await orchestrator.SendDtmfAsync(CurrentAgentId, callId, request.Digits, ct));

    /// <summary>
    /// Pauses or resumes recording for card capture (FR-G3, PCI-DSS). The paused range is journalled
    /// so the gap can be proven to an auditor rather than asserted.
    /// </summary>
    [HttpPost("{callId:guid}/recording")]
    [ProducesResponseType<RecordingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetRecording(Guid callId, [FromBody] RecordingRequest request, CancellationToken ct) =>
        Respond(await orchestrator.SetRecordingAsync(CurrentAgentId, callId, request.Paused, ct));

    [HttpPost("{callId:guid}/transfer")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Transfer(Guid callId, [FromBody] TransferRequest request, CancellationToken ct)
    {
        if (request.ToNumber is null && request.ToAgentId is null)
            return BadRequest(new ApiError("A transfer needs either a number or an agent."));

        return Respond(await orchestrator.TransferAsync(CurrentAgentId, callId, request.ToNumber, request.ToAgentId, ct));
    }

    [HttpPost("{callId:guid}/hangup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Hangup(Guid callId, CancellationToken ct) =>
        Respond(await orchestrator.HangupAsync(CurrentAgentId, callId, ct));

    /// <summary>Submits wrap-up and returns the agent to Available. Disposition is mandatory (Q21).</summary>
    [HttpPost("{callId:guid}/disposition")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitDisposition(Guid callId, [FromBody] DispositionRequest request, CancellationToken ct) =>
        Respond(await orchestrator.SubmitDispositionAsync(CurrentAgentId, callId, request.Code, request.Notes, ct));

    /// <summary>
    /// Click-to-call. Every attempt passes a synchronous do-not-call gate that fails closed
    /// (FR-D4, R-07).
    /// </summary>
    /// <remarks>
    /// A suppressed number returns <b>403 Forbidden</b> rather than 400: it is a policy refusal, not
    /// a malformed request, and the block is written to the audit log.
    /// </remarks>
    [HttpPost("outbound")]
    [ProducesResponseType<OutboundCallResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PlaceOutbound([FromBody] OutboundCallRequest request, CancellationToken ct)
    {
        var result = await orchestrator.PlaceOutboundAsync(CurrentAgentId, request.To, OutboundCallerId, ct);

        return result.Success
            ? Ok(result.Data)
            : StatusCode(StatusCodes.Status403Forbidden, new ApiError(result.Error!));
    }

    /// <summary>
    /// Presentation number for outbound calls. Per team/campaign and restricted to numbers we own
    /// in production (FR-D3); fixed here.
    /// </summary>
    private const string OutboundCallerId = "+442045550200";
}
