using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CallCenter.Api.Controllers;

/// <summary>
/// Removes <see cref="SimulatorController"/> from the application entirely unless the simulated
/// telephony provider is active.
///
/// An endpoint that can conjure calls out of nothing must not merely be guarded by a runtime check
/// someone could misconfigure — it should not be routable at all. Doing it at the feature-provider
/// level means the action never reaches the routing table, so it cannot be reached, cannot appear in
/// Swagger, and cannot be re-enabled by a config mistake.
/// </summary>
public sealed class SimulatorControllerFeatureProvider(bool enabled) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (enabled) return;

        var simulator = typeof(SimulatorController).GetTypeInfo();
        feature.Controllers.Remove(simulator);
    }
}
