using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace CallCenter.Tests;

/// <summary>
/// Full-stack tests against the real application, driven through the simulated telephony provider.
///
/// This is the payoff from building the second adapter (docs/03-mvp-definition.md §3.5): an entire
/// inbound call — arrival, queue, skills match, reservation, screen-pop, answer, controls, wrap-up —
/// is exercised in a few hundred milliseconds, deterministically, for free. With only a real
/// carrier, none of this could be a CI test.
/// </summary>
[Collection("integration")]
public sealed class EndToEndCallFlowTests : IAsyncLifetime
{
    private static readonly Guid Amara = Guid.Parse("22222222-0000-0000-0000-000000000001"); // support + billing
    private static readonly Guid Chen  = Guid.Parse("22222222-0000-0000-0000-000000000003"); // billing + support
    private const string SupportDid = "+442045550100";
    private const string KnownCaller = "+447700900001";   // Priya Raman in the seeded CRM
    private const string SuppressedNumber = "+447700900999";

    private WebApplicationFactory<Program> _factory = null!;
    private readonly TestDatabase _database = new();

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:CallCenter", _database.ConnectionString);
            b.UseSetting("Telephony:Provider", "simulated");
            b.UseSetting("Telephony:Simulation:ProviderLatencyMs", "0");
            b.UseSetting("Telephony:Simulation:CallerPatienceSeconds", "120");
            b.UseSetting("Crm:SimulatedLatencyMs", "5");
            b.UseSetting("Urls", "");
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _database.Drop();
    }

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public async Task An_inbound_call_reaches_a_skilled_agent_and_produces_a_complete_audit_trail()
    {
        var client = _factory.CreateClient();
        var agent = await LoginAsync(client, Amara);
        await SetStateAsync(client, agent, "Available");

        var callId = await SimulateInboundAsync(client, SupportDid, KnownCaller);

        // 1. The call is offered to the one available agent who holds the 'support' skill.
        //    Reserved and Ringing are both "offered": the agent is claimed the instant the match is
        //    made, then moves to Ringing once the provider confirms the phone is ringing. Pinning
        //    one of them would make this test a race against how fast the database answers, which
        //    is a property of the environment rather than of the routing engine.
        var offered = await WaitForOfferAsync(client, agent);
        Assert.Equal(callId, offered.CallId);
        Assert.Contains(offered.AgentState, new[] { "Reserved", "Ringing" });

        // 2. Screen-pop arrives on its own track. The phone rings first (NFR-P1, p95 < 1 s) and the
        //    CRM lookup catches up (NFR-P2, p95 < 2 s) — deliberately decoupled so a slow CRM can
        //    never delay ringing. That is why this is a separate wait, not a field on the offer.
        Assert.Equal("Priya Raman", await WaitForContactAsync(client, agent));

        // 3. A stale or forged token cannot answer the call (NFR-SEC3).
        var forged = await PostAsync(client, agent, $"/api/v1/calls/{callId}/answer",
            new { reservationToken = "not-the-real-token" });
        Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);

        // 4. The real token can.
        var answered = await PostAsync(client, agent, $"/api/v1/calls/{callId}/answer",
            new { reservationToken = offered.Token });
        answered.EnsureSuccessStatusCode();
        Assert.Equal("OnCall", (await MeAsync(client, agent)).AgentState);

        // 5. In-call controls.
        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/hold", new { hold = true })).EnsureSuccessStatusCode();
        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/hold", new { hold = false })).EnsureSuccessStatusCode();

        // 6. PCI pause/resume around card capture (FR-G3).
        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/recording", new { paused = true })).EnsureSuccessStatusCode();
        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/dtmf", new { digits = "4111111111111111" })).EnsureSuccessStatusCode();
        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/recording", new { paused = false })).EnsureSuccessStatusCode();

        // 7. Hang up → mandatory wrap-up → back to Available (Q21).
        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/hangup", new { })).EnsureSuccessStatusCode();
        Assert.Equal("AfterCallWork", (await MeAsync(client, agent)).AgentState);

        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/disposition",
            new { code = "Resolved — first contact", notes = "Explained the invoice." })).EnsureSuccessStatusCode();
        Assert.Equal("Available", (await MeAsync(client, agent)).AgentState);

        // 8. The call is fully reconstructible from its event stream (NFR-M4) — this is the answer
        //    to "what happened to call X?", and the substrate every future AI feature reads.
        var trace = await GetJsonAsync(client, agent, $"/api/v1/calls/{callId}/trace");
        var types = trace.EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();

        Assert.Equal(
            ["CallInitiated", "CallQueued", "CallOffered"],
            types.Take(3));

        Assert.Contains("ScreenPopDelivered", types);
        Assert.Contains("AnswerRejected", types);      // the forged attempt is itself auditable
        Assert.Contains("CallAnswered", types);
        Assert.Contains("RecordingPaused", types);
        Assert.Contains("RecordingResumed", types);
        Assert.Contains("CallEnded", types);
        Assert.Contains("DispositionSet", types);

        // 9. The DTMF digits themselves are never written to the event stream (NFR-SEC6).
        var raw = trace.GetRawText();
        Assert.DoesNotContain("4111111111111111", raw);

        // 10. The CDR exists and is queryable.
        var cdr = await GetJsonAsync(client, agent, "/api/v1/reports/cdr?take=10");
        var row = cdr.EnumerateArray().First(c => c.GetProperty("id").GetGuid() == callId);
        Assert.Equal("Completed", row.GetProperty("status").GetString());
        Assert.Equal("support", row.GetProperty("queueName").GetString());
        Assert.Equal("Resolved — first contact", row.GetProperty("disposition").GetString());
    }

    // ------------------------------------------------------------------ RNA

    [Fact]
    public async Task A_rejected_call_is_requeued_with_a_priority_boost_and_offered_to_someone_else()
    {
        var client = _factory.CreateClient();

        var first = await LoginAsync(client, Amara);
        await SetStateAsync(client, first, "Available");

        var callId = await SimulateInboundAsync(client, SupportDid, KnownCaller);
        var offered = await WaitForOfferAsync(client, first);

        // A second agent joins only now, so the first offer could only have gone to agent one.
        var second = await LoginAsync(client, Chen);
        await SetStateAsync(client, second, "Available");

        (await PostAsync(client, first, $"/api/v1/calls/{callId}/reject",
            new { reservationToken = offered.Token })).EnsureSuccessStatusCode();

        // The agent who did not answer is taken out of routing so the next call does not hit the
        // same unattended desk (FR-C7).
        var firstAfter = await MeAsync(client, first);
        Assert.Equal("NotReady", firstAfter.AgentState);
        Assert.Equal("RNA", firstAfter.Reason);

        // The caller is not punished for it: re-queued, and offered to the other skilled agent.
        var reoffered = await WaitForOfferAsync(client, second);
        Assert.Equal(callId, reoffered.CallId);

        var trace = await GetJsonAsync(client, second, $"/api/v1/calls/{callId}/trace");
        var types = trace.EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToList();
        Assert.Contains("RingNoAnswer", types);
        Assert.Contains("CallRequeued", types);

        var requeue = trace.EnumerateArray().First(e => e.GetProperty("type").GetString() == "CallRequeued");
        using var payload = JsonDocument.Parse(requeue.GetProperty("payload").GetString()!);
        Assert.True(payload.RootElement.GetProperty("newPriority").GetInt32() > 5);
    }

    // ------------------------------------------------------------------ idempotency

    [Fact]
    public async Task A_call_that_ends_twice_is_only_counted_once()
    {
        // A hang-up produces both a command result and a provider event for the same call, and real
        // carriers retry their callbacks on top of that. Without an idempotency gate the agent is
        // credited with two calls, the CDR is written twice, and every productivity report is wrong
        // in a way nobody notices for a quarter (§4.3.1, §4.10).
        var client = _factory.CreateClient();
        var agent = await LoginAsync(client, Amara);
        await SetStateAsync(client, agent, "Available");

        var callId = await SimulateInboundAsync(client, SupportDid, KnownCaller);
        var offered = await WaitForOfferAsync(client, agent);

        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/answer",
            new { reservationToken = offered.Token })).EnsureSuccessStatusCode();

        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/hangup", new { })).EnsureSuccessStatusCode();

        // A second hang-up, and the provider event that the first one also produced.
        await PostAsync(client, agent, $"/api/v1/calls/{callId}/hangup", new { });
        await client.PostAsJsonAsync($"/api/v1/simulator/hangup/{callId}", new { });
        await Task.Delay(300);

        var trace = await GetJsonAsync(client, agent, $"/api/v1/calls/{callId}/trace");
        var endings = trace.EnumerateArray().Count(e => e.GetProperty("type").GetString() == "CallEnded");
        Assert.Equal(1, endings);

        var me = await GetJsonAsync(client, agent, "/api/v1/agents/me");
        Assert.Equal(1, me.GetProperty("callsHandled").GetInt32());
        Assert.Equal("AfterCallWork", me.GetProperty("state").GetString());
    }

    // ------------------------------------------------------------------ outbound + DNC

    [Fact]
    public async Task Outbound_to_a_suppressed_number_is_blocked_and_audited()
    {
        var client = _factory.CreateClient();
        var agent = await LoginAsync(client, Amara);
        await SetStateAsync(client, agent, "Available");

        var response = await PostAsync(client, agent, "/api/v1/calls/outbound", new { to = SuppressedNumber });

        // A policy refusal, not a validation error — and the call is never placed (FR-D4, R-07).
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Available", (await MeAsync(client, agent)).AgentState);

        var audit = await GetJsonAsync(client, agent, "/api/v1/audit");
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "OutboundBlocked");
    }

    [Fact]
    public async Task Outbound_to_a_permitted_number_occupies_the_agent_exactly_like_an_inbound_call()
    {
        var client = _factory.CreateClient();
        var agent = await LoginAsync(client, Amara);
        await SetStateAsync(client, agent, "Available");

        var response = await PostAsync(client, agent, "/api/v1/calls/outbound", new { to = "07700 900002" });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("+447700900002", body.GetProperty("to").GetString());       // normalised to E.164
        Assert.Equal("Tom Alvarez", body.GetProperty("contact").GetProperty("displayName").GetString());

        // FR-D5: the agent is OnCall, so the routing engine will not also offer them a queue call.
        Assert.Equal("OnCall", (await MeAsync(client, agent)).AgentState);
    }

    // ------------------------------------------------------------------ resilience

    [Fact]
    public async Task Calls_keep_flowing_when_the_crm_is_down()
    {
        // The rule the whole integration is designed around: the CRM is not allowed to take the
        // phones down (FR-F5). The screen-pop degrades; the call does not.
        var client = _factory.CreateClient();
        var agent = await LoginAsync(client, Amara);
        await SetStateAsync(client, agent, "Available");

        (await client.PostAsJsonAsync("/api/v1/simulator/crm-outage", new { enabled = true }))
            .EnsureSuccessStatusCode();

        var callId = await SimulateInboundAsync(client, SupportDid, KnownCaller);
        var offered = await WaitForOfferAsync(client, agent);

        Assert.Equal(callId, offered.CallId);
        Assert.Null(await WaitForContactAsync(client, agent, timeoutMs: 1000));   // degraded, not broken

        (await PostAsync(client, agent, $"/api/v1/calls/{callId}/answer",
            new { reservationToken = offered.Token })).EnsureSuccessStatusCode();
        Assert.Equal("OnCall", (await MeAsync(client, agent)).AgentState);

        var trace = await GetJsonAsync(client, agent, $"/api/v1/calls/{callId}/trace");
        var pop = trace.EnumerateArray().First(e => e.GetProperty("type").GetString() == "ScreenPopDelivered");
        using var payload = JsonDocument.Parse(pop.GetProperty("payload").GetString()!);
        Assert.Equal("Unavailable", payload.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_call_to_an_unmapped_number_gets_a_fallback_treatment_rather_than_silence()
    {
        // FR-I3: a misconfigured DID must never leave a customer listening to nothing.
        var client = _factory.CreateClient();
        var agent = await LoginAsync(client, Amara);

        var callId = await SimulateInboundAsync(client, "+442045559999", KnownCaller);

        var types = await WaitForEventAsync(client, agent, callId, "FallbackTreatmentApplied");
        Assert.Contains("FallbackTreatmentApplied", types);
    }

    [Fact]
    public async Task An_agent_who_is_not_available_is_never_offered_a_call()
    {
        var client = _factory.CreateClient();
        var agent = await LoginAsync(client, Amara);
        await SetStateAsync(client, agent, "NotReady", "Lunch");

        await SimulateInboundAsync(client, SupportDid, KnownCaller);
        await Task.Delay(1200);

        var me = await MeAsync(client, agent);
        Assert.Equal("NotReady", me.AgentState);
        Assert.Null(me.CallId);

        // The call is still waiting, not lost.
        var wallboard = await GetJsonAsync(client, agent, "/api/v1/wallboard");
        var support = wallboard.GetProperty("queues").EnumerateArray()
            .First(q => q.GetProperty("name").GetString() == "support");
        Assert.Equal(1, support.GetProperty("waiting").GetInt32());
    }

    // ------------------------------------------------------------------ helpers

    private sealed record AgentView(Guid Id, string Token);

    private sealed record Offer(Guid CallId, string Token, string AgentState, string? Contact);

    private sealed record MeView(string AgentState, string? Reason, Guid? CallId, string? Token, string? Contact);

    private static async Task<AgentView> LoginAsync(HttpClient client, Guid agentId)
    {
        var response = await client.PostAsJsonAsync("/api/v1/session", new { agentId, role = "Supervisor" });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentView(agentId, body.GetProperty("token").GetString()!);
    }

    private static async Task SetStateAsync(HttpClient client, AgentView agent, string state, string? reason = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agents/me/state")
        {
            Content = JsonContent.Create(new { state, reason })
        };
        request.Headers.Add("Authorization", $"Bearer {agent.Token}");
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> SimulateInboundAsync(HttpClient client, string did, string from)
    {
        var response = await client.PostAsJsonAsync("/api/v1/simulator/inbound", new { did, from });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("callId").GetGuid();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, AgentView agent, string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Authorization", $"Bearer {agent.Token}");
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, AgentView agent, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Authorization", $"Bearer {agent.Token}");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<MeView> MeAsync(HttpClient client, AgentView agent)
    {
        var me = await GetJsonAsync(client, agent, "/api/v1/agents/me");
        var call = me.GetProperty("currentCall");

        return new MeView(
            me.GetProperty("state").GetString()!,
            me.GetProperty("reason").GetString(),
            call.ValueKind == JsonValueKind.Null ? null : call.GetProperty("id").GetGuid(),
            me.GetProperty("reservationToken").GetString(),
            call.ValueKind == JsonValueKind.Null ? null : call.GetProperty("contact").GetString());
    }

    private static async Task<Offer> WaitForOfferAsync(HttpClient client, AgentView agent, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var me = await MeAsync(client, agent);
            if (me.CallId is { } callId && me.Token is { } token)
                return new Offer(callId, token, me.AgentState, me.Contact);

            await Task.Delay(50);
        }

        throw new TimeoutException($"No call was offered to agent {agent.Id} within {timeoutMs} ms.");
    }

    /// <summary>Polls for the screen-pop, which resolves independently of the call being offered.</summary>
    private static async Task<string?> WaitForContactAsync(HttpClient client, AgentView agent, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var me = await MeAsync(client, agent);
            if (me.Contact is not null) return me.Contact;
            await Task.Delay(50);
        }

        return null;
    }

    private static async Task<List<string>> WaitForEventAsync(
        HttpClient client, AgentView agent, Guid callId, string eventType, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/calls/{callId}/trace");
            request.Headers.Add("Authorization", $"Bearer {agent.Token}");
            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var trace = await response.Content.ReadFromJsonAsync<JsonElement>();
                var types = trace.EnumerateArray().Select(e => e.GetProperty("type").GetString()!).ToList();
                if (types.Contains(eventType)) return types;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Event '{eventType}' never appeared for call {callId}.");
    }
}

/// <summary>
/// Integration tests share a SQLite file per factory; running the class in its own non-parallel
/// collection keeps runs independent.
/// </summary>
[CollectionDefinition("integration", DisableParallelization = true)]
public sealed class IntegrationCollection;
