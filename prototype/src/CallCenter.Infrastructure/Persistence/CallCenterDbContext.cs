using CallCenter.Domain.Agents;
using CallCenter.Domain.Calls;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Infrastructure.Persistence;

/// <summary>
/// The SQL Server mapping. It lives out here so the entities stay free of persistence concerns —
/// there is not one EF Core attribute in the domain project.
/// </summary>
public class CallCenterDbContext : DbContext
{
    public CallCenterDbContext(DbContextOptions<CallCenterDbContext> options) : base(options)
    {
    }

    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<Call> Calls => Set<Call>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CallCenterDbContext).Assembly);
    }
}
