namespace CallCenter.Domain;

/// <summary>
/// Queue configuration. In production this is Ops-editable without a deployment (FR-A4).
/// </summary>
public sealed class QueueDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Skills an agent must hold to be eligible for this queue (FR-C4).</summary>
    public IReadOnlyList<string> RequiredSkills { get; init; } = [];

    /// <summary>Base priority; a rule may raise an individual call above it (FR-C5).</summary>
    public int BasePriority { get; init; } = 5;

    /// <summary>How long an agent's phone rings before we treat it as RNA (FR-C7).</summary>
    public int RingTimeoutSeconds { get; init; } = 15;

    /// <summary>Service-level threshold in seconds — the "20" in "80/20" (FR-H4).</summary>
    public int SlaThresholdSeconds { get; init; } = 20;

    public string SelectionPolicy { get; init; } = SelectionPolicies.LongestIdle;

    public bool RecordCalls { get; init; } = true;
}

public static class SelectionPolicies
{
    public const string LongestIdle = "LongestIdle";
    public const string HighestProficiency = "HighestProficiency";
    public const string LeastCallsToday = "LeastCallsToday";
}

/// <summary>
/// A call waiting in a queue. In production this is a Redis sorted set scored by
/// (priority, enqueuedAt) so ordering survives a routing-worker restart — see §4.7.3.
/// </summary>
public sealed record QueuedCall(
    Guid CallId,
    Guid QueueId,
    int Priority,
    DateTimeOffset EnqueuedAt,
    IReadOnlyList<string> RequiredSkills)
{
    public int WaitSeconds(DateTimeOffset now) => (int)(now - EnqueuedAt).TotalSeconds;
}
