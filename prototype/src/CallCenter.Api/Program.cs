using CallCenter.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Api;

/// <summary>
/// The entry point: build the host, get the database ready, run. Everything about *what* the
/// application is lives in <see cref="Startup"/>.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();

        await PrepareDatabaseAsync(host.Services);

        await host.RunAsync();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder => webBuilder.UseStartup<Startup>());

    /// <summary>
    /// Creates the database on first run and seeds a handful of agents, so a reviewer can clone
    /// the repository and see a working call centre without running a script first.
    ///
    /// EnsureCreated is the right tool for a prototype and the wrong one for production, where the
    /// schema changes over time and needs migrations.
    /// </summary>
    private static async Task PrepareDatabaseAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CallCenterDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        await db.Database.EnsureCreatedAsync();

        if (!await db.Agents.AnyAsync())
        {
            var now = DateTimeOffset.UtcNow;
            db.Agents.AddRange(
                NewAgent("Amina Rahman", "1001", false, now),
                NewAgent("Daniel Okafor", "1002", false, now),
                NewAgent("Priya Nair", "1003", false, now),
                NewAgent("Tomas Novak", "1004", false, now),
                NewAgent("Sara Haddad", "1100", true, now));
        }

        // A restart leaves nobody signed in, so any call that was with an agent goes back to the
        // queue rather than sitting on a desktop that no longer exists.
        foreach (var agent in await db.Agents.ToListAsync())
        {
            agent.State = AgentState.Offline;
            agent.CurrentCallId = null;
        }

        var stranded = await db.Calls
            .Where(c => c.Status == CallStatus.Ringing
                     || c.Status == CallStatus.Connected
                     || c.Status == CallStatus.WrapUp)
            .ToListAsync();

        foreach (var call in stranded)
        {
            call.Status = CallStatus.Queued;
            call.AgentId = null;
            call.AgentName = null;
        }

        await db.SaveChangesAsync();
    }

    private static Agent NewAgent(string name, string extension, bool supervisor, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Extension = extension,
        IsSupervisor = supervisor,
        State = AgentState.Offline,
        StateChangedAt = now
    };
}
