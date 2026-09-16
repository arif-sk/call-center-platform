using CallCenter.Api.Models;
using CallCenter.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

/// <summary>
/// Sign-in and availability.
///
/// There is no password check: an agent picks their name from a list. Authentication belongs in
/// the company's existing identity provider and is described in the design document; building a
/// second login screen here would have proved nothing about call handling.
/// </summary>
[ApiController]
[Route("api/agents")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
public class AgentsController : ControllerBase
{
    private readonly CallCenterService _callCenterService;
    private readonly ILogger<AgentsController> _logger;

    public AgentsController(CallCenterService callCenterService, ILogger<AgentsController> logger)
    {
        _callCenterService = callCenterService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current picture of the call centre: every agent, every live call and the
    /// headline numbers. The screens call this once on load; after that the live connection
    /// pushes the same object whenever anything changes.
    /// </summary>
    [HttpGet(Name = nameof(GetSnapshot))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<ActionResult<Snapshot>> GetSnapshot(CancellationToken cancellationToken)
    {
        Snapshot snapshot = await _callCenterService.GetSnapshotAsync(cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>
    /// Takes a seat. The agent starts not-ready and presses "ready" when they are set up, so
    /// signing in never puts a call through to somebody who is not at their desk yet.
    /// </summary>
    [HttpPost("{agentId:guid}/sign-in", Name = nameof(SignIn))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<ActionResult<Snapshot>> SignIn(Guid agentId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent {AgentId} is signing in.", agentId);

        Snapshot snapshot = await _callCenterService.SignInAsync(agentId, cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>
    /// Gives up the seat. Refused while the agent still has a call in hand, so nobody can walk
    /// away from a customer who is still on the line.
    /// </summary>
    [HttpPost("{agentId:guid}/sign-out", Name = nameof(SignOut))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<ActionResult<Snapshot>> SignOut(Guid agentId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent {AgentId} is signing out.", agentId);

        Snapshot snapshot = await _callCenterService.SignOutAsync(agentId, cancellationToken);

        return Ok(snapshot);
    }

    /// <summary>
    /// Starts or stops taking calls. This is the only state change an agent may ask for: ringing,
    /// on-call and wrap-up are set by the platform, never requested by the client.
    /// </summary>
    [HttpPost("{agentId:guid}/ready", Name = nameof(SetReady))]
    [ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
    public async Task<ActionResult<Snapshot>> SetReady(
        Guid agentId,
        [FromBody] ReadyRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent {AgentId} is going {State}.", agentId, request.Ready ? "ready" : "not ready");

        Snapshot snapshot = await _callCenterService.SetReadyAsync(agentId, request.Ready, cancellationToken);

        return Ok(snapshot);
    }
}
