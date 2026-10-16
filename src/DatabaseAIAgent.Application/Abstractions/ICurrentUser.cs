namespace DatabaseAIAgent.Application.Abstractions;

/// <summary>
/// The authenticated caller, derived from the validated access token. Never populated from request bodies,
/// query strings or other client-supplied values.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    bool IsAuthenticated { get; }

    string? Email { get; }

    string? IpAddress { get; }

    string? UserAgent { get; }

    /// <summary>The authenticated user id; throws if the request is anonymous (a programming error at the call site).</summary>
    Guid RequiredUserId => UserId ?? throw new InvalidOperationException("No authenticated user in the current context.");
}
