using System.Reflection;
using CallCenter.Api.Controllers;
using CallCenter.Application.Services;
using CallCenter.Domain.Agents;
using CallCenter.Infrastructure.Persistence;

namespace CallCenter.Tests.Architecture;

/// <summary>
/// Clean architecture is a claim about which way the arrows point, and a claim nobody checks stops
/// being true within a month. These tests fail the build the moment somebody reaches outward.
/// </summary>
public class DependencyRuleTests
{
    private static readonly Assembly Domain = typeof(Agent).Assembly;
    private static readonly Assembly Application = typeof(ICallCenterService).Assembly;
    private static readonly Assembly Infrastructure = typeof(CallCenterDbContext).Assembly;
    private static readonly Assembly Api = typeof(AgentsController).Assembly;

    [Fact]
    public void The_domain_depends_on_nothing_of_ours_and_no_framework()
    {
        var references = ReferencesOf(Domain);

        Assert.DoesNotContain("CallCenter.Application", references);
        Assert.DoesNotContain("CallCenter.Infrastructure", references);
        Assert.DoesNotContain("CallCenter.Api", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore"));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore"));
    }

    [Fact]
    public void The_application_layer_knows_the_domain_and_nothing_outside_it()
    {
        var references = ReferencesOf(Application);

        Assert.Contains("CallCenter.Domain", references);
        Assert.DoesNotContain("CallCenter.Infrastructure", references);
        Assert.DoesNotContain("CallCenter.Api", references);

        // The whole point of the abstractions: no database, no web framework in the use cases.
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore"));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore"));
    }

    [Fact]
    public void Infrastructure_implements_the_application_and_is_depended_on_by_nobody_but_the_host()
    {
        Assert.Contains("CallCenter.Application", ReferencesOf(Infrastructure));
        Assert.DoesNotContain("CallCenter.Api", ReferencesOf(Infrastructure));
    }

    [Fact]
    public void The_api_talks_to_the_application_layer_and_never_to_the_database_directly()
    {
        var references = ReferencesOf(Api);

        Assert.Contains("CallCenter.Application", references);

        // It references infrastructure only to wire it up at startup, so EF Core must not be
        // reachable from a controller.
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore"));
    }

    private static IReadOnlyList<string> ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToList();
}
