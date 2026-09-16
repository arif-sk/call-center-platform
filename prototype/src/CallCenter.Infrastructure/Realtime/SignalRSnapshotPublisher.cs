using CallCenter.Application.Abstractions;
using CallCenter.Application.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace CallCenter.Infrastructure.Realtime;

/// <summary>
/// The SignalR end of <see cref="ISnapshotPublisher"/>. Swapping this for websockets, or for
/// server-sent events, would not touch a line of the application layer.
/// </summary>
public class SignalRSnapshotPublisher : ISnapshotPublisher
{
    private readonly IHubContext<CallCenterHub> _hubContext;

    public SignalRSnapshotPublisher(IHubContext<CallCenterHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAsync(Snapshot snapshot, CancellationToken cancellationToken) =>
        _hubContext.Clients.All.SendAsync("snapshot", snapshot, cancellationToken);
}
