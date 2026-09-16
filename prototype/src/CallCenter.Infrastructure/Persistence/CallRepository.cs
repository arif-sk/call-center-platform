using CallCenter.Application.Abstractions;
using CallCenter.Domain.Calls;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Infrastructure.Persistence;

public class CallRepository : ICallRepository
{
    private static readonly CallStatus[] InProgressStatuses =
    [
        CallStatus.Queued, CallStatus.Ringing, CallStatus.Connected, CallStatus.WrapUp
    ];

    private readonly CallCenterDbContext _dbContext;

    public CallRepository(CallCenterDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Call?> FindAsync(Guid callId, CancellationToken cancellationToken) =>
        _dbContext.Calls.FirstOrDefaultAsync(call => call.Id == callId, cancellationToken);

    public void Add(Call call) => _dbContext.Calls.Add(call);

    public async Task<IReadOnlyList<Call>> ListWaitingLongestFirstAsync(CancellationToken cancellationToken) =>
        await _dbContext.Calls
            .Where(call => call.Status == CallStatus.Queued)
            .OrderBy(call => call.QueuedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Call>> ListInProgressAsync(CancellationToken cancellationToken) =>
        await _dbContext.Calls
            .Where(call => InProgressStatuses.Contains(call.Status))
            .OrderBy(call => call.QueuedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Call>> ListRecentlyFinishedAsync(int count, CancellationToken cancellationToken) =>
        await _dbContext.Calls
            .Where(call => call.Status == CallStatus.Completed || call.Status == CallStatus.Abandoned)
            .OrderByDescending(call => call.EndedAt)
            .Take(count)
            .ToListAsync(cancellationToken);

    public Task<int> CountCompletedAsync(CancellationToken cancellationToken) =>
        _dbContext.Calls.CountAsync(call => call.Status == CallStatus.Completed, cancellationToken);

    public async Task<IReadOnlyList<Call>> ListStrandedAsync(CancellationToken cancellationToken) =>
        await _dbContext.Calls
            .Where(call => call.Status == CallStatus.Ringing
                        || call.Status == CallStatus.Connected
                        || call.Status == CallStatus.WrapUp)
            .ToListAsync(cancellationToken);
}
