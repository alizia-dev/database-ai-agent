using System.Text;
using DatabaseAIAgent.Domain.Tools;

namespace DatabaseAIAgent.Tools.Generation;

/// <summary>Model-facing names: snake_case tool names and camelCase parameter names derived from catalog names.</summary>
public static class ToolNaming
{
    private static readonly string[] RoutinePrefixes = ["usp_", "sp_", "fn_", "proc_", "pr_"];
    private static readonly string[] ParameterPrefixes = ["p_", "in_", "i_", "par_"];
    private static readonly HashSet<string> DefaultSchemas = new(StringComparer.OrdinalIgnoreCase) { "dbo", "public" };

    /// <summary>A unique tool name: the routine name, then schema-qualified, then numbered if still taken.</summary>
    public static string UniqueToolName(string schema, string routine, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var baseName = ToSnakeCase(StripPrefix(routine, RoutinePrefixes));
        var candidates = new List<string> { baseName };
        if (!DefaultSchemas.Contains(schema))
        {
            candidates.Add(ToSnakeCase(schema) + "_" + baseName);
        }

        foreach (var candidate in candidates.Select(Fit))
        {
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        var root = Fit(candidates[^1]);
        for (var i = 2; ; i++)
        {
            var suffix = "_" + i;
            var candidate = (root.Length + suffix.Length > AiTool.NameMaxLength ? root[..(AiTool.NameMaxLength - suffix.Length)] : root) + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    public static string ParameterName(string sourceName)
    {
        var name = StripPrefix(sourceName.TrimStart('@', ':', '?'), ParameterPrefixes);
        var snake = ToSnakeCase(name);
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "value";
        }

        var camel = new StringBuilder(parts[0]);
        foreach (var part in parts.Skip(1))
        {
            camel.Append(char.ToUpperInvariant(part[0])).Append(part.AsSpan(1));
        }

        return char.IsAsciiLetter(camel[0]) ? camel.ToString() : "p" + camel;
    }

    /// <summary>"GetCustomerOrders" / "get-customer orders" → "get_customer_orders" (ASCII letters and digits only).</summary>
    public static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsAsciiLetterOrDigit(c))
            {
                var boundary = char.IsAsciiLetterUpper(c) && i > 0
                    && (char.IsAsciiLetterLower(value[i - 1]) || char.IsAsciiDigit(value[i - 1])
                        || (i + 1 < value.Length && char.IsAsciiLetterLower(value[i + 1]) && char.IsAsciiLetterUpper(value[i - 1])));
                if (boundary && builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var result = builder.ToString().Trim('_');
        return result.Length == 0 ? "tool" : result;
    }

    public static string Humanize(string name)
    {
        var words = ToSnakeCase(StripPrefix(name.TrimStart('@'), RoutinePrefixes.Concat(ParameterPrefixes).ToArray())).Replace('_', ' ');
        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string Fit(string name)
    {
        var value = char.IsAsciiLetterLower(name[0]) ? name : "tool_" + name;
        return value.Length <= AiTool.NameMaxLength ? value : value[..AiTool.NameMaxLength].TrimEnd('_');
    }

    private static string StripPrefix(string value, string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (value.Length > prefix.Length && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[prefix.Length..];
            }
        }

        return value;
    }
}
