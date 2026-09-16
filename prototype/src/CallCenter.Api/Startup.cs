using CallCenter.Api.Filters;
using CallCenter.Infrastructure;
using CallCenter.Infrastructure.Realtime;

namespace CallCenter.Api;

/// <summary>
/// The composition root: service registration and the HTTP pipeline, in the two places you expect
/// to find them.
///
/// This is the only file in the API that mentions infrastructure at all, and it mentions it once.
/// The controllers below depend on the application layer's interfaces, so nothing in this project
/// knows that the database is SQL Server.
/// </summary>
public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        // Falls back to a local SQL Server so the prototype runs with no configuration at all.
        var connectionString = Configuration.GetConnectionString("CallCenter")
            ?? "Server=localhost;Database=CallCenterPrototype;Trusted_Connection=True;TrustServerCertificate=True";

        services.AddCallCenter(connectionString);

        // The exception filter is registered once, here, which is why no action in this project
        // has a try/catch in it.
        services.AddControllers(options => options.Filters.Add<DomainExceptionFilter>());

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
