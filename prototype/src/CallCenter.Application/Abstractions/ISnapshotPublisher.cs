using CallCenter.Application.Contracts;

namespace CallCenter.Application.Abstractions;

/// <summary>
/// How the new picture reaches the screens. The application layer knows that somebody is told;
/// it does not know that the somebody is a web browser on the end of a SignalR connection.
/// </summary>
public interface ISnapshotPublisher
{
    Task PublishAsync(Snapshot snapshot, CancellationToken cancellationToken);
}
