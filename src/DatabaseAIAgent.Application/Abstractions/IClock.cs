namespace DatabaseAIAgent.Application.Abstractions;

/// <summary>Testable source of the current time. Always UTC.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
