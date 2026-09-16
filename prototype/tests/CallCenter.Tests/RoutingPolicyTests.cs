using CallCenter.Api.Services;
using CallCenter.Domain;
using CallCenter.Domain.Routing;

namespace CallCenter.Tests;

/// <summary>
/// Selection policy is business policy, not an algorithm choice (Q19). These tests exist because a
/// routing bug that quietly favours some agents is invisible in aggregate metrics and extremely
/// visible to the floor within a day — it becomes a fairness and payroll issue, not a defect report.
/// </summary>
public class RoutingPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid QueueId = Guid.NewGuid();

    private static Agent Agent(string name, DateTimeOffset availableSince, int callsToday = 0,
        params (string Skill, int Proficiency)[] skills)
    {
        var agent = new Domain.Agent
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            Extension = "0000",
            Skills = skills.ToDictionary(s => s.Skill, s => s.Proficiency, StringComparer.OrdinalIgnoreCase),
            Queues = [QueueId]
        };

        AgentStateMachine.Force(agent, AgentState.Available, availableSince);
        for (var i = 0; i < callsToday; i++) agent.RecordHandledCall();
        return agent;
    }

    private static QueuedCall Call(params string[] skills) =>
        new(Guid.NewGuid(), QueueId, 5, Now, skills);

    [Fact]
    public void Longest_idle_picks_the_agent_waiting_longest()
    {
        var recent = Agent("Recent", Now.AddSeconds(-10), skills: ("support", 5));
        var waiting = Agent("Waiting", Now.AddMinutes(-8), skills: ("support", 1));
        var middle = Agent("Middle", Now.AddMinutes(-2), skills: ("support", 3));

        var chosen = new LongestIdlePolicy().Select([recent, waiting, middle], Call("support"));

        // Deliberately picks the least skilled agent: fairness is the point of this policy.
        Assert.Equal("Waiting", chosen!.DisplayName);
    }

    [Fact]
    public void Highest_proficiency_prefers_skill_then_falls_back_to_idle_time()
    {
        var expert = Agent("Expert", Now.AddSeconds(-5), skills: ("billing", 5));
        var novice = Agent("Novice", Now.AddMinutes(-20), skills: ("billing", 1));

        var chosen = new HighestProficiencyPolicy().Select([novice, expert], Call("billing"));

        Assert.Equal("Expert", chosen!.DisplayName);
    }

    [Fact]
    public void Least_calls_today_levels_load_across_a_shift()
    {
        var busy = Agent("Busy", Now.AddMinutes(-30), callsToday: 12, skills: ("sales", 4));
        var quiet = Agent("Quiet", Now.AddSeconds(-5), callsToday: 3, skills: ("sales", 4));

        var chosen = new LeastCallsTodayPolicy().Select([busy, quiet], Call("sales"));

        Assert.Equal("Quiet", chosen!.DisplayName);
    }

    [Fact]
    public void Selection_is_deterministic_when_agents_tie()
    {
        // Non-deterministic selection makes routing bugs unreproducible, which is how they survive
        // to production. Ties break on agent id.
        var a = Agent("A", Now, skills: ("support", 3));
        var b = Agent("B", Now, skills: ("support", 3));
        var policy = new LongestIdlePolicy();

        var first = policy.Select([a, b], Call("support"));
        var second = policy.Select([b, a], Call("support"));

        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public void An_empty_eligible_set_selects_nobody()
    {
        Assert.Null(new LongestIdlePolicy().Select([], Call("support")));
    }

    [Fact]
    public void Unknown_policy_names_fall_back_to_longest_idle_rather_than_failing()
    {
        // Ops edit these values in production (FR-A4). A typo must not stop the queue.
        Assert.Equal(SelectionPolicies.LongestIdle, SelectionPolicyFactory.Resolve("nonsense").Name);
        Assert.Equal(SelectionPolicies.LongestIdle, SelectionPolicyFactory.Resolve(null).Name);
    }

    [Fact]
    public void Skill_matching_requires_every_skill_the_queue_asks_for()
    {
        var partial = Agent("Partial", Now, skills: ("support", 5));
        var full = Agent("Full", Now, 0, ("support", 3), ("german", 3));

        Assert.False(partial.HasSkills(["support", "german"]));
        Assert.True(full.HasSkills(["support", "german"]));
    }

    [Fact]
    public void Queue_order_is_priority_first_then_oldest_first()
    {
        var state = new PlatformState();
        var queue = new QueueDefinition { Id = QueueId, Name = "q", DisplayName = "Q" };
        state.AddQueue(queue);

        var oldLowPriority = new QueuedCall(Guid.NewGuid(), QueueId, 5, Now.AddMinutes(-10), []);
        var newHighPriority = new QueuedCall(Guid.NewGuid(), QueueId, 9, Now, []);
        var midLowPriority = new QueuedCall(Guid.NewGuid(), QueueId, 5, Now.AddMinutes(-5), []);

        state.Enqueue(oldLowPriority);
        state.Enqueue(newHighPriority);
        state.Enqueue(midLowPriority);

        var ordered = state.PeekOrdered(QueueId);

        // A VIP who just arrived is served before a standard caller who has waited ten minutes —
        // that is the business rule (FR-C5), and it is worth asserting so nobody "fixes" it later.
        Assert.Equal(newHighPriority.CallId, ordered[0].CallId);
        Assert.Equal(oldLowPriority.CallId, ordered[1].CallId);
        Assert.Equal(midLowPriority.CallId, ordered[2].CallId);
    }
}
