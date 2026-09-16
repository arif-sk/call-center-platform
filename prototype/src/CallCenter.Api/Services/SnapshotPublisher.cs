using CallCenter.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace CallCenter.Api.Services;

/// <summary>
/// How the new picture reaches the screens. It is an interface only so the domain can be tested
/// without standing up a web server.
/// </summary>
public interface ISnapshotPublisher
{
    Task PublishAsync(Snapshot snapshot, CancellationToken ct);
}

public sealed class SignalRSnapshotPublisher(IHubContext<CallCenterHub> hub) : ISnapshotPublisher
{
    public Task PublishAsync(Snapshot snapshot, CancellationToken ct) =>
        hub.Clients.All.SendAsync("snapshot", snapshot, ct);
}
