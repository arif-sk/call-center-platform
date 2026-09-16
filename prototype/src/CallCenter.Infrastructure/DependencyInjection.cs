using CallCenter.Application.Abstractions;
using CallCenter.Application.Services;
using CallCenter.Infrastructure.Persistence;
using CallCenter.Infrastructure.Realtime;
using CallCenter.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CallCenter.Infrastructure;

/// <summary>
/// Everything the outside world provides, registered in one place. The API calls this and does
/// not otherwise know that SQL Server or SignalR exist.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCallCenter(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<CallCenterDbContext>(options => options.UseSqlServer(connectionString));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
        services.AddSingleton<ISnapshotPublisher, SignalRSnapshotPublisher>();
        services.AddSingleton<DatabaseInitialiser>();

        // The one application service, bound to its interface so the API never sees the class.
        services.AddSingleton<ICallCenterService, CallCenterService>();

        return services;
    }
}
