using Microsoft.EntityFrameworkCore;

namespace CallCenter.Api.Data;

public enum AgentState { Offline, Available, NotReady, Ringing, OnCall, WrapUp }

public enum CallStatus { Queued, Ringing, Connected, WrapUp, Completed, Abandoned }

/// <summary>An agent, and the one state they are in right now. The server owns this value.</summary>
public sealed class Agent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public bool IsSupervisor { get; set; }
    public AgentState State { get; set; }
    public Guid? CurrentCallId { get; set; }
    public DateTimeOffset StateChangedAt { get; set; }
}

/// <summary>One row per call. Durations are written on completion so reports never recompute them.</summary>
public sealed class Call
{
    public Guid Id { get; set; }
    public string FromNumber { get; set; } = "";
    public string ToNumber { get; set; } = "";
    public CallStatus Status { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public Guid? AgentId { get; set; }
    public string? AgentName { get; set; }
    public string? Disposition { get; set; }
    public string? Notes { get; set; }
    public int WaitSeconds { get; set; }
    public int TalkSeconds { get; set; }
}

public sealed class CallCenterDbContext(DbContextOptions<CallCenterDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Call> Calls => Set<Call>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Enums are stored as text. A support engineer reading this table at 2am should not have to
        // look up what status 3 means.
        b.Entity<Agent>().Property(a => a.State).HasConversion<string>().HasMaxLength(20);
        b.Entity<Call>().Property(c => c.Status).HasConversion<string>().HasMaxLength(20);

        // The queue is "oldest waiting call first", so that is the index the routing query needs.
        b.Entity<Call>().HasIndex(c => new { c.Status, c.QueuedAt });

        foreach (var property in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                property.SetColumnType("datetimeoffset(3)"); // milliseconds is plenty for call timing
        }
    }
}
