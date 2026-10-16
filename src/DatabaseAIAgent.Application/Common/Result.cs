using System.Diagnostics.CodeAnalysis;

namespace DatabaseAIAgent.Application.Common;

public enum ErrorType
{
    Validation = 0,
    Unauthorized = 1,
    Forbidden = 2,
    NotFound = 3,
    Conflict = 4,
    TooManyRequests = 5,

    /// <summary>A required external service (e.g. a payment gateway) is unavailable or failed.</summary>
    Unavailable = 6,
}

/// <summary>
/// An expected, user-facing failure. <see cref="Code"/> is a stable machine-readable identifier for clients;
/// <see cref="Message"/> is safe to display and must never contain secrets or internal details.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type, IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? fields = null) =>
        new(code, message, ErrorType.Validation, fields);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);
}

public class Result
{
    protected Result(Error? error)
    {
        Error = error;
    }

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value)
        : base(null)
    {
        _value = value;
    }

    private Result(Error error)
        : base(error)
    {
    }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("A failed result has no value.");

#pragma warning disable CA2225 // Implicit conversions are the intended ergonomic API for results.
    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);
#pragma warning restore CA2225
}
