using CallCenter.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Infrastructure.Persistence;

/// <summary>
/// One database context, with both repositories reading through it, so routing sees a single
/// consistent view of agents and calls and one save writes all of it.
/// </summary>
public class UnitOfWork : IUnitOfWork
{
    private readonly CallCenterDbContext _dbContext;

    public UnitOfWork(CallCenterDbContext dbContext)
    {
        _dbContext = dbContext;
        Agents = new AgentRepository(dbContext);
        Calls = new CallRepository(dbContext);
    }

    public IAgentRepository Agents { get; }

    public ICallRepository Calls { get; }

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    public ValueTask DisposeAsync() => _dbContext.DisposeAsync();
}

public class UnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly IDbContextFactory<CallCenterDbContext> _dbContextFactory;

    public UnitOfWorkFactory(IDbContextFactory<CallCenterDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken) =>
        new UnitOfWork(await _dbContextFactory.CreateDbContextAsync(cancellationToken));
}
