using Microsoft.EntityFrameworkCore;

namespace CallCenter.Api.Data;

/// <summary>
/// The durable journal, on SQL Server. In production the time-series tables (<c>Calls</c> and
/// <c>CallEvents</c>) are partitioned monthly on a partition scheme, so retention is a
/// <c>SWITCH OUT</c> of one partition rather than a mass DELETE — see docs §4.7.2.
///
/// Note what is *not* here: agent presence, queue membership and reservations. Those change many
/// times per minute per agent and live in Redis in production (§4.7.3) — writing them to the OLTP
/// primary would create pointless write pressure on the same database the call path depends on.
/// </summary>
public sealed class CallCenterDbContext(DbContextOptions<CallCenterDbContext> options) : DbContext(options)
{
    public DbSet<CallRecord> Calls => Set<CallRecord>();
    public DbSet<CallEventRecord> CallEvents => Set<CallEventRecord>();
    public DbSet<AgentStateLogRecord> AgentStateLog => Set<AgentStateLogRecord>();
    public DbSet<SuppressionEntry> Suppressions => Set<SuppressionEntry>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CallRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired();
            // Every reporting query filters on time first — see §4.7.2.
            e.HasIndex(x => new { x.TenantId, x.InitiatedAt });
            e.HasIndex(x => new { x.QueueName, x.InitiatedAt });
            e.HasIndex(x => x.AgentId);
            e.HasIndex(x => x.FromNumber);
        });

        b.Entity<CallEventRecord>(e =>
        {
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.CallId, x.Seq });
            e.HasIndex(x => x.OccurredAt);
        });

        b.Entity<AgentStateLogRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.AgentId, x.StartedAt });
        });

        b.Entity<SuppressionEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.NumberE164 }).IsUnique();
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.OccurredAt);
        });

        ApplyStorageTypes(b);
    }

    /// <summary>
    /// SQL Server maps <see cref="DateTimeOffset"/> natively to <c>datetimeoffset</c>, which sorts
    /// and compares correctly — so no value converter is needed. We pin the precision instead:
    /// the default <c>datetimeoffset(7)</c> costs 10 bytes per column, and 100 ns resolution is
    /// meaningless for call timings. <c>datetimeoffset(3)</c> is 8 bytes and still millisecond
    /// accurate, which matters across millions of rows on the two time-series tables.
    ///
    /// JSON payloads use <c>nvarchar(max)</c>. On SQL Server 2025 the native <c>json</c> type is
    /// the better choice; it is left as nvarchar here so the prototype also runs against 2019/2022.
    /// </summary>
    private static void ApplyStorageTypes(ModelBuilder b)
    {
        foreach (var property in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                property.SetColumnType("datetimeoffset(3)");
        }
    }
}
