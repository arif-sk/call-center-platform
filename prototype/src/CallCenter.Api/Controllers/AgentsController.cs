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
public sealed class AgentsController(CallCenterService service) : CallCenterControllerBase(service)
{
    /// <summary>The snapshot the screens start from, before the live connection takes over.</summary>
    [HttpGet(Name = nameof(GetSnapshot))]
    public async Task<ActionResult<Snapshot>> GetSnapshot(CancellationToken ct) =>
        Ok(await Service.GetSnapshotAsync(ct));

    /// <summary>Takes a seat. The agent starts not-ready and presses "ready" when they are set up.</summary>
    [HttpPost("{agentId:guid}/sign-in", Name = nameof(SignIn))]
    public Task<ActionResult<Snapshot>> SignIn(Guid agentId, CancellationToken ct) =>
        RunAsync(() => Service.SignInAsync(agentId, ct));

    /// <summary>Refused while the agent still has a call in hand.</summary>
    [HttpPost("{agentId:guid}/sign-out", Name = nameof(SignOut))]
    public Task<ActionResult<Snapshot>> SignOut(Guid agentId, CancellationToken ct) =>
        RunAsync(() => Service.SignOutAsync(agentId, ct));

    /// <summary>
    /// The only state change an agent may ask for. Ringing, on-call and wrap-up are set by the
    /// platform, never requested by the client.
    /// </summary>
    [HttpPost("{agentId:guid}/ready", Name = nameof(SetReady))]
    public Task<ActionResult<Snapshot>> SetReady(Guid agentId, ReadyRequest request, CancellationToken ct) =>
        RunAsync(() => Service.SetReadyAsync(agentId, request.Ready, ct));
}
