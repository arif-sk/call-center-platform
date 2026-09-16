namespace CallCenter.Domain;

/// <summary>
/// A rule of the business was broken: the wrong state, somebody else's call, a missing
/// disposition. It is not a fault — the request was understood and refused.
///
/// The API turns this into a 400 with a message the agent can read. Nothing else in the domain
/// knows or cares that HTTP exists.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}
