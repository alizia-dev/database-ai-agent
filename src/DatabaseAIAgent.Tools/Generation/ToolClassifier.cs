using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;

namespace DatabaseAIAgent.Tools.Generation;

/// <summary>
/// Classifies a procedure as READ / WRITE / DELETE / ADMIN with a risk level, conservatively: a tool is READ only
/// when the definition was readable and shows no writes, dynamic SQL or nested calls, or when the engine itself
/// guarantees it cannot change data (PostgreSQL STABLE/IMMUTABLE). Everything uncertain is treated as a write.
/// </summary>
public static class ToolClassifier
{
    private static readonly string[] DeleteVerbs = ["delete", "remove", "purge", "drop", "truncate", "erase", "destroy"];

    private static readonly string[] WriteVerbs =
        ["set", "update", "insert", "add", "create", "save", "upsert", "merge", "approve", "cancel", "close", "post", "process", "assign", "change", "edit", "submit", "import", "sync", "write", "register", "reset"];

    public static ToolClassification Classify(StoredProcedure procedure, DatabaseProviderKind provider)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        var b = procedure.Behavior;

        if (b.ChangesSchema)
        {
            return new(ToolOperation.Admin, ToolRisk.Critical, ClassificationSource.DefinitionAnalysis,
                ["The definition creates, alters or drops database objects, or changes permissions."]);
        }

        if (provider == DatabaseProviderKind.PostgreSql && procedure.DeclaredDataAccess is "STABLE" or "IMMUTABLE" && procedure.Kind != RoutineKind.Procedure)
        {
            return new(ToolOperation.Read, ToolRisk.Low, ClassificationSource.Declaration,
                [$"Declared {procedure.DeclaredDataAccess}: PostgreSQL does not allow it to change data."]);
        }

        if (b.DeletesData)
        {
            return new(ToolOperation.Delete, ToolRisk.Critical, ClassificationSource.DefinitionAnalysis, ["The definition deletes or truncates data."]);
        }

        var reasons = new List<string>();
        if (b.ModifiesData)
        {
            reasons.Add(b.DefinitionAvailable ? "The definition inserts, updates or merges data." : "The routine declares that it modifies data.");
        }

        if (b.UsesDynamicSql)
        {
            reasons.Add("The definition builds and runs SQL dynamically, so its effects cannot be verified.");
        }

        if (b.CallsOtherRoutines)
        {
            reasons.Add("The definition calls other routines whose effects are not verified here.");
        }

        if (reasons.Count > 0)
        {
            return new(ToolOperation.Write, b.ModifiesData ? ToolRisk.High : ToolRisk.Medium,
                b.DefinitionAvailable ? ClassificationSource.DefinitionAnalysis : ClassificationSource.Declaration, reasons);
        }

        if (b.DefinitionAvailable)
        {
            return new(ToolOperation.Read, ToolRisk.Low, ClassificationSource.DefinitionAnalysis, ["The definition only reads data."]);
        }

        // Definition not visible to the connection's account: never assume read-only.
        var words = ToolNaming.ToSnakeCase(procedure.Name).Split('_');
        if (words.Any(w => DeleteVerbs.Contains(w)))
        {
            return new(ToolOperation.Delete, ToolRisk.Critical, ClassificationSource.Unverified,
                ["The definition is not visible to the connection's account, and the name suggests it deletes data."]);
        }

        return new(ToolOperation.Write, words.Any(w => WriteVerbs.Contains(w)) ? ToolRisk.High : ToolRisk.Medium, ClassificationSource.Unverified,
        [
            "The definition is not visible to the connection's account, so it is treated as a write.",
            "An administrator who knows the procedure only reads data can confirm it as read-only. It will still run in a read-only transaction.",
        ]);
    }

    /// <summary>An administrator's confirmation that an unverified routine only reads data.</summary>
    public static ToolClassification ConfirmedReadOnly(string administrator, DateTimeOffset at) =>
        new(ToolOperation.Read, ToolRisk.Low, ClassificationSource.AdministratorConfirmed,
        [
            $"Confirmed as read-only by {administrator} on {at:yyyy-MM-dd}. The definition could not be verified automatically.",
            "Executions run in a read-only transaction, so accidental changes are rejected or rolled back.",
        ]);
}
