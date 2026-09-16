using CallCenter.Application.Abstractions;
using CallCenter.Domain.Agents;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Infrastructure.Persistence;

/// <summary>
/// Brings the database up to date on startup and seeds a handful of agents, so a reviewer can
/// clone the repository and see a working call centre without running a script first.
///
/// Migrating on startup suits one instance and a prototype. Several instances starting at once
/// would race, and a migration that takes a lock would hold up the deployment, so a real
/// deployment runs migrations as their own step before the new version starts — which is exactly
/// what having them as migrations rather than EnsureCreated makes possible.
/// </summary>
public class DatabaseInitialiser
{
    private static readonly (string Name, string Extension, bool IsSupervisor)[] SeedAgents =
    [
        ("Amina Rahman", "1001", false),
        ("Daniel Okafor", "1002", false),
        ("Priya Nair", "1003", false),
        ("Tomas Novak", "1004", false),
        ("Sara Haddad", "1100", true)
    ];

    private readonly IDbContextFactory<CallCenterDbContext> _dbContextFactory;
    private readonly IClock _clock;

    public DatabaseInitialiser(IDbContextFactory<CallCenterDbContext> dbContextFactory, IClock clock)
    {
        _dbContextFactory = dbContextFactory;
        _clock = clock;
    }

    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        await dbContext.Database.MigrateAsync(cancellationToken);

        if (!await dbContext.Agents.AnyAsync(cancellationToken))
        {
            foreach (var (name, extension, isSupervisor) in SeedAgents)
            {
                dbContext.Agents.Add(Agent.Create(name, extension, isSupervisor, _clock.UtcNow));
            }
        }

        // A restart leaves nobody signed in, so anything that was on a desktop goes back to the
        // queue rather than sitting with an agent who is no longer there.
        foreach (var agent in await dbContext.Agents.ToListAsync(cancellationToken))
        {
            agent.ResetForRestart();
        }

        var stranded = await dbContext.Calls
            .Where(call => call.Status == Domain.Calls.CallStatus.Ringing
                        || call.Status == Domain.Calls.CallStatus.Connected
                        || call.Status == Domain.Calls.CallStatus.WrapUp)
            .ToListAsync(cancellationToken);

        foreach (var call in stranded)
        {
            call.ReturnToQueueAfterRestart();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
