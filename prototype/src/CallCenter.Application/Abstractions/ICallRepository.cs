using CallCenter.Domain.Calls;

namespace CallCenter.Application.Abstractions;

public interface ICallRepository
{
    Task<Call?> FindAsync(Guid callId, CancellationToken cancellationToken);

    void Add(Call call);

    /// <summary>Waiting calls, the one waiting longest first.</summary>
    Task<IReadOnlyList<Call>> ListWaitingLongestFirstAsync(CancellationToken cancellationToken);

    /// <summary>Everything still in progress: waiting, ringing, connected or being filed.</summary>
    Task<IReadOnlyList<Call>> ListInProgressAsync(CancellationToken cancellationToken);

    /// <summary>The most recently finished calls, newest first.</summary>
    Task<IReadOnlyList<Call>> ListRecentlyFinishedAsync(int count, CancellationToken cancellationToken);

    Task<int> CountCompletedAsync(CancellationToken cancellationToken);

    /// <summary>Calls left with an agent when the platform stopped.</summary>
    Task<IReadOnlyList<Call>> ListStrandedAsync(CancellationToken cancellationToken);
}
