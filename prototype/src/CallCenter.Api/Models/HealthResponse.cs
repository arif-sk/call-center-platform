namespace CallCenter.Api.Models;

/// <summary>What the health endpoint returns.</summary>
public class HealthResponse
{
    public string Status { get; set; } = string.Empty;

    public DateTimeOffset ServerTime { get; set; }
}
