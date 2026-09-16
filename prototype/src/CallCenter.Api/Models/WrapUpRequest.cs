namespace CallCenter.Api.Models;

/// <summary>Body of a request to file a finished call.</summary>
public class WrapUpRequest
{
    /// <summary>How the call ended. Required — the rule is enforced in the domain service.</summary>
    public string Disposition { get; set; } = string.Empty;

    /// <summary>Anything the agent wants to record about the call.</summary>
    public string? Notes { get; set; }
}
