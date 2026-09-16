using CallCenter.Api.Data;
using CallCenter.Api.Hubs;
using CallCenter.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Falls back to a local SQL Server so the prototype runs with no configuration at all.
var connectionString = builder.Configuration.GetConnectionString("CallCenter")
    ?? "Server=localhost;Database=CallCenterPrototype;Trusted_Connection=True;TrustServerCertificate=True";

builder.Services.AddDbContextFactory<CallCenterDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddSingleton<ISnapshotPublisher, SignalRSnapshotPublisher>();
builder.Services.AddSingleton<CallCenterService>();
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

await PrepareDatabaseAsync(app);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();
app.MapHub<CallCenterHub>("/hub");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// The Angular app owns its own routes, so anything unrecognised returns the shell.
app.MapFallbackToFile("index.html");

app.Run();

// Creates the database on first run and seeds a handful of agents, so a reviewer can clone the
// repository and see a working call centre without running a script first. EnsureCreated is the
// right tool for a prototype and the wrong one for production, where the schema changes over time
// and needs migrations.
static async Task PrepareDatabaseAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
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

    // A restart leaves nobody signed in, so any call that was with an agent goes back to the queue
    // rather than sitting on a desktop that no longer exists.
    foreach (var agent in await db.Agents.ToListAsync())
    {
        agent.State = AgentState.Offline;
        agent.CurrentCallId = null;
    }

    foreach (var call in await db.Calls.Where(c =>
        c.Status == CallStatus.Ringing || c.Status == CallStatus.Connected || c.Status == CallStatus.WrapUp).ToListAsync())
    {
        call.Status = CallStatus.Queued;
        call.AgentId = null;
        call.AgentName = null;
    }

    await db.SaveChangesAsync();

    static Agent NewAgent(string name, string extension, bool supervisor, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Extension = extension,
        IsSupervisor = supervisor,
        State = AgentState.Offline,
        StateChangedAt = now
    };
}

