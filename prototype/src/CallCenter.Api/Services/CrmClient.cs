using System.Collections.Concurrent;
using System.Diagnostics;

namespace CallCenter.Api.Services;

public sealed record CrmContact(string ExternalId, string DisplayName, string Company, string Tier, string LastInteraction);

public enum ScreenPopOutcome { Found, Unknown, Ambiguous, Unavailable }

public sealed record ScreenPopResult(ScreenPopOutcome Outcome, CrmContact? Contact, IReadOnlyList<CrmContact> Candidates, int ElapsedMs)
{
    public static ScreenPopResult Unavailable(int ms) => new(ScreenPopOutcome.Unavailable, null, [], ms);
}

public sealed class CrmOptions
{
    /// <summary>Hard timeout on the lookup. The call is offered to the agent regardless (FR-F5).</summary>
    public int TimeoutMs { get; set; } = 800;

    /// <summary>Simulated CRM latency, so the timeout path is demonstrable.</summary>
    public int SimulatedLatencyMs { get; set; } = 120;

    /// <summary>Demo toggle: make the CRM fail, and watch calls keep flowing.</summary>
    public bool ForceFailure { get; set; }

    public int CircuitBreakerThreshold { get; set; } = 5;
    public int CircuitBreakerCooldownSeconds { get; set; } = 30;
    public int CacheTtlSeconds { get; set; } = 900;
}

/// <summary>
/// The anti-corruption layer in front of the CRM (docs/04-system-design.md §4.3.5).
///
/// The design rule this class exists to enforce: <b>the CRM is not allowed to take the phones
/// down.</b> A hard timeout, a circuit breaker and a read-through cache mean a CRM outage degrades
/// the screen-pop to "unavailable" — it never delays ringing and never drops a call (FR-F5).
///
/// The in-memory store here stands in for a real CRM; swapping it for an HTTP client changes
/// nothing above this class.
/// </summary>
public sealed class CrmClient(CrmOptions options, ILogger<CrmClient> logger)
{
    private readonly ConcurrentDictionary<string, List<CrmContact>> _directory = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (ScreenPopResult Result, DateTimeOffset CachedAt)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(Guid CallId, string Summary)> _writeBackQueue = new();

    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    public CrmOptions Options => options;

    public bool CircuitOpen => DateTimeOffset.UtcNow < _circuitOpenUntil;

    public int PendingWriteBacks => _writeBackQueue.Count;

    public void Seed(string numberE164, CrmContact contact) =>
        _directory.AddOrUpdate(numberE164, _ => [contact], (_, list) => { list.Add(contact); return list; });

    /// <summary>Reverse lookup by ANI → screen-pop (FR-F1). Never throws; always answers in time.</summary>
    public async Task<ScreenPopResult> LookupByNumberAsync(string numberE164, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (_cache.TryGetValue(numberE164, out var cached)
            && DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(options.CacheTtlSeconds))
        {
            return cached.Result with { ElapsedMs = (int)sw.ElapsedMilliseconds };
        }

        // Circuit open: answer instantly rather than adding latency to every single call.
        if (CircuitOpen)
        {
            logger.LogDebug("CRM circuit open — serving degraded screen-pop for {Number}", numberE164);
            return ScreenPopResult.Unavailable((int)sw.ElapsedMilliseconds);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.TimeoutMs);

            var result = await QueryAsync(numberE164, timeout.Token);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            var final = result with { ElapsedMs = (int)sw.ElapsedMilliseconds };
            _cache[numberE164] = (final, DateTimeOffset.UtcNow);
            return final;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (failures >= options.CircuitBreakerThreshold)
            {
                _circuitOpenUntil = DateTimeOffset.UtcNow.AddSeconds(options.CircuitBreakerCooldownSeconds);
                logger.LogWarning("CRM circuit breaker OPEN after {Failures} failures — screen-pop degraded until {Until}",
                    failures, _circuitOpenUntil);
            }

            // Degrade, never fail. The agent's phone is already ringing.
            return ScreenPopResult.Unavailable((int)sw.ElapsedMilliseconds);
        }
    }

    private async Task<ScreenPopResult> QueryAsync(string numberE164, CancellationToken ct)
    {
        await Task.Delay(options.SimulatedLatencyMs, ct);

        if (options.ForceFailure)
            throw new InvalidOperationException("Simulated CRM outage.");

        if (!_directory.TryGetValue(numberE164, out var matches) || matches.Count == 0)
            return new ScreenPopResult(ScreenPopOutcome.Unknown, null, [], 0);

        // One number matching several contacts is common and must never be guessed (FR-F4).
        return matches.Count == 1
            ? new ScreenPopResult(ScreenPopOutcome.Found, matches[0], [], 0)
            : new ScreenPopResult(ScreenPopOutcome.Ambiguous, null, matches.ToArray(), 0);
    }

    /// <summary>
    /// Activity write-back (FR-F3). Asynchronous, durable and idempotent by CallId: a CRM outage
    /// delays activity records, it never blocks a call and never loses one. In production this is a
    /// bus message consumed by a worker with backoff and a dead-letter queue (§4.3.5).
    /// </summary>
    public void QueueActivityWriteBack(Guid callId, string summary) => _writeBackQueue.Enqueue((callId, summary));

    public bool TryDrainOne(out Guid callId, out string summary)
    {
        if (_writeBackQueue.TryDequeue(out var item))
        {
            (callId, summary) = item;
            return true;
        }
        callId = Guid.Empty;
        summary = "";
        return false;
    }
}
