namespace CallCenter.Api.Models;

/// <summary>Body of a request to start or stop taking calls.</summary>
public class ReadyRequest
{
    /// <summary>True to start taking calls, false to stop.</summary>
    public bool Ready { get; set; }
}
