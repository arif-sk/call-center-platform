using System.Security.Claims;
using System.Text.Encodings.Web;
using CallCenter.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CallCenter.Api.Security;

public static class AgentSessionDefaults
{
    public const string Scheme = "AgentSession";

    /// <summary>Roles used by <c>[Authorize(Roles = ...)]</c>. Mirrors FR-A2.</summary>
    public const string AgentRole = "Agent";
    public const string SupervisorRole = "Supervisor";
    public const string AdminRole = "Admin";

    public const string SupervisorPolicy = "SupervisorOrAdmin";
}

/// <summary>
/// Turns the server-held session token into a <see cref="ClaimsPrincipal"/> so authorisation is
/// enforced by the framework — <c>[Authorize]</c> and <c>[Authorize(Roles = ...)]</c> — rather than
/// by a helper every action has to remember to call. Forgetting an attribute fails closed; forgetting
/// a helper call fails open, which is the whole reason this is a scheme and not a utility method.
///
/// In production this is replaced by OIDC bearer validation against the corporate IdP (NFR-SEC2).
/// Nothing in the controllers changes when that swap happens: they already read claims, not tokens.
/// </summary>
public sealed class AgentSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PlatformState state)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken(Request);

        if (string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var session = state.GetSession(token);
        if (session is null)
            return Task.FromResult(AuthenticateResult.Fail("Unknown or expired session."));

        var agent = state.GetAgent(session.AgentId);
        if (agent is null)
            return Task.FromResult(AuthenticateResult.Fail("Session refers to an unknown agent."));

        var claims = new List<Claim>
        {
            // NameIdentifier is also what SignalR's default user-id provider reads, so the hub gets
            // the same identity without parsing anything itself.
            new(ClaimTypes.NameIdentifier, agent.Id.ToString()),
            new(ClaimTypes.Name, agent.DisplayName),
            new(ClaimTypes.Role, session.Role)
        };

        // An admin is implicitly a supervisor; a supervisor is implicitly an agent.
        if (session.Role == AgentSessionDefaults.AdminRole)
            claims.Add(new Claim(ClaimTypes.Role, AgentSessionDefaults.SupervisorRole));

        if (session.Role != AgentSessionDefaults.AgentRole)
            claims.Add(new Claim(ClaimTypes.Role, AgentSessionDefaults.AgentRole));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    /// <summary>
    /// Header first, then the <c>access_token</c> query parameter — browsers cannot set headers on a
    /// WebSocket handshake, so SignalR has to pass its token in the query string. Accepting it only
    /// here keeps that concession in exactly one place.
    /// </summary>
    private static string? ExtractToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header[7..].Trim();

        var query = request.Query["access_token"].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : query;
    }
}
