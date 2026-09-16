using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

public sealed record HealthResponse(string Status, DateTimeOffset ServerTime);

/// <summary>
/// What a load balancer or a deployment script calls to decide whether this instance is ready to
/// take traffic. It stays a controller like everything else so there is one HTTP surface to
/// reason about, not two.
/// </summary>
[ApiController]
[Route("health")]
[Produces("application/json")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get() => Ok(new HealthResponse("ok", DateTimeOffset.UtcNow));
}
