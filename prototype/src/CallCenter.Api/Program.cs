using CallCenter.Infrastructure.Persistence;

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

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var initialiser = scope.ServiceProvider.GetRequiredService<DatabaseInitialiser>();
            await initialiser.InitialiseAsync();
        }

        await host.RunAsync();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder => webBuilder.UseStartup<Startup>());
}
