using CallCenter.Api.Contracts;
using CallCenter.Api.Data;
using CallCenter.Api.Security;
using CallCenter.Api.Services;
using CallCenter.Telephony;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Api.Controllers;

/// <summary>
/// Supervisor and reporting surface. Gated by role at the framework level — an agent holding a valid
/// session still cannot read the wallboard, the CDR or the audit log (FR-A2, NFR-SEC3).
/// </summary>
[Authorize(Roles = AgentSessionDefaults.SupervisorRole)]
[Tags("Supervisor & reporting")]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class SupervisorController(
    WallboardService wallboard,
    PlatformState state,
    InteractionEventStore events,
    SuppressionService suppression,
    IDbContextFactory<CallCenterDbContext> dbFactory) : ApiControllerBase
{
    /// <summary>
    /// Point-in-time wallboard. Live clients receive this pushed over SignalR at 1 Hz instead of
    /// polling; this endpoint exists for the initial paint and for scripted checks.
    /// </summary>
    [HttpGet("wallboard")]
    [ProducesResponseType<WallboardSnapshot>(StatusCodes.Status200OK)]
    public ActionResult<WallboardSnapshot> Wallboard() => Ok(wallboard.Snapshot());

    [HttpGet("calls/recent")]
    [ProducesResponseType<IEnumerable<CallView>>(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<CallView>> RecentCalls([FromQuery] int take = 50) =>
        Ok(state.RecentCalls(Math.Clamp(take, 1, 200)).Select(CallView.Project));

    /// <summary>
    /// Reconstructs a call from its event stream — the answer to "what happened to call X?"
    /// (NFR-M4), and the same data every future AI feature reads (doc 6).
    /// </summary>
    [HttpGet("calls/{callId:guid}/trace")]
    [ProducesResponseType<IEnumerable<CallEventView>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<CallEventView>>> Trace(Guid callId, CancellationToken ct)
    {
        var trace = await events.TraceAsync(callId, ct);

        return trace.Count == 0
            ? NotFound(new ApiError("No events for that call."))
            : Ok(trace.Select(e => new CallEventView(e.Seq, e.Type, e.OccurredAt, e.CorrelationId, e.Payload)));
    }

    /// <summary>Call detail records (FR-H3).</summary>
    [HttpGet("reports/cdr")]
    [ProducesResponseType<IEnumerable<CallRecord>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<CallRecord>>> Cdr(
        [FromQuery] string? queue,
        [FromQuery] int take,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.Calls.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(queue)) query = query.Where(c => c.QueueName == queue);

        var rows = await query
            .OrderByDescending(c => c.InitiatedAt)
            .Take(take <= 0 ? 100 : Math.Min(take, 1000))
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpGet("suppressions")]
    [ProducesResponseType<IEnumerable<SuppressionEntry>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SuppressionEntry>>> Suppressions(CancellationToken ct) =>
        Ok(await suppression.ListAsync(ct));

    [HttpPost("suppressions")]
    [ProducesResponseType<SuppressionAddedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddSuppression([FromBody] SuppressionRequest request, CancellationToken ct)
    {
        var e164 = PhoneNumber.ToE164(request.Number);
        if (!PhoneNumber.IsValid(e164)) return BadRequest(new ApiError("Invalid number."));

        await suppression.AddAsync(e164, request.Source ?? "Internal", ct);
        await events.AuditAsync(CurrentAgentName, "SuppressionAdded", PhoneNumber.Mask(e164), request.Source, ct);

        return Ok(new SuppressionAddedResponse(e164));
    }

    /// <summary>Append-only audit trail: DNC blocks, suppression edits, recording access (NFR-SEC5).</summary>
    [HttpGet("audit")]
    [ProducesResponseType<IEnumerable<AuditEntry>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<AuditEntry>>> Audit(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return Ok(await db.AuditLog.AsNoTracking()
            .OrderByDescending(a => a.OccurredAt)
            .Take(100)
            .ToListAsync(ct));
    }
}
