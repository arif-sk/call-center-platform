using CallCenter.Domain;

namespace CallCenter.Api.Services;

/// <summary>
/// Seeds the configuration an Ops admin would own in production (FR-A4). Kept in one place and
/// obviously replaceable — nothing in the platform reads from here at runtime.
/// </summary>
public static class DemoSeeder
{
    public static readonly Guid SupervisorAgentId = Guid.Parse("22222222-0000-0000-0000-000000000099");

    public static readonly string[] Dispositions =
    [
        "Resolved — first contact",
        "Resolved — escalated",
        "Information provided",
        "Callback scheduled",
        "Payment taken",
        "Complaint logged",
        "Wrong number",
        "Abandoned by customer"
    ];

    public static async Task SeedAsync(
        PlatformState state,
        DidRoutingTable dids,
        CrmClient crm,
        SuppressionService suppression)
    {
        // ---- queues -------------------------------------------------------
        var support = new QueueDefinition
        {
            Id = Guid.Parse("11111111-0000-0000-0000-000000000001"),
            Name = "support",
            DisplayName = "Customer Support",
            RequiredSkills = ["support"],
            BasePriority = 5,
            RingTimeoutSeconds = 15,
            SlaThresholdSeconds = 20,
            SelectionPolicy = SelectionPolicies.LongestIdle,
            RecordCalls = true
        };

        var sales = new QueueDefinition
        {
            Id = Guid.Parse("11111111-0000-0000-0000-000000000002"),
            Name = "sales",
            DisplayName = "Sales",
            RequiredSkills = ["sales"],
            BasePriority = 6,               // sales calls outrank support by default
            RingTimeoutSeconds = 12,
            SlaThresholdSeconds = 15,
            SelectionPolicy = SelectionPolicies.LeastCallsToday,
            RecordCalls = true
        };

        var billing = new QueueDefinition
        {
            Id = Guid.Parse("11111111-0000-0000-0000-000000000003"),
            Name = "billing",
            DisplayName = "Billing (VIP)",
            RequiredSkills = ["billing"],
            BasePriority = 8,               // highest priority — served first (FR-C5)
            RingTimeoutSeconds = 20,
            SlaThresholdSeconds = 30,
            SelectionPolicy = SelectionPolicies.HighestProficiency,
            RecordCalls = true
        };

        foreach (var q in new[] { support, sales, billing }) state.AddQueue(q);

        // ---- DID routing (FR-C1) -------------------------------------------
        dids.Map("+442045550100", "support");
        dids.Map("+442045550200", "sales");
        dids.Map("+442045550300", "billing");

        // ---- agents ---------------------------------------------------------
        AddAgent(state, "22222222-0000-0000-0000-000000000001", "Amara Okafor", "1001", "Team A",
            new() { ["support"] = 5, ["billing"] = 3 }, [support.Id, billing.Id]);

        AddAgent(state, "22222222-0000-0000-0000-000000000002", "Ben Halvorsen", "1002", "Team A",
            new() { ["support"] = 3, ["sales"] = 4 }, [support.Id, sales.Id]);

        AddAgent(state, "22222222-0000-0000-0000-000000000003", "Chen Wei", "1003", "Team B",
            new() { ["billing"] = 5, ["support"] = 2 }, [billing.Id, support.Id]);

        AddAgent(state, "22222222-0000-0000-0000-000000000004", "Dalia Rahman", "1004", "Team B",
            new() { ["sales"] = 5 }, [sales.Id]);

        AddAgent(state, "22222222-0000-0000-0000-000000000005", "Eli Brennan", "1005", "Team B",
            new() { ["support"] = 4, ["sales"] = 2, ["billing"] = 2 }, [support.Id, sales.Id, billing.Id]);

        // A supervisor account: authenticates and observes, never takes calls. Without this the
        // supervisor console would sign in as a real agent and evict their session (FR-B5).
        state.AddAgent(new Agent
        {
            Id = SupervisorAgentId,
            DisplayName = "Supervisor Console",
            Extension = "9000",
            TeamName = "Supervision",
            TakesCalls = false
        });

        // ---- CRM directory --------------------------------------------------
        crm.Seed("+447700900001", new CrmContact("crm-0031x001", "Priya Raman", "Northwind Ltd", "Gold", "2026-08-30 · billing query"));
        crm.Seed("+447700900002", new CrmContact("crm-0031x002", "Tom Alvarez", "Contoso", "Standard", "2026-09-02 · new order"));
        crm.Seed("+447700900003", new CrmContact("crm-0031x003", "Sofia Marchetti", "Fabrikam", "Platinum", "2026-09-10 · renewal"));

        // Deliberate ambiguity: one number, two contacts — we must never guess (FR-F4).
        crm.Seed("+447700900004", new CrmContact("crm-0031x004", "Jordan Lee", "Adventure Works", "Standard", "2026-07-11"));
        crm.Seed("+447700900004", new CrmContact("crm-0031x005", "Robin Lee", "Adventure Works", "Standard", "2026-08-19"));

        // ---- suppression list (FR-D4) ---------------------------------------
        await suppression.AddAsync("+447700900999", "Regulator");
        await suppression.AddAsync("+447700900998", "Customer");
    }

    private static void AddAgent(
        PlatformState state, string id, string name, string extension, string team,
        Dictionary<string, int> skills, Guid[] queues)
    {
        var agent = new Agent
        {
            Id = Guid.Parse(id),
            DisplayName = name,
            Extension = extension,
            TeamName = team,
            Skills = new Dictionary<string, int>(skills, StringComparer.OrdinalIgnoreCase),
            Queues = [.. queues]
        };
        state.AddAgent(agent);
    }
}
