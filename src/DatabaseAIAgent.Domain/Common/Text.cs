namespace DatabaseAIAgent.Domain.Common;

/// <summary>Shared string guard clauses for entity invariants.</summary>
public static class Text
{
    public static string Required(string? value, string name, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException($"{name} is required.");
        }

        return trimmed.Length > maxLength
            ? throw new DomainException($"{name} must be at most {maxLength} characters.")
            : trimmed;
    }

    public static string? Optional(string? value, string name, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > maxLength
            ? throw new DomainException($"{name} must be at most {maxLength} characters.")
            : trimmed;
    }
}
