using System.Text.Json.Nodes;
using DatabaseAIAgent.Domain.Tools;

namespace DatabaseAIAgent.Tools;

/// <summary>Builds the JSON Schema (draft 2020-12 subset accepted by OpenAI and Anthropic) describing a tool's arguments.</summary>
public static class ToolJsonSchema
{
    public static JsonObject For(AiTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var properties = new JsonObject();
        foreach (var p in tool.Parameters)
        {
            var schema = new JsonObject();
            switch (p.Type)
            {
                case ToolParameterType.Integer:
                    schema["type"] = "integer";
                    break;
                case ToolParameterType.Number:
                    schema["type"] = "number";
                    break;
                case ToolParameterType.Boolean:
                    schema["type"] = "boolean";
                    break;
                case ToolParameterType.Date:
                    schema["type"] = "string";
                    schema["format"] = "date";
                    break;
                case ToolParameterType.DateTime:
                    schema["type"] = "string";
                    schema["format"] = "date-time";
                    break;
                case ToolParameterType.Time:
                    schema["type"] = "string";
                    schema["format"] = "time";
                    break;
                case ToolParameterType.Uuid:
                    schema["type"] = "string";
                    schema["format"] = "uuid";
                    break;
                default:
                    schema["type"] = "string";
                    if (p.MaxLength is { } max)
                    {
                        schema["maxLength"] = max;
                    }

                    break;
            }

            if (p.Type is ToolParameterType.Integer or ToolParameterType.Number)
            {
                if (p.Minimum is { } min && min > long.MinValue)
                {
                    schema["minimum"] = min;
                }

                if (p.Maximum is { } maximum && maximum < long.MaxValue)
                {
                    schema["maximum"] = maximum;
                }
            }

            if (p.Description is not null)
            {
                schema["description"] = p.Description;
            }

            properties[p.Name] = schema;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(tool.Parameters.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
            ["additionalProperties"] = false,
        };
    }
}
