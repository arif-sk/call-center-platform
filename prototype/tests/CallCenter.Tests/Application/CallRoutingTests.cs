using CallCenter.Application.Abstractions;
using CallCenter.Application.Contracts;
using CallCenter.Application.Services;
using CallCenter.Domain;
using CallCenter.Domain.Agents;
using CallCenter.Domain.Calls;
using CallCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CallCenter.Tests.Application;

/// <summary>
/// The use cases, over a real unit of work backed by an in-memory database. These are the
/// behaviours that would embarrass us in a demo, so they are the ones worth writing first.
///
/// Time is injected, so "the agent free longest goes next" is tested by moving a clock rather
/// than by sleeping and hoping.
/// </summary>
public class CallRoutingTests
{
    [Fact]
    public async Task An_incoming_call_rings_an_available_agent()
    {
        var context = new TestContext();
        var amina = await context.AddReadyAgentAsync("Amina");

        var snapshot = await context.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100");

        var call = Assert.Single(snapshot.Queue);
        Assert.Equal("Ringing", call.Status);
        Assert.Equal(amina, call.AgentId);
        Assert.Equal("Ringing", context.AgentIn(snapshot, amina).State);
    }

    [Fact]
    public async Task An_incoming_call_waits_when_nobody_is_available()
    {
        var context = new TestContext();
        await context.AddAgentAsync("Amina");

        var snapshot = await context.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100");

        Assert.Equal("Queued", Assert.Single(snapshot.Queue).Status);
        Assert.Equal(1, snapshot.Stats.Waiting);
    }

    [Fact]
    public async Task A_waiting_call_connects_the_moment_somebody_becomes_ready()
    {
        var context = new TestContext();
        var amina = await context.AddAgentAsync("Amina");
        await context.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100");

        var snapshot = await context.Service.SetReadyAsync(amina, ready: true);

        Assert.Equal("Ringing", Assert.Single(snapshot.Queue).Status);
        Assert.Equal(0, snapshot.Stats.Waiting);
    }

    [Fact]
    public async Task The_longest_waiting_call_is_offered_first()
    {
        var context = new TestContext();
        var older = await context.Service.ReceiveInboundCallAsync("+15550000001", "+18005550100");
        var olderId = Assert.Single(older.Queue).Id;

        context.Clock.Advance(TimeSpan.FromMinutes(4));
        await context.Service.ReceiveInboundCallAsync("+15550000002", "+18005550100");

        var amina = await context.AddAgentAsync("Amina");
        var snapshot = await context.Service.SetReadyAsync(amina, ready: true);

        var ringing = snapshot.Queue.Single(call => call.Status == "Ringing");
        Assert.Equal(olderId, ringing.Id);
    }

    [Fact]
    public async Task The_agent_who_has_been_free_longest_is_offered_the_next_call()
    {
        var context = new TestContext();
        var first = await context.AddReadyAgentAsync("Amina");

        context.Clock.Advance(TimeSpan.FromMinutes(5));
        await context.AddReadyAgentAsync("Daniel");

        var snapshot = await context.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100");

        Assert.Equal(first, Assert.Single(snapshot.Queue).AgentId);
    }

    [Fact]
    public async Task Two_calls_never_land_on_the_same_agent()
    {
        var context = new TestContext();
        await context.AddReadyAgentAsync("Amina");
        await context.AddReadyAgentAsync("Daniel");

        await context.Service.ReceiveInboundCallAsync("+15550000001", "+18005550100");
        var snapshot = await context.Service.ReceiveInboundCallAsync("+15550000002", "+18005550100");

        var owners = snapshot.Queue.Select(call => call.AgentId).ToList();
        Assert.Equal(2, owners.Distinct().Count());
        Assert.DoesNotContain(null, owners);
    }

    [Fact]
    public async Task Answering_then_hanging_up_leaves_the_agent_in_wrap_up()
    {
        var context = new TestContext();
        var amina = await context.AddReadyAgentAsync("Amina");
        var callId = await context.RingOneCallAsync();

        await context.Service.AnswerAsync(amina, callId);
        var snapshot = await context.Service.HangUpAsync(amina, callId);

        Assert.Equal("WrapUp", context.AgentIn(snapshot, amina).State);
        Assert.Equal("WrapUp", Assert.Single(snapshot.Queue).Status);
    }

    [Fact]
    public async Task Finishing_wrap_up_completes_the_call_and_frees_the_agent()
    {
        var context = new TestContext();
        var amina = await context.AddReadyAgentAsync("Amina");
        var callId = await context.RingOneCallAsync();
        await context.Service.AnswerAsync(amina, callId);
        await context.Service.HangUpAsync(amina, callId);

        var snapshot = await context.Service.CompleteWrapUpAsync(amina, callId, "Resolved", "Reset the password.");

        Assert.Empty(snapshot.Queue);
        Assert.Equal("Available", context.AgentIn(snapshot, amina).State);
        var completed = Assert.Single(snapshot.Recent);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal("Resolved", completed.Disposition);
        Assert.Equal(1, snapshot.Stats.Completed);
    }

    [Fact]
    public async Task An_agent_cannot_touch_a_call_that_was_offered_to_somebody_else()
    {
        var context = new TestContext();
        var amina = await context.AddReadyAgentAsync("Amina");
        var daniel = await context.AddAgentAsync("Daniel");
        var callId = await context.RingOneCallAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => context.Service.AnswerAsync(daniel, callId));

        Assert.Equal("That call belongs to another agent.", error.Message);
        var snapshot = await context.Service.GetSnapshotAsync();
        Assert.Equal(amina, Assert.Single(snapshot.Queue).AgentId);
    }

    [Fact]
    public async Task Declining_returns_the_call_to_the_queue_and_stops_offering_it_to_that_agent()
    {
        var context = new TestContext();
        var amina = await context.AddReadyAgentAsync("Amina");
        var callId = await context.RingOneCallAsync();

        var snapshot = await context.Service.DeclineAsync(amina, callId);

        Assert.Equal("Queued", Assert.Single(snapshot.Queue).Status);
        Assert.Equal("NotReady", context.AgentIn(snapshot, amina).State);
        Assert.Null(context.AgentIn(snapshot, amina).CurrentCallId);
    }

    [Fact]
    public async Task A_caller_who_gives_up_is_recorded_as_abandoned()
    {
        var context = new TestContext();
        var queued = await context.Service.ReceiveInboundCallAsync("+15550000001", "+18005550100");
        var callId = Assert.Single(queued.Queue).Id;

        context.Clock.Advance(TimeSpan.FromSeconds(95));
        var snapshot = await context.Service.AbandonAsync(callId);

        Assert.Empty(snapshot.Queue);
        var abandoned = Assert.Single(snapshot.Recent);
        Assert.Equal("Abandoned", abandoned.Status);
        Assert.Equal(95, abandoned.WaitSeconds);
    }

    [Fact]
    public async Task Every_command_pushes_the_new_picture_to_the_screens()
    {
        var context = new TestContext();
        await context.AddReadyAgentAsync("Amina");
        var publishedBefore = context.Publisher.Published.Count;

        await context.Service.ReceiveInboundCallAsync("+15551234567", "+18005550100");

        Assert.Equal(publishedBefore + 1, context.Publisher.Published.Count);
        Assert.Equal("Ringing", Assert.Single(context.Publisher.Published[^1].Queue).Status);
    }

    // ------------------------------------------------------------------ harness

    private sealed class TestContext
    {
        private readonly InMemoryUnitOfWorkFactory _unitOfWorkFactory = new(Guid.NewGuid().ToString());

        public TestContext()
        {
            Service = new CallCenterService(_unitOfWorkFactory, Publisher, Clock);
        }

        public ICallCenterService Service { get; }

        public FakeClock Clock { get; } = new();

        public RecordingPublisher Publisher { get; } = new();

        public async Task<Guid> AddAgentAsync(string name)
        {
            await using var dbContext = _unitOfWorkFactory.CreateDbContext();
            var agent = Agent.Create(name, "1001", isSupervisor: false, Clock.UtcNow);
            agent.SignIn(Clock.UtcNow);
            dbContext.Agents.Add(agent);
            await dbContext.SaveChangesAsync();
            return agent.Id;
        }

        public async Task<Guid> AddReadyAgentAsync(string name)
        {
            var agentId = await AddAgentAsync(name);
            await Service.SetReadyAsync(agentId, ready: true);
            return agentId;
        }

        /// <summary>One inbound call, already ringing on the only available agent.</summary>
        public async Task<Guid> RingOneCallAsync()
        {
            var snapshot = await Service.ReceiveInboundCallAsync("+15551234567", "+18005550100");
            return Assert.Single(snapshot.Queue).Id;
        }

        public AgentView AgentIn(Snapshot snapshot, Guid agentId) =>
            snapshot.Agents.Single(agent => agent.Id == agentId);
    }

    /// <summary>
    /// The real <see cref="UnitOfWork"/> from the infrastructure layer, over an in-memory
    /// provider. The repositories under test are the ones that ship.
    /// </summary>
    private sealed class InMemoryUnitOfWorkFactory : IUnitOfWorkFactory
    {
        private readonly string _databaseName;

        public InMemoryUnitOfWorkFactory(string databaseName)
        {
            _databaseName = databaseName;
        }

        public CallCenterDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<CallCenterDbContext>().UseInMemoryDatabase(_databaseName).Options);

        public Task<IUnitOfWork> CreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IUnitOfWork>(new UnitOfWork(CreateDbContext()));
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    }

    private sealed class RecordingPublisher : ISnapshotPublisher
    {
        public List<Snapshot> Published { get; } = [];

        public Task PublishAsync(Snapshot snapshot, CancellationToken cancellationToken)
        {
            Published.Add(snapshot);
            return Task.CompletedTask;
        }
    }
}
