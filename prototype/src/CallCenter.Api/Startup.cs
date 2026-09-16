using CallCenter.Api.Data;
using CallCenter.Api.Filters;
using CallCenter.Api.Hubs;
using CallCenter.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Api;

/// <summary>
/// Service registration and the HTTP pipeline, in the two places you expect to find them.
///
/// This is the classic MVC layout rather than top-level statements: what the application depends
/// on is in one method, the order middleware runs in is in the other, and neither is mixed in with
/// startup work. Every endpoint is a controller action — there is not a single route handler
/// defined in this file.
/// </summary>
public class Startup(IConfiguration configuration)
{
    public IConfiguration Configuration { get; } = configuration;

    public void ConfigureServices(IServiceCollection services)
    {
        // Falls back to a local SQL Server so the prototype runs with no configuration at all.
        var connectionString = Configuration.GetConnectionString("CallCenter")
            ?? "Server=localhost;Database=CallCenterPrototype;Trusted_Connection=True;TrustServerCertificate=True";

        services.AddDbContextFactory<CallCenterDbContext>(options => options.UseSqlServer(connectionString));

        services.AddSingleton<ISnapshotPublisher, SignalRSnapshotPublisher>();
        services.AddSingleton<CallCenterService>();

        // The exception filter is registered once, here, which is why no action in this project
        // has a try/catch in it.
        services.AddControllers(options => options.Filters.Add<CallCenterExceptionFilter>());

        services.AddSignalR();

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Serves the compiled Angular app out of wwwroot.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.UseRouting();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            // Controllers only. Nothing on this API is handled by an inline lambda.
            endpoints.MapControllers();

            endpoints.MapHub<CallCenterHub>("/hub");

            // The Angular app owns its own routes, so anything unrecognised returns the shell.
            endpoints.MapFallbackToFile("index.html");
        });
    }
}
