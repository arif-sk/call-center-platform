namespace CallCenter.Api.Data;

/// <summary>
/// One row per call. Durations are denormalised on completion so reports never aggregate raw
/// events at query time (docs/04-system-design.md §4.7.2).
/// </summary>
public sealed class CallRecord
{
    public Guid Id { get; set; }

    /// <summary>Present from day one even though multi-tenancy is out of scope — retrofitting it
    /// into a live call table is the expensive part (docs/01-requirement-analysis.md §1.8).</summary>
    public Guid TenantId { get; set; }

    public string Direction { get; set; } = "";
    public string FromNumber { get; set; } = "";
    public string ToNumber { get; set; } = "";
    public string? QueueName { get; set; }
    public int Priority { get; set; }
    public string Status { get; set; } = "";

    public DateTimeOffset InitiatedAt { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public int WaitSeconds { get; set; }
    public int TalkSeconds { get; set; }
    public int WrapSeconds { get; set; }
    public int RnaCount { get; set; }

    public Guid? AgentId { get; set; }
    public string? AgentName { get; set; }
    public string? EndReason { get; set; }
    public string? Disposition { get; set; }
    public string? Notes { get; set; }

    /// <summary>CRM reference only. We never copy customer PII into this platform (§4.7.2).</summary>
    public string? ExternalContactId { get; set; }

    public string ProviderName { get; set; } = "";
    public string? ProviderCallId { get; set; }
    public bool Recorded { get; set; }

    /// <summary>PCI evidence: the ranges during which recording was paused (FR-G3).</summary>
    public string? RecordingPausedRanges { get; set; }
}

/// <summary>
/// Append-only. This is the system of record (FR-I1): the CDR above is a projection of it, and
/// every future AI feature reads it (docs/06-ai-readiness.md §6.2).
/// </summary>
public sealed class CallEventRecord
{
    public long Seq { get; set; }
    public Guid CallId { get; set; }
    public Guid TenantId { get; set; }
    public string Type { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public string CorrelationId { get; set; } = "";
    /// <summary>JSON. JSONB in PostgreSQL.</summary>
    public string Payload { get; set; } = "{}";
}

/// <summary>Durations per agent state — the source for occupancy and adherence reporting.</summary>
public sealed class AgentStateLogRecord
{
    public long Id { get; set; }
    public Guid AgentId { get; set; }
    public string AgentName { get; set; } = "";
    public string State { get; set; } = "";
    public string? ReasonCode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
}

/// <summary>Do-not-call / suppression list. Checked synchronously before every outbound attempt (FR-D4).</summary>
public sealed class SuppressionEntry
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string NumberE164 { get; set; } = "";
    public string Source { get; set; } = "Internal";
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>Append-only audit of the things auditors and incident reviews actually ask about.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Detail { get; set; }
}
