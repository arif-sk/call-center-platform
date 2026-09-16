namespace CallCenter.Application.Abstractions;

/// <summary>
/// The current time, as a dependency. Reaching for <c>DateTimeOffset.UtcNow</c> inside a use case
/// makes anything that depends on elapsed time untestable without sleeping.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
