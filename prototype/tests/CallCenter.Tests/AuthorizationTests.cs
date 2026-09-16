using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace CallCenter.Tests;

/// <summary>
/// Authorisation is enforced by the framework, at the controller, not by a helper each action has to
/// remember to call (FR-A2, NFR-SEC3). These tests assert the behaviour that distinction buys:
/// a valid agent session is <b>not</b> enough to read the wallboard, the CDR or the audit log.
///
/// They also cover the model validation <c>[ApiController]</c> now does at the edge — an empty
/// reservation token is rejected before any routing code runs.
/// </summary>
[Collection("integration")]
public sealed class AuthorizationTests : IAsyncLifetime
{
    private static readonly Guid Amara = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid SupervisorAccount = Guid.Parse("22222222-0000-0000-0000-000000000099");

    private WebApplicationFactory<Program> _factory = null!;
    private readonly TestDatabase _database = new();

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:CallCenter", _database.ConnectionString);
            b.UseSetting("Telephony:Provider", "simulated");
            b.UseSetting("Telephony:Simulation:ProviderLatencyMs", "0");
            b.UseSetting("Urls", "");
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _database.Drop();
    }

    // ------------------------------------------------------------------ authentication

    [Theory]
    [InlineData("GET", "/api/v1/agents/me")]
    [InlineData("GET", "/api/v1/wallboard")]
    [InlineData("GET", "/api/v1/reports/cdr")]
    [InlineData("GET", "/api/v1/audit")]
    [InlineData("GET", "/api/v1/calls/recent")]
    public async Task Protected_endpoints_reject_anonymous_callers(string method, string path)
    {
        var response = await _factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_is_rejected()
    {
        var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agents/me");
        request.Headers.Add("Authorization", "Bearer 00000000000000000000000000000000");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/bootstrap")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Public_endpoints_stay_public(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------ authorisation by role

    [Theory]
    [InlineData("/api/v1/wallboard")]
    [InlineData("/api/v1/reports/cdr")]
    [InlineData("/api/v1/audit")]
    [InlineData("/api/v1/suppressions")]
    [InlineData("/api/v1/calls/recent")]
    public async Task An_agent_session_cannot_reach_the_supervisor_surface(string path)
    {
        // The point of moving authorisation onto the controller: an agent with a perfectly valid
        // session is still refused. Under the previous hand-rolled check these endpoints were
        // reachable by anyone holding any token.
        var client = _factory.CreateClient();
        var agentToken = await LoginAsync(client, Amara, "Agent");

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, path, agentToken)).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/wallboard")]
    [InlineData("/api/v1/reports/cdr")]
    [InlineData("/api/v1/audit")]
    [InlineData("/api/v1/suppressions")]
    [InlineData("/api/v1/calls/recent")]
    public async Task A_supervisor_session_can(string path)
    {
        var client = _factory.CreateClient();
        var supervisorToken = await LoginAsync(client, SupervisorAccount, "Supervisor");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, path, supervisorToken)).StatusCode);
    }

    [Fact]
    public async Task A_supervisor_can_still_use_the_agent_surface()
    {
        // Roles are hierarchical: Admin implies Supervisor implies Agent. A team lead who takes
        // calls during a peak should not need two logins.
        var client = _factory.CreateClient();
        var supervisorToken = await LoginAsync(client, Amara, "Supervisor");

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/v1/agents/me", supervisorToken)).StatusCode);
    }

    [Fact]
    public async Task An_unrecognised_role_is_downgraded_to_agent_rather_than_honoured()
    {
        // Never trust a role the client asked for. "Admin" spelled creatively must not become admin.
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, Amara, "SuperAdmin!!");

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, "/api/v1/wallboard", token)).StatusCode);
    }

    // ------------------------------------------------------------------ model validation

    [Fact]
    public async Task An_empty_reservation_token_is_rejected_at_the_edge()
    {
        // [ApiController] + data annotations reject this before any routing or telephony code runs,
        // and the response is RFC-7807 problem details rather than a hand-written error.
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, Amara, "Agent");

        var response = await PostAsync(client, $"/api/v1/calls/{Guid.NewGuid()}/answer",
            new { reservationToken = "" }, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task A_disposition_without_a_code_is_rejected_at_the_edge()
    {
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, Amara, "Agent");

        var response = await PostAsync(client, $"/api/v1/calls/{Guid.NewGuid()}/disposition",
            new { code = "", notes = "nothing" }, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_transfer_with_no_destination_is_refused()
    {
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, Amara, "Agent");

        var response = await PostAsync(client, $"/api/v1/calls/{Guid.NewGuid()}/transfer",
            new { toNumber = (string?)null, toAgentId = (Guid?)null }, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<string> LoginAsync(HttpClient client, Guid agentId, string role)
    {
        var response = await client.PostAsJsonAsync("/api/v1/session", new { agentId, role });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Authorization", $"Bearer {token}");
        return client.SendAsync(request);
    }
}
