using CallCenter.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CallCenter.Api.Controllers;

public sealed record ReadyRequest(bool Ready);

/// <summary>
/// Sign-in and availability.
///
/// There is no password check: an agent picks their name from a list. Authentication belongs in
/// the company's existing identity provider and is described in the design document; building a
/// second login screen here would have proved nothing about call handling.
/// </summary>
[Route("api/agents")]
public sealed class AgentsController(CallCenterService service) : CallCenterControllerBase
{
    /// <summary>The snapshot the screens start from, before the live connection takes over.</summary>
    [HttpGet]
    public async Task<ActionResult<Snapshot>> Get(CancellationToken ct) =>
        Ok(await service.GetSnapshotAsync(ct));

    [HttpPost("{agentId:guid}/sign-in")]
    public Task<ActionResult<Snapshot>> SignIn(Guid agentId, CancellationToken ct) =>
        RunAsync(() => service.SignInAsync(agentId, ct));

    [HttpPost("{agentId:guid}/sign-out")]
    public Task<ActionResult<Snapshot>> SignOut(Guid agentId, CancellationToken ct) =>
        RunAsync(() => service.SignOutAsync(agentId, ct));

    [HttpPost("{agentId:guid}/ready")]
    public Task<ActionResult<Snapshot>> SetReady(Guid agentId, ReadyRequest request, CancellationToken ct) =>
        RunAsync(() => service.SetReadyAsync(agentId, request.Ready, ct));
}
