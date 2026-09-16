using CallCenter.Api.Data;
using CallCenter.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Tests;

/// <summary>
/// The routing rules, tested against the real service with an in-memory database. These are the
/// behaviours that would embarrass us in a demo, so they are the ones worth writing first.
/// </summary>
public sealed class CallCenterServiceTests
{
    [Fact]
    public async Task An_incoming_call_rings_an_available_agent()
    {
        var h = new Harness();
        var amina = h.AddAgent("Amina", AgentState.Available);

        var snapshot = await h.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100");

        Assert.Equal("Ringing", snapshot.Queue.Single().Status);
        Assert.Equal(amina, snapshot.Queue.Single().AgentId);
        Assert.Equal("Ringing", h.AgentIn(snapshot, amina).State);
    }

    [Fact]
    public async Task An_incoming_call_waits_when_nobody_is_available()
    {
        var h = new Harness();
        h.AddAgent("Amina", AgentState.NotReady);

        var snapshot = await h.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100");

        Assert.Equal("Queued", snapshot.Queue.Single().Status);
        Assert.Equal(1, snapshot.Stats.Waiting);
    }

    [Fact]
    public async Task A_waiting_call_connects_as_soon_as_someone_becomes_ready()
    {
        var h = new Harness();
        var amina = h.AddAgent("Amina", AgentState.NotReady);
        await h.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100");

        var snapshot = await h.Service.SetReadyAsync(amina, ready: true);

        Assert.Equal("Ringing", snapshot.Queue.Single().Status);
        Assert.Equal(0, snapshot.Stats.Waiting);
    }

    [Fact]
    public async Task The_longest_waiting_call_is_offered_first()
    {
        var h = new Harness();
        var older = h.AddQueuedCall("+15550000001", minutesAgo: 5);
        h.AddQueuedCall("+15550000002", minutesAgo: 1);
        var amina = h.AddAgent("Amina", AgentState.NotReady);

        var snapshot = await h.Service.SetReadyAsync(amina, ready: true);

        var ringing = snapshot.Queue.Single(c => c.Status == "Ringing");
        Assert.Equal(older, ringing.Id);
    }

    [Fact]
    public async Task Two_calls_never_land_on_the_same_agent()
    {
        var h = new Harness();
        h.AddAgent("Amina", AgentState.Available);
        h.AddAgent("Daniel", AgentState.Available);

        await h.Service.ReceiveInboundCallAsync("+15550000001", "+18005550100");
        var snapshot = await h.Service.ReceiveInboundCallAsync("+15550000002", "+18005550100");

        var owners = snapshot.Queue.Select(c => c.AgentId).ToList();
        Assert.Equal(2, owners.Distinct().Count());
        Assert.DoesNotContain(null, owners);
    }

    [Fact]
    public async Task Answering_then_hanging_up_leaves_the_agent_in_wrap_up()
    {
        var h = new Harness();
        var amina = h.AddAgent("Amina", AgentState.Available);
        var callId = (await h.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100")).Queue.Single().Id;

        await h.Service.AnswerAsync(amina, callId);
        var snapshot = await h.Service.HangUpAsync(amina, callId);

        Assert.Equal("WrapUp", h.AgentIn(snapshot, amina).State);
        Assert.Equal("WrapUp", snapshot.Queue.Single().Status);
    }

    [Fact]
    public async Task Finishing_wrap_up_completes_the_call_and_frees_the_agent()
    {
        var h = new Harness();
        var amina = h.AddAgent("Amina", AgentState.Available);
        var callId = (await h.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100")).Queue.Single().Id;
        await h.Service.AnswerAsync(amina, callId);
        await h.Service.HangUpAsync(amina, callId);

        var snapshot = await h.Service.CompleteWrapUpAsync(amina, callId, "Resolved", "Reset the password.");

        Assert.Empty(snapshot.Queue);
        Assert.Equal("Available", h.AgentIn(snapshot, amina).State);
        var completed = snapshot.Recent.Single();
        Assert.Equal("Completed", completed.Status);
        Assert.Equal("Resolved", completed.Disposition);
    }

    [Fact]
    public async Task Wrap_up_cannot_be_finished_without_a_disposition()
    {
        var h = new Harness();
        var amina = h.AddAgent("Amina", AgentState.Available);
        var callId = (await h.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100")).Queue.Single().Id;
        await h.Service.AnswerAsync(amina, callId);
        await h.Service.HangUpAsync(amina, callId);

        await Assert.ThrowsAsync<CallCenterException>(
            () => h.Service.CompleteWrapUpAsync(amina, callId, "  ", null));
    }

    [Fact]
    public async Task An_agent_cannot_touch_a_call_that_was_offered_to_someone_else()
    {
        var h = new Harness();
        var amina = h.AddAgent("Amina", AgentState.Available);
        h.AddAgent("Daniel", AgentState.NotReady);
        var daniel = h.AgentIdByName("Daniel");
        var callId = (await h.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100")).Queue.Single().Id;

        var error = await Assert.ThrowsAsync<CallCenterException>(() => h.Service.AnswerAsync(daniel, callId));

        Assert.Contains("another agent", error.Message);
        Assert.Equal(amina, (await h.Service.GetSnapshotAsync()).Queue.Single().AgentId);
    }

    [Fact]
    public async Task Declining_puts_the_call_back_in_the_queue_and_stops_offering_it_to_that_agent()
    {
        var h = new Harness();
        var amina = h.AddAgent("Amina", AgentState.Available);
        var callId = (await h.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100")).Queue.Single().Id;

        var snapshot = await h.Service.DeclineAsync(amina, callId);

        Assert.Equal("Queued", snapshot.Queue.Single().Status);
        Assert.Equal("NotReady", h.AgentIn(snapshot, amina).State);
        Assert.Null(h.AgentIn(snapshot, amina).CurrentCallId);
    }

    [Fact]
    public async Task A_caller_who_gives_up_is_recorded_as_abandoned()
    {
        var h = new Harness();
        var callId = h.AddQueuedCall("+15550000001", minutesAgo: 2);

        var snapshot = await h.Service.AbandonAsync(callId);

        Assert.Empty(snapshot.Queue);
        Assert.Equal("Abandoned", snapshot.Recent.Single().Status);
        Assert.True(snapshot.Recent.Single().WaitSeconds >= 120);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class Harness
    {
        private readonly InMemoryFactory _factory = new(Guid.NewGuid().ToString());

        public CallCenterService Service { get; }

        public Harness() => Service = new CallCenterService(_factory, new NoOpPublisher());

        public Guid AddAgent(string name, AgentState state)
        {
            using var db = _factory.CreateDbContext();
            var agent = new Agent
            {
                Id = Guid.NewGuid(),
                Name = name,
                Extension = "1000",
                State = state,
                // Seeded in call order, so "the agent waiting longest" is deterministic.
                StateChangedAt = DateTimeOffset.UtcNow.AddMinutes(-10 + db.Agents.Count())
            };
            db.Agents.Add(agent);
            db.SaveChanges();
            return agent.Id;
        }

        public Guid AddQueuedCall(string from, int minutesAgo)
        {
            using var db = _factory.CreateDbContext();
            var call = new Call
            {
                Id = Guid.NewGuid(),
                FromNumber = from,
                ToNumber = "+18005550100",
                Status = CallStatus.Queued,
                QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)
            };
            db.Calls.Add(call);
            db.SaveChanges();
            return call.Id;
        }

        public Guid AgentIdByName(string name)
        {
            using var db = _factory.CreateDbContext();
            return db.Agents.Single(a => a.Name == name).Id;
        }

        public AgentView AgentIn(Snapshot snapshot, Guid agentId) => snapshot.Agents.Single(a => a.Id == agentId);
    }

    private sealed class InMemoryFactory(string name) : IDbContextFactory<CallCenterDbContext>
    {
        public CallCenterDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<CallCenterDbContext>().UseInMemoryDatabase(name).Options);
    }

    private sealed class NoOpPublisher : ISnapshotPublisher
    {
        public Task PublishAsync(Snapshot snapshot, CancellationToken ct) => Task.CompletedTask;
    }
}
