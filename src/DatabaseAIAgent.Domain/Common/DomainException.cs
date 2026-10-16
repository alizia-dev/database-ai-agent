namespace DatabaseAIAgent.Domain.Common;

/// <summary>
/// Raised when a domain invariant is violated. The message is safe to show to an authenticated user;
/// it must never contain secrets or internal details.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException()
    {
    }

    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
