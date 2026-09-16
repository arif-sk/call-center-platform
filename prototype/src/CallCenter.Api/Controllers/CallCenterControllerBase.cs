using CallCenter.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

/// <summary>
/// Shared behaviour for the command controllers.
///
/// Every command returns the new snapshot, and a rejected command comes back as a standard
/// <c>ProblemDetails</c> carrying a message the screen can show the agent as-is. Putting both in
/// one place keeps each action to a line and keeps every error on this API the same shape.
///
/// Note what is *not* here: the rules themselves. "You cannot finish a call without saying how it
/// ended" is a domain rule, so it lives in the service and is covered by a test — not in a
/// validation attribute that only runs when the request happens to arrive over HTTP.
/// </summary>
[ApiController]
[Produces("application/json")]
[ProducesResponseType(typeof(Snapshot), StatusCodes.Status200OK)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
public abstract class CallCenterControllerBase(CallCenterService service) : ControllerBase
{
    protected CallCenterService Service { get; } = service;

    protected async Task<ActionResult<Snapshot>> RunAsync(Func<Task<Snapshot>> command)
    {
        try
        {
            return Ok(await command());
        }
        catch (CallCenterException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                title: "The command was refused");
        }
    }
}
