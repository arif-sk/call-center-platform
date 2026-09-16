using CallCenter.Api.Controllers;
using CallCenter.Api.Data;
using CallCenter.Api.Realtime;
using CallCenter.Api.Security;
using CallCenter.Api.Services;
using CallCenter.Telephony;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- persistence
// SQL Server. Defaults to a local instance with Windows authentication so the prototype starts
// with one command; point ConnectionStrings:CallCenter at anything else (LocalDB, a container,
// Azure SQL) without touching code. In production the time-series tables are partitioned monthly
// — see docs §4.7.2.
var connectionString = builder.Configuration.GetConnectionString("CallCenter")
    ?? "Server=localhost;Database=CallCenterPrototype;Trusted_Connection=True;TrustServerCertificate=True";

builder.Services.AddDbContextFactory<CallCenterDbContext>(o =>
    o.UseSqlServer(connectionString, sql =>
    {
        // Transient faults are normal against any networked database and routine against Azure SQL.
        // Retrying here is what stops a two-second failover becoming a failed call record.
        sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(3), errorNumbersToAdd: null);
        sql.CommandTimeout(30);
    }));

// ---------------------------------------------------------------- options
var routingOptions = builder.Configuration.GetSection("Routing").Get<RoutingOptions>() ?? new RoutingOptions();
var crmOptions = builder.Configuration.GetSection("Crm").Get<CrmOptions>() ?? new CrmOptions();
var simulationOptions = builder.Configuration.GetSection("Telephony:Simulation").Get<SimulationOptions>() ?? new SimulationOptions();
var twilioOptions = builder.Configuration.GetSection("Telephony:Twilio").Get<TwilioOptions>() ?? new TwilioOptions();

builder.Services.AddSingleton(routingOptions);
builder.Services.AddSingleton(crmOptions);
builder.Services.AddSingleton(twilioOptions);

// ---------------------------------------------------------------- telephony port
// The whole platform talks to ITelephonyProvider and nothing else. Swapping the line below is the
// entire cost of changing carrier for everything above this layer (NFR-M1, R-10).
var providerName = builder.Configuration["Telephony:Provider"] ?? "simulated";
var useSimulator = !providerName.Equals("twilio", StringComparison.OrdinalIgnoreCase);

if (useSimulator)
{
    builder.Services.AddSingleton<SimulatedTelephonyProvider>(_ => new SimulatedTelephonyProvider(simulationOptions));
    builder.Services.AddSingleton<ITelephonyProvider>(sp => sp.GetRequiredService<SimulatedTelephonyProvider>());
}
else
{
    builder.Services.AddHttpClient("twilio");
    builder.Services.AddSingleton<ITelephonyProvider>(sp =>
        new TwilioTelephonyProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("twilio"), twilioOptions));
}

// ---------------------------------------------------------------- platform services
builder.Services.AddSingleton<PlatformState>();
builder.Services.AddSingleton<DidRoutingTable>();
builder.Services.AddSingleton<InteractionEventStore>();
builder.Services.AddSingleton<CrmClient>();
builder.Services.AddSingleton<SuppressionService>();
builder.Services.AddSingleton<WallboardService>();
builder.Services.AddSingleton<CallOrchestrator>();

builder.Services.AddSingleton<RoutingEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RoutingEngine>());
builder.Services.AddHostedService<WallboardBroadcaster>();
builder.Services.AddHostedService<CrmWriteBackWorker>();

// ---------------------------------------------------------------- auth
// Authorisation is enforced by the framework through [Authorize] attributes rather than by a helper
// every action has to remember to call — forgetting an attribute fails closed, forgetting a helper
// call fails open. In production this scheme is replaced by OIDC bearer validation (NFR-SEC2); the
// controllers read claims, not tokens, so none of them change.
builder.Services
    .AddAuthentication(AgentSessionDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, AgentSessionAuthenticationHandler>(AgentSessionDefaults.Scheme, null);

builder.Services.AddAuthorization();

// ---------------------------------------------------------------- MVC
builder.Services
    .AddControllers()
    .ConfigureApplicationPartManager(manager =>
        // The simulator is not merely disabled when a real carrier is configured — it is removed
        // from the routing table altogether. See SimulatorControllerFeatureProvider.
        manager.FeatureProviders.Add(new SimulatorControllerFeatureProvider(useSimulator)));

builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    var xml = Path.Combine(AppContext.BaseDirectory, "CallCenter.Api.xml");
    if (File.Exists(xml)) o.IncludeXmlComments(xml, includeControllerXmlComments: true);
});
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

var app = builder.Build();

// ---------------------------------------------------------------- bootstrap
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CallCenterDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureDeletedAsync();     // prototype: start every run from a clean journal
    await db.Database.EnsureCreatedAsync();
}

await DemoSeeder.SeedAsync(
    app.Services.GetRequiredService<PlatformState>(),
    app.Services.GetRequiredService<DidRoutingTable>(),
    app.Services.GetRequiredService<CrmClient>(),
    app.Services.GetRequiredService<SuppressionService>());

// Canonical telephony events → the orchestrator. This is the seam where a vendor payload has
// already been translated and nothing downstream knows which carrier we use.
var telephony = app.Services.GetRequiredService<ITelephonyProvider>();
var orchestrator = app.Services.GetRequiredService<CallOrchestrator>();
telephony.EventReceived += e => _ = orchestrator.HandleTelephonyEventAsync(e);

// ---------------------------------------------------------------- pipeline
app.UseSwagger();
app.UseSwaggerUI(o => o.RoutePrefix = "swagger");
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapHub<CallCenterHub>("/hubs/callcenter");
app.MapControllers();

// The Angular client is a single-page app: /agent and /supervisor are client routes, not files.
// The fallback has the lowest route priority, so it cannot shadow the API, the hub, Swagger or the
// health probes — a deep link or a refresh lands on index.html and the router takes over.
app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Call Center prototype ready. Provider={Provider}. Open http://localhost:5080", telephony.Name);

app.Run();

/// <summary>Exposed so the integration tests can spin the whole app up with WebApplicationFactory.</summary>
public partial class Program;
