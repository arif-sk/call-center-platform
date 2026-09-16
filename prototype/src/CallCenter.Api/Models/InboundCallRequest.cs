namespace CallCenter.Api.Models;

/// <summary>Body of a simulated inbound call. Stands in for the carrier's webhook payload.</summary>
public class InboundCallRequest
{
    /// <summary>The number the customer is calling from.</summary>
    public string? From { get; set; }

    /// <summary>The number they dialled.</summary>
    public string? To { get; set; }
}
