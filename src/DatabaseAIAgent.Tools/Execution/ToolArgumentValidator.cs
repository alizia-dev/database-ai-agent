using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Domain.Tools;

namespace DatabaseAIAgent.Tools.Execution;

/// <summary>
/// Validates model-supplied arguments against the tool's parameter definitions and converts them to typed values.
/// Unknown arguments are rejected (the model cannot smuggle extra parameters); values are never interpreted as SQL.
/// </summary>
public static class ToolArgumentValidator
{
    public sealed record Validated(IReadOnlyList<ProcedureArgument> Arguments, string RedactedJson, IReadOnlyList<(string Name, string Display)> Summary);

    public static (Validated? Result, IReadOnlyDictionary<string, string[]> Errors) Validate(AiTool tool, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var supplied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in arguments.EnumerateObject())
            {
                supplied[property.Name] = property.Value;
            }
        }
        else if (arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return (null, new Dictionary<string, string[]> { ["arguments"] = ["Arguments must be a JSON object."] });
        }

        var known = tool.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var unknown in supplied.Keys.Where(k => !known.Contains(k)))
        {
            errors[unknown] = ["Unknown argument."];
        }

        var values = new List<ProcedureArgument>();
        var redacted = new JsonObject();
        var summary = new List<(string, string)>();
        foreach (var parameter in tool.Parameters)
        {
            if (!supplied.TryGetValue(parameter.Name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (parameter.Required)
                {
                    errors[parameter.Name] = ["This argument is required."];
                }

                continue;
            }

            var (converted, error) = Convert(parameter, value);
            if (error is not null)
            {
                errors[parameter.Name] = [error];
                continue;
            }

            values.Add(new ProcedureArgument(parameter.SourceName, parameter.Type, converted));
            redacted[parameter.Name] = converted is string text ? $"[text, {text.Length} characters]" : JsonValue.Create(Display(converted));
            summary.Add((parameter.Name, Display(converted)));
        }

        return errors.Count > 0
            ? (null, errors)
            : (new Validated(values, redacted.ToJsonString(), summary), errors);
    }

    private static (object? Value, string? Error) Convert(ToolParameter parameter, JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : null;
        switch (parameter.Type)
        {
            case ToolParameterType.Integer:
                if ((value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer))
                    || (text is not null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)))
                {
                    return InRange(parameter, integer) ? (integer, null) : (null, RangeMessage(parameter));
                }

                return (null, "Expected a whole number.");
            case ToolParameterType.Number:
                if ((value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
                    || (text is not null && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out number)))
                {
                    return InRange(parameter, number) ? (number, null) : (null, RangeMessage(parameter));
                }

                return (null, "Expected a number.");
            case ToolParameterType.Boolean:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return (value.GetBoolean(), null);
                }

                return text is not null && bool.TryParse(text, out var flag) ? (flag, null) : (null, "Expected true or false.");
            case ToolParameterType.Date:
                if (text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return (date, null);
                }

                return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime)
                    ? (DateOnly.FromDateTime(dateTime.Date), null)
                    : (null, "Expected a date (YYYY-MM-DD).");
            case ToolParameterType.DateTime:
                if (text is null || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var moment))
                {
                    return (null, "Expected an ISO 8601 date and time.");
                }

                return parameter.SourceDataType.Contains("offset", StringComparison.OrdinalIgnoreCase) || parameter.SourceDataType.Contains("time zone", StringComparison.OrdinalIgnoreCase)
                    || parameter.SourceDataType.Contains("timestamptz", StringComparison.OrdinalIgnoreCase)
                    ? (moment, null)
                    : (DateTime.SpecifyKind(moment.UtcDateTime, DateTimeKind.Unspecified), null);
            case ToolParameterType.Time:
                return text is not null && TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    ? (time, null)
                    : (null, "Expected a time (HH:MM).");
            case ToolParameterType.Uuid:
                return text is not null && Guid.TryParse(text, out var guid) ? (guid, null) : (null, "Expected a UUID.");
            default:
                if (text is null)
                {
                    return value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                        ? (value.GetRawText(), null)
                        : (null, "Expected text.");
                }

                return parameter.MaxLength is { } max && text.Length > max ? (null, $"Must be at most {max} characters.") : (text, null);
        }
    }

    private static bool InRange(ToolParameter parameter, decimal value) =>
        (parameter.Minimum is null || value >= parameter.Minimum) && (parameter.Maximum is null || value <= parameter.Maximum);

    private static string RangeMessage(ToolParameter parameter) =>
        $"Must be between {parameter.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "-∞"} and {parameter.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "∞"}.";

    private static string Display(object? value) => value switch
    {
        null => "null",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
