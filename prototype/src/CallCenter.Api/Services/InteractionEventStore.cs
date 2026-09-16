using System.Text.Json;
using CallCenter.Api.Data;
using CallCenter.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Api.Services;

/// <summary>
/// The append-only interaction event stream (FR-I1).
///
/// This is the highest-leverage component in the design and the one most often bolted on too late.
/// Reporting, audit, "what happened to call X?" support questions and every future AI feature all
/// read from here (docs/06-ai-readiness.md §6.2). Building it for reporting means it pays for
/// itself before any AI exists.
///
/// In production the write goes through a transactional outbox so the state change and the event
/// commit atomically, and a dispatcher publishes to the bus (§4.8).
/// </summary>
public sealed class InteractionEventStore(
    IDbContextFactory<CallCenterDbContext> dbFactory,
    IHubContext<CallCenterHub> hub,
    PlatformState state,
    ILogger<InteractionEventStore> logger)
{
    public async Task AppendAsync(
        Guid callId,
        string type,
        string correlationId,
        object? payload = null,
        CancellationToken ct = default)
    {
        var record = new CallEventRecord
        {
            CallId = callId,
            TenantId = state.TenantId,
            Type = type,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
            Payload = payload is null ? "{}" : JsonSerializer.Serialize(payload)
        };

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.CallEvents.Add(record);
        await db.SaveChangesAsync(ct);

        // Structured log correlated by CallId (NFR-M4) — the single most useful thing in production
        // support for this domain.
        logger.LogInformation("call={CallId} event={EventType} correlation={CorrelationId}",
            callId, type, correlationId);

        await hub.Clients.Group(CallCenterHub.SupervisorGroup)
            .SendAsync("callEvent", new
            {
                callId,
                type,
                occurredAt = record.OccurredAt,
                payload = record.Payload
            }, ct);
    }

    public async Task<IReadOnlyList<CallEventRecord>> TraceAsync(Guid callId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.CallEvents
            .Where(e => e.CallId == callId)
            .OrderBy(e => e.Seq)
            .ToListAsync(ct);
    }

    public async Task AuditAsync(string actor, string action, string target, string? detail = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.AuditLog.Add(new AuditEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Actor = actor,
            Action = action,
            Target = target,
            Detail = detail
        });
        await db.SaveChangesAsync(ct);
    }
}
