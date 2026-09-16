using CallCenter.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

/// <summary>
/// What a load balancer or a deployment script calls to decide whether this instance is ready to
/// take traffic. It stays a controller like everything else so there is one HTTP surface to
/// reason about, not two.
/// </summary>
[ApiController]
[Route("health")]
[Produces("application/json")]
public class HealthController : ControllerBase
{
    [HttpGet(Name = nameof(GetHealth))]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> GetHealth()
    {
        HealthResponse response = new HealthResponse
        {
            Status = "ok",
            ServerTime = DateTimeOffset.UtcNow
        };

        return Ok(response);
    }
}
