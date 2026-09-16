using CallCenter.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Api.Services;

public sealed record SuppressionCheck(bool Blocked, string? Reason, string? Source);

/// <summary>
/// Do-not-call / suppression checking (FR-D4, risk R-07).
///
/// Two properties make this a control rather than a report:
/// <list type="number">
/// <item>It is a <b>synchronous gate in the outbound call path</b> — the call is not placed until
/// this returns.</item>
/// <item>It <b>fails closed</b>. If the lookup errors, the call is blocked. A blocked legitimate
/// call is an inconvenience; a call to a suppressed number is a regulatory breach.</item>
/// </list>
/// Failing closed is a deliberate inversion of the CRM rule above — the difference is that one
/// protects convenience and the other protects legality.
/// </summary>
public sealed class SuppressionService(
    IDbContextFactory<CallCenterDbContext> dbFactory,
    PlatformState state,
    ILogger<SuppressionService> logger)
{
    public async Task<SuppressionCheck> CheckAsync(string numberE164, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;

            var entry = await db.Suppressions
                .Where(s => s.TenantId == state.TenantId && s.NumberE164 == numberE164)
                .Where(s => s.ExpiresAt == null || s.ExpiresAt > now)
                .FirstOrDefaultAsync(ct);

            return entry is null
                ? new SuppressionCheck(false, null, null)
                : new SuppressionCheck(true, "Number is on the do-not-call list.", entry.Source);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Suppression check failed for {Number} — failing closed.",
                Telephony.PhoneNumber.Mask(numberE164));
            return new SuppressionCheck(true, "Suppression list unavailable; outbound blocked.", "FailClosed");
        }
    }

    public async Task AddAsync(string numberE164, string source, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Suppressions.AnyAsync(s => s.TenantId == state.TenantId && s.NumberE164 == numberE164, ct))
            return;

        db.Suppressions.Add(new SuppressionEntry
        {
            Id = Guid.NewGuid(),
            TenantId = state.TenantId,
            NumberE164 = numberE164,
            Source = source,
            AddedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SuppressionEntry>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Suppressions
            .Where(s => s.TenantId == state.TenantId)
            .OrderBy(s => s.NumberE164)
            .ToListAsync(ct);
    }
}
