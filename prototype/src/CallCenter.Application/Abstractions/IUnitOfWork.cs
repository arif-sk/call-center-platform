namespace CallCenter.Application.Abstractions;

/// <summary>
/// One consistent view of the data for the length of one command, and one place where it is
/// saved. Routing reads agents and calls together and must not see half of somebody else's
/// change, which is why they are reached through the same unit of work rather than separately.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    IAgentRepository Agents { get; }

    ICallRepository Calls { get; }

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Opens a unit of work. A factory rather than an injected instance because commands are
/// serialised by the service, and each one wants its own clean view of the data.
/// </summary>
public interface IUnitOfWorkFactory
{
    Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken);
}
