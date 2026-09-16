using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace CallCenter.Telephony;

public sealed class TwilioOptions
{
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.twilio.com";

    /// <summary>Public URL the carrier calls back on. Points at a stable gateway host, never at an
    /// environment-specific URL — see docs/07-deployment-strategy.md §7.7.</summary>
    public string WebhookBaseUrl { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountSid) && !string.IsNullOrWhiteSpace(AuthToken);
}

/// <summary>
/// The real adapter. It exists in the prototype for one reason: to prove the port in
/// <see cref="ITelephonyProvider"/> is a genuine boundary and not a single-implementation guess
/// (docs/03-mvp-definition.md §3.5, R-10).
///
/// Everything that does not require carrier credentials is implemented for real and unit-tested —
/// notably <see cref="VerifyWebhook"/>, which is the security control on our most exposed public
/// endpoint (NFR-SEC10). The control-API calls are real HTTP requests; they simply need an account
/// to talk to. Nothing above this class changes when the demo switches between providers.
/// </summary>
public sealed class TwilioTelephonyProvider : ITelephonyProvider
{
    private readonly HttpClient _http;
    private readonly TwilioOptions _options;
    private readonly HashSet<string> _seenSignatures = new(StringComparer.Ordinal);
    private readonly object _replayLock = new();

    public string Name => "twilio";

    public event Action<TelephonyEvent>? EventReceived;

    public TwilioTelephonyProvider(HttpClient http, TwilioOptions options)
    {
        _http = http;
        _options = options;

        if (_options.IsConfigured)
        {
            var basic = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.AccountSid}:{_options.AuthToken}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }
    }

    public async Task<CallHandle> PlaceCallAsync(PlaceCallRequest request, CancellationToken ct = default)
    {
        EnsureConfigured();

        var form = new Dictionary<string, string>
        {
            ["To"] = request.To,
            ["From"] = request.From,
            // The carrier fetches call-control instructions from us when the call connects.
            ["Url"] = $"{_options.WebhookBaseUrl}/api/v1/telephony/webhooks/twilio/voice?callId={request.CallId}",
            ["StatusCallback"] = $"{_options.WebhookBaseUrl}/api/v1/telephony/webhooks/twilio/status?callId={request.CallId}",
            ["StatusCallbackEvent"] = "initiated ringing answered completed",
            ["Record"] = request.Record ? "true" : "false"
        };

        using var response = await _http.PostAsync(
            $"{_options.BaseUrl}/2010-04-01/Accounts/{_options.AccountSid}/Calls.json",
            new FormUrlEncodedContent(form), ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var sid = ExtractJsonString(body, "sid") ?? throw new InvalidOperationException("No call SID returned.");

        return new CallHandle(sid, Name);
    }

    public Task<CallHandle> RingAgentAsync(Guid callId, Guid agentId, TimeSpan ringTimeout, CancellationToken ct = default)
        => PlaceCallAsync(new PlaceCallRequest(callId, _options.WebhookBaseUrl, $"client:agent-{agentId:N}", false), ct);

    public Task AnswerAsync(Guid callId, CancellationToken ct = default)
        // With a CPaaS, the browser SDK accepts the invite; nothing to do server-side.
        => Task.CompletedTask;

    public Task HangupAsync(Guid callId, string reason, CancellationToken ct = default)
        => UpdateCallAsync(callId, new Dictionary<string, string> { ["Status"] = "completed" }, ct);

    public Task HoldAsync(Guid callId, bool hold, CancellationToken ct = default)
        => UpdateCallAsync(callId, new Dictionary<string, string>
        {
            ["Url"] = $"{_options.WebhookBaseUrl}/api/v1/telephony/webhooks/twilio/{(hold ? "hold" : "retrieve")}?callId={callId}",
            ["Method"] = "POST"
        }, ct);

    public Task SendDtmfAsync(Guid callId, string digits, CancellationToken ct = default)
        => UpdateCallAsync(callId, new Dictionary<string, string>
        {
            ["Url"] = $"{_options.WebhookBaseUrl}/api/v1/telephony/webhooks/twilio/dtmf?callId={callId}&digits={Uri.EscapeDataString(digits)}",
            ["Method"] = "POST"
        }, ct);

    public Task SetRecordingAsync(Guid callId, RecordingCommand command, CancellationToken ct = default)
    {
        EnsureConfigured();
        var status = command switch
        {
            RecordingCommand.Start => "in-progress",
            RecordingCommand.Pause => "paused",
            RecordingCommand.Resume => "in-progress",
            RecordingCommand.Stop => "stopped",
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };

        return _http.PostAsync(
            $"{_options.BaseUrl}/2010-04-01/Accounts/{_options.AccountSid}/Calls/{callId}/Recordings/Twilio.json",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Status"] = status }), ct);
    }

    public Task TransferAsync(Guid callId, TransferTarget target, TransferMode mode, CancellationToken ct = default)
    {
        var destination = target.Number
            ?? (target.AgentId is { } a ? $"client:agent-{a:N}" : null)
            ?? throw new ArgumentException("Transfer target must be a number or an agent.", nameof(target));

        return UpdateCallAsync(callId, new Dictionary<string, string>
        {
            ["Url"] = $"{_options.WebhookBaseUrl}/api/v1/telephony/webhooks/twilio/transfer" +
                      $"?callId={callId}&to={Uri.EscapeDataString(destination)}&mode={mode}",
            ["Method"] = "POST"
        }, ct);
    }

    /// <summary>
    /// Real signature verification (NFR-SEC10). The algorithm: HMAC-SHA1, keyed with the account
    /// auth token, over the full request URL followed by every POST parameter sorted by name and
    /// concatenated as key+value, then base64-encoded.
    ///
    /// Replay protection is layered on top: a signature is accepted once. In production this cache
    /// is Redis with a TTL, not a local set — but the control belongs at this boundary either way.
    /// </summary>
    public bool VerifyWebhook(string url, IReadOnlyDictionary<string, string> form, string signature)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(_options.AuthToken))
            return false;

        var builder = new StringBuilder(url);
        foreach (var key in form.Keys.OrderBy(k => k, StringComparer.Ordinal))
            builder.Append(key).Append(form[key]);

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(_options.AuthToken));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));

        // Constant-time compare — a timing oracle on a public endpoint is a real finding.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature)))
            return false;

        lock (_replayLock)
        {
            return _seenSignatures.Add(signature);
        }
    }

    /// <summary>
    /// Vendor payload → canonical domain events. Nothing above this method sees a Twilio field name.
    /// </summary>
    public IReadOnlyList<TelephonyEvent> ParseWebhook(Guid callId, IReadOnlyDictionary<string, string> form)
    {
        var status = form.GetValueOrDefault("CallStatus", "").ToLowerInvariant();

        var type = status switch
        {
            "ringing" => TelephonyEventTypes.AgentRinging,
            "in-progress" => TelephonyEventTypes.CallAnswered,
            "completed" => TelephonyEventTypes.CallEnded,
            "no-answer" => TelephonyEventTypes.OutboundNoAnswer,
            "busy" or "failed" or "canceled" => TelephonyEventTypes.CallEnded,
            _ => ""
        };

        if (type.Length == 0) return [];

        var e = new TelephonyEvent(type, callId, DateTimeOffset.UtcNow,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["from"] = form.GetValueOrDefault("From"),
                ["to"] = form.GetValueOrDefault("To"),
                ["reason"] = status == "completed" ? "CallerHangup" : status,
                ["durationSeconds"] = form.GetValueOrDefault("CallDuration"),
                ["providerCallId"] = form.GetValueOrDefault("CallSid")
            });

        EventReceived?.Invoke(e);
        return [e];
    }

    private Task UpdateCallAsync(Guid callId, Dictionary<string, string> form, CancellationToken ct)
    {
        EnsureConfigured();
        return _http.PostAsync(
            $"{_options.BaseUrl}/2010-04-01/Accounts/{_options.AccountSid}/Calls/{callId}.json",
            new FormUrlEncodedContent(form), ct);
    }

    private void EnsureConfigured()
    {
        if (!_options.IsConfigured)
            throw new InvalidOperationException(
                "Twilio credentials are not configured. Run the prototype with the simulated provider " +
                "(Telephony:Provider=simulated) or supply Telephony:Twilio:AccountSid / AuthToken.");
    }

    private static string? ExtractJsonString(string json, string property)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(property, out var value)
            ? value.GetString()
            : null;
    }
}

/// <summary>E.164 normalisation (FR-D2). Deliberately conservative — it never invents a country.</summary>
public static class PhoneNumber
{
    public static string ToE164(string raw, string defaultCountryCode = "+44")
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var trimmed = raw.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return "";

        if (trimmed.StartsWith('+')) return "+" + digits;
        if (trimmed.StartsWith("00", StringComparison.Ordinal)) return "+" + digits[2..];
        if (digits.StartsWith('0')) return defaultCountryCode + digits[1..];

        return defaultCountryCode + digits;
    }

    public static bool IsValid(string e164) =>
        e164.StartsWith('+')
        && e164.Length is >= 8 and <= 16
        && e164[1..].All(char.IsDigit);

    public static string Mask(string e164) =>
        e164.Length <= 5 ? e164 : string.Concat(e164.AsSpan(0, e164.Length - 4), "****");
}
