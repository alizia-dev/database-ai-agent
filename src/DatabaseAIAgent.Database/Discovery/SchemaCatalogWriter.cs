using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Databases;

namespace DatabaseAIAgent.Database.Discovery;

/// <summary>
/// Merges a discovery result into the stored catalog. Tables and procedures are matched by natural key so their IDs
/// stay stable (AI tools reference procedures by ID). Tables that disappeared are removed; procedures that disappeared
/// are kept and flagged missing, so tools built on them can be disabled rather than silently broken.
/// Changes are tracked; the caller saves them in one transaction.
/// </summary>
public sealed class SchemaCatalogWriter(ISchemaMetadataRepository metadata)
{
    public async Task<AnalysisCounts> ApplyAsync(DatabaseConnection connection, DiscoveredDatabase discovered, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(discovered);

        var existingTables = (await metadata.ListTablesAsync(connection.Id, cancellationToken))
            .ToDictionary(t => (t.SchemaName, t.Name));
        var seenTables = new HashSet<(string, string)>();
        foreach (var t in discovered.Tables)
        {
            var key = (t.Schema, t.Name);
            if (!seenTables.Add(key))
            {
                continue;
            }

            if (!existingTables.TryGetValue(key, out var table))
            {
                table = new DatabaseTable(connection.Id, t.Schema, t.Name, now);
                metadata.AddTable(table);
            }

            table.Update(
                t.Kind,
                t.Description,
                t.EstimatedRowCount,
                t.Columns.Select(c => new DatabaseColumn(c.Name, c.Ordinal, c.DataType, c.MaxLength, c.Precision, c.Scale, c.IsNullable, c.IsPrimaryKey, c.IsIdentity, c.Description)),
                t.Indexes.Select(i => new DatabaseIndex(i.Name, i.IsUnique, i.IsPrimaryKey, i.Columns)),
                now);
        }

        foreach (var (key, table) in existingTables)
        {
            if (!seenTables.Contains(key))
            {
                metadata.RemoveTable(table);
            }
        }

        var existingProcedures = (await metadata.ListProceduresAsync(connection.Id, cancellationToken))
            .ToDictionary(p => (p.SchemaName, p.Name, p.Signature));
        var seenProcedures = new HashSet<(string, string, string)>();
        var newProcedures = 0;
        foreach (var r in discovered.Routines)
        {
            var key = (r.Schema, r.Name, r.Signature);
            if (!seenProcedures.Add(key))
            {
                continue;
            }

            if (!existingProcedures.TryGetValue(key, out var procedure))
            {
                procedure = new StoredProcedure(connection.Id, r.Schema, r.Name, r.Signature, now);
                metadata.AddProcedure(procedure);
                newProcedures++;
            }

            procedure.Update(
                r.Kind,
                r.Description,
                r.ReturnType,
                r.CanExecute,
                RoutineDefinitionAnalyzer.Analyze(connection.Provider, r.Definition, r.DeclaredDataAccess),
                r.DeclaredDataAccess,
                RoutineDefinitionAnalyzer.Hash(r.Definition),
                r.AverageDurationMs,
                r.Parameters.Select(p => new ProcedureParameter(p.Name, p.Ordinal, p.DataType, p.MaxLength, p.Precision, p.Scale, p.Direction, p.HasDefault)),
                r.ResultColumns.Select(c => new ResultColumn(c.Name, c.Ordinal, c.DataType, c.IsNullable)),
                now);
        }

        var missing = 0;
        foreach (var (key, procedure) in existingProcedures)
        {
            if (!seenProcedures.Contains(key) && !procedure.IsMissing)
            {
                procedure.MarkMissing();
                missing++;
            }
        }

        var schemaNames = discovered.Schemas
            .Concat(discovered.Tables.Select(t => t.Schema))
            .Concat(discovered.Routines.Select(r => r.Schema))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        await metadata.ReplaceSchemasAndRelationshipsAsync(
            connection.Id,
            schemaNames.Select(s => new DatabaseSchema(connection.Id, s)),
            discovered.Relationships.Select(r => new DatabaseRelationship(connection.Id, r.Name, r.FromSchema, r.FromTable, r.FromColumns, r.ToSchema, r.ToTable, r.ToColumns)),
            cancellationToken);

        return new AnalysisCounts(
            schemaNames.Count,
            discovered.Tables.Count(t => t.Kind == TableKind.Table),
            discovered.Tables.Count(t => t.Kind == TableKind.View),
            discovered.Tables.Sum(t => t.Columns.Count),
            discovered.Relationships.Count,
            seenProcedures.Count,
            newProcedures,
            missing);
    }
}
