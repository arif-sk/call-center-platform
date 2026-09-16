using System.Security.Cryptography;
using System.Text;
using CallCenter.Telephony;

namespace CallCenter.Tests;

/// <summary>
/// Webhook verification is the security control on our most exposed public endpoint (NFR-SEC10):
/// anyone on the internet can POST to it, and a forged "call completed" would corrupt the CDR and
/// release an agent mid-call. It is implemented for real in the adapter, so it is tested for real.
/// </summary>
public class TwilioWebhookVerificationTests
{
    private const string AuthToken = "test-auth-token-not-a-real-secret";
    private const string Url = "https://callcenter.example.com/api/v1/telephony/webhooks/twilio/status";

    private static TwilioTelephonyProvider Provider() =>
        new(new HttpClient(), new TwilioOptions { AccountSid = "ACtest", AuthToken = AuthToken });

    private static string Sign(string url, IReadOnlyDictionary<string, string> form)
    {
        var builder = new StringBuilder(url);
        foreach (var key in form.Keys.OrderBy(k => k, StringComparer.Ordinal))
            builder.Append(key).Append(form[key]);

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(AuthToken));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static Dictionary<string, string> Form() => new()
    {
        ["CallSid"] = "CA1234567890",
        ["CallStatus"] = "completed",
        ["From"] = "+447700900001",
        ["To"] = "+442045550100",
        ["CallDuration"] = "213"
    };

    [Fact]
    public void A_correctly_signed_webhook_is_accepted()
    {
        var provider = Provider();
        var form = Form();

        Assert.True(provider.VerifyWebhook(Url, form, Sign(Url, form)));
    }

    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        var provider = Provider();
        var form = Form();
        var signature = Sign(Url, form);

        form["CallDuration"] = "9999";   // inflate the billable duration

        Assert.False(provider.VerifyWebhook(Url, form, signature));
    }

    [Fact]
    public void A_signature_for_a_different_url_is_rejected()
    {
        var provider = Provider();
        var form = Form();
        var signature = Sign("https://evil.example.com/hook", form);

        Assert.False(provider.VerifyWebhook(Url, form, signature));
    }

    [Fact]
    public void A_replayed_webhook_is_rejected()
    {
        // Carriers retry, and an attacker who captures one valid callback should not be able to
        // replay it. First delivery wins; the rest are dropped.
        var provider = Provider();
        var form = Form();
        var signature = Sign(Url, form);

        Assert.True(provider.VerifyWebhook(Url, form, signature));
        Assert.False(provider.VerifyWebhook(Url, form, signature));
    }

    [Fact]
    public void An_empty_signature_is_rejected()
    {
        Assert.False(Provider().VerifyWebhook(Url, Form(), ""));
    }

    [Fact]
    public void Vendor_payloads_are_translated_into_canonical_events()
    {
        // The point of the port: nothing above this line ever sees "CallStatus" or "CallSid".
        var provider = Provider();
        var callId = Guid.NewGuid();

        var events = provider.ParseWebhook(callId, new Dictionary<string, string>
        {
            ["CallStatus"] = "in-progress",
            ["CallSid"] = "CA999",
            ["From"] = "+447700900001"
        });

        var e = Assert.Single(events);
        Assert.Equal(TelephonyEventTypes.CallAnswered, e.Type);
        Assert.Equal(callId, e.CallId);
        Assert.Equal("CA999", e.Data["providerCallId"]);
    }

    [Fact]
    public void Unknown_vendor_statuses_produce_no_events_rather_than_failing()
    {
        var events = Provider().ParseWebhook(Guid.NewGuid(), new Dictionary<string, string>
        {
            ["CallStatus"] = "some-new-status-the-vendor-added"
        });

        Assert.Empty(events);
    }

    [Fact]
    public async Task Unconfigured_credentials_produce_a_clear_error_rather_than_a_null_reference()
    {
        var provider = new TwilioTelephonyProvider(new HttpClient(), new TwilioOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.PlaceCallAsync(new PlaceCallRequest(Guid.NewGuid(), "+442045550200", "+447700900001", false)));

        Assert.Contains("simulated", ex.Message);
    }
}

public class PhoneNumberTests
{
    [Theory]
    [InlineData("020 4555 0100", "+442045550100")]
    [InlineData("+44 20 4555 0100", "+442045550100")]
    [InlineData("00442045550100", "+442045550100")]
    [InlineData("(020) 4555-0100", "+442045550100")]
    public void Numbers_normalise_to_e164(string input, string expected)
    {
        Assert.Equal(expected, PhoneNumber.ToE164(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    public void Junk_input_produces_an_invalid_number_rather_than_a_crash(string input)
    {
        Assert.False(PhoneNumber.IsValid(PhoneNumber.ToE164(input)));
    }

    [Fact]
    public void Masking_keeps_numbers_out_of_logs()
    {
        // Phone numbers are PII. They belong in the CDR, not in application logs (NFR-SEC4).
        Assert.Equal("+44770090****", PhoneNumber.Mask("+447700900001"));
    }
}

public class SimulatedProviderTests
{
    [Fact]
    public async Task An_impatient_caller_abandons_while_queued()
    {
        // The scenario that is nearly impossible to reproduce on demand with a real carrier, and
        // which every abandon-rate report depends on being handled correctly (FR-C8).
        using var provider = new SimulatedTelephonyProvider(new SimulationOptions
        {
            CallerPatienceSeconds = 2,
            ProviderLatencyMs = 0
        });

        var received = new List<TelephonyEvent>();
        provider.EventReceived += received.Add;

        var callId = Guid.NewGuid();
        await provider.SimulateInboundAsync(callId, "+447700900001", "+442045550100", patienceSeconds: 2);

        provider.Tick(DateTimeOffset.UtcNow.AddSeconds(3));

        Assert.Contains(received, e => e.Type == TelephonyEventTypes.InboundCallReceived);
        Assert.Contains(received, e => e.Type == TelephonyEventTypes.CallerAbandoned && e.CallId == callId);
    }

    [Fact]
    public async Task Outbound_calls_that_nobody_answers_resolve_rather_than_hanging()
    {
        using var provider = new SimulatedTelephonyProvider(new SimulationOptions
        {
            OutboundAnswerProbability = 0,     // nobody ever picks up
            OutboundRingSeconds = 1,
            ProviderLatencyMs = 0
        });

        var received = new List<TelephonyEvent>();
        provider.EventReceived += received.Add;

        var callId = Guid.NewGuid();
        await provider.PlaceCallAsync(new PlaceCallRequest(callId, "+442045550200", "+447700900002", false));

        provider.Tick(DateTimeOffset.UtcNow.AddSeconds(2));

        Assert.Contains(received, e => e.Type == TelephonyEventTypes.OutboundNoAnswer && e.CallId == callId);
    }
}
