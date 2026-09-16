using CallCenter.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

/// <summary>
/// Every command returns the new snapshot, and a rejected command returns a message the screen can
/// show the agent. Putting that in one place keeps the controllers to a line or two per action.
/// </summary>
[ApiController]
public abstract class CallCenterControllerBase : ControllerBase
{
    protected async Task<ActionResult<Snapshot>> RunAsync(Func<Task<Snapshot>> command)
    {
        try
        {
            return Ok(await command());
        }
        catch (CallCenterException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
