using System.Security.Claims;
using CallCenter.Api.Contracts;
using CallCenter.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

/// <summary>
/// Shared plumbing for the API surface: the authenticated agent's identity, and one place that
/// decides how a <see cref="CommandResult"/> becomes an HTTP status code.
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>The signed-in agent. Safe on any action reached through <c>[Authorize]</c>.</summary>
    protected Guid CurrentAgentId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? throw new InvalidOperationException("No agent identity on an authorised request."));

    protected string CurrentAgentName => User.FindFirstValue(ClaimTypes.Name) ?? "unknown";

    /// <summary>
    /// Maps a domain command result onto HTTP. Kept in one method so status-code semantics are
    /// consistent across every endpoint rather than decided ad hoc per action.
    /// </summary>
    protected IActionResult Respond(CommandResult result) =>
        result.Success
            ? Ok(result.Data ?? new { ok = true })
            : BadRequest(new ApiError(result.Error!));
}
