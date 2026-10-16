using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;

namespace DatabaseAIAgent.Tools.Generation;

/// <summary>
/// Maps procedure parameters to tool inputs with JSON-schema types. Types that cannot be passed safely as a simple
/// value (binary, XML, spatial, table-valued, arrays, cursors, polymorphic) make the tool unsupported.
/// </summary>
public static class ToolParameterMapper
{
    private static readonly Dictionary<string, (decimal Min, decimal Max)> IntegerRanges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tinyint"] = (0, 255),
        ["smallint"] = (short.MinValue, short.MaxValue),
        ["int2"] = (short.MinValue, short.MaxValue),
        ["mediumint"] = (-8_388_608, 8_388_607),
        ["int"] = (int.MinValue, int.MaxValue),
        ["integer"] = (int.MinValue, int.MaxValue),
        ["int4"] = (int.MinValue, int.MaxValue),
        ["bigint"] = (long.MinValue, long.MaxValue),
        ["int8"] = (long.MinValue, long.MaxValue),
    };

    private static readonly HashSet<string> NumberTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "decimal", "numeric", "money", "smallmoney", "float", "real", "double", "double precision", "float4", "float8",
    };

    private static readonly HashSet<string> BooleanTypes = new(StringComparer.OrdinalIgnoreCase) { "bit", "boolean", "bool" };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar", "text", "ntext", "character", "character varying", "bpchar", "citext", "name",
        "tinytext", "mediumtext", "longtext", "enum", "set", "json", "jsonb", "sysname",
    };

    private static readonly HashSet<string> DateTimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime", "datetime2", "smalldatetime", "timestamp", "timestamp without time zone", "datetimeoffset", "timestamptz", "timestamp with time zone",
    };

    public static (IReadOnlyList<ToolParameter> Parameters, string? UnsupportedReason) Map(StoredProcedure procedure, int defaultMaxTextLength)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        var parameters = new List<ToolParameter>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        string? unsupported = null;

        foreach (var p in procedure.Parameters.Where(p => p.Direction != ParameterDirection.Out).OrderBy(p => p.Ordinal))
        {
            var mapped = MapType(p.DataType);
            if (mapped is null)
            {
                unsupported ??= $"Parameter '{p.Name}' has type '{p.DataType}', which cannot be passed safely by the assistant.";
                continue;
            }

            var name = ToolNaming.ParameterName(p.Name);
            for (var i = 2; !usedNames.Add(name); i++)
            {
                name = ToolNaming.ParameterName(p.Name) + i;
            }

            var (type, min, max) = mapped.Value;
            var maxLength = type == ToolParameterType.String
                ? p.MaxLength is > 0 ? Math.Min(p.MaxLength.Value, defaultMaxTextLength) : defaultMaxTextLength
                : (int?)null;

            parameters.Add(new ToolParameter(name, p.Name, p.Ordinal, type, p.DataType, !p.HasDefault, maxLength, min, max, Describe(p, type)));
        }

        return (parameters, unsupported);
    }

    internal static (ToolParameterType Type, decimal? Min, decimal? Max)? MapType(string dataType)
    {
        var raw = dataType.Trim().ToLowerInvariant();
        if (raw.EndsWith("[]", StringComparison.Ordinal) || raw.Contains("readonly", StringComparison.Ordinal))
        {
            return null; // arrays, table-valued parameters
        }

        var unsigned = raw.Contains("unsigned", StringComparison.Ordinal);
        var baseType = raw.Split('(')[0].Replace("unsigned", string.Empty, StringComparison.Ordinal).Trim();

        if (baseType == "tinyint" && raw.StartsWith("tinyint(1)", StringComparison.Ordinal))
        {
            return (ToolParameterType.Boolean, null, null); // MySQL BOOL
        }

        if (IntegerRanges.TryGetValue(baseType, out var range))
        {
            return (ToolParameterType.Integer, unsigned ? 0 : range.Min, range.Max);
        }

        if (NumberTypes.Contains(baseType))
        {
            return (ToolParameterType.Number, null, null);
        }

        if (BooleanTypes.Contains(baseType))
        {
            return (ToolParameterType.Boolean, null, null);
        }

        if (baseType == "date")
        {
            return (ToolParameterType.Date, null, null);
        }

        if (DateTimeTypes.Contains(baseType))
        {
            return (ToolParameterType.DateTime, null, null);
        }

        if (baseType is "time" or "time without time zone")
        {
            return (ToolParameterType.Time, null, null);
        }

        if (baseType is "uniqueidentifier" or "uuid")
        {
            return (ToolParameterType.Uuid, null, null);
        }

        return TextTypes.Contains(baseType) ? (ToolParameterType.String, null, null) : null;
    }

    private static string Describe(ProcedureParameter parameter, ToolParameterType type)
    {
        var format = type switch
        {
            ToolParameterType.Date => "date, YYYY-MM-DD",
            ToolParameterType.DateTime => "date and time, ISO 8601",
            ToolParameterType.Time => "time, HH:MM",
            ToolParameterType.Uuid => "UUID",
            ToolParameterType.Integer => "whole number",
            ToolParameterType.Number => "number",
            ToolParameterType.Boolean => "true or false",
            _ => "text",
        };
        return $"{ToolNaming.Humanize(parameter.Name)} ({format}){(parameter.HasDefault ? ", optional" : string.Empty)}.";
    }
}
