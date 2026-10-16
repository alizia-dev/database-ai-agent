using DatabaseAIAgent.Application.Abstractions;

namespace DatabaseAIAgent.Infrastructure.Time;

internal sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
