using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;

namespace DatabaseAIAgent.UnitTests.Tools;

internal static class ToolFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public static RoutineBehavior ReadsOnly => new(true, false, false, false, false, false);

    public static StoredProcedure Procedure(
        string name = "get_customer_orders",
        RoutineKind kind = RoutineKind.Procedure,
        RoutineBehavior? behavior = null,
        string? declaredDataAccess = null,
        bool canExecute = true,
        params ProcedureParameter[] parameters)
    {
        var procedure = new StoredProcedure(Guid.NewGuid(), "dbo", name, $"{name}()", Now);
        procedure.Update(kind, null, null, canExecute, behavior ?? ReadsOnly, declaredDataAccess, "hash-1", null, parameters, [], Now);
        return procedure;
    }

    public static ProcedureParameter Parameter(string name, string type, int ordinal = 1, bool hasDefault = false, int? maxLength = null, ParameterDirection direction = ParameterDirection.In) =>
        new(name, ordinal, type, maxLength, null, null, direction, hasDefault);

    public static AiTool Tool(ToolOperation operation = ToolOperation.Read, params ToolParameter[] parameters)
    {
        var tool = new AiTool(Guid.NewGuid(), Guid.NewGuid(), "get_customer_orders", "Orders for a customer.",
            new ToolClassification(operation, operation == ToolOperation.Read ? ToolRisk.Low : ToolRisk.High, ClassificationSource.DefinitionAnalysis, ["test"]),
            "hash-1", Now);
        tool.SetParameters(parameters, null);
        return tool;
    }
}
