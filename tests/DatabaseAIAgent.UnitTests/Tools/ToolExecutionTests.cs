using System.Text.Json;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Database.Execution;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Common;
using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;
using DatabaseAIAgent.Tools;
using DatabaseAIAgent.Tools.Execution;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using static DatabaseAIAgent.UnitTests.Tools.ToolFixtures;

namespace DatabaseAIAgent.UnitTests.Tools;

public sealed class ToolExecutionTests
{
    private static readonly ToolParameter[] OrderParameters =
    [
        new("customerId", "@CustomerId", 1, ToolParameterType.Integer, "int", true, null, int.MinValue, int.MaxValue, null),
        new("status", "@Status", 2, ToolParameterType.String, "nvarchar", false, 10, null, null, null),
        new("fromDate", "@FromDate", 3, ToolParameterType.Date, "date", false, null, null, null, null),
    ];

    [Fact]
    public void Validator_ConvertsTypedValues_AndRedactsText()
    {
        var tool = Tool(ToolOperation.Read, OrderParameters);

        var (result, errors) = ToolArgumentValidator.Validate(tool, Args("""{"customerId": 42, "status": "open", "fromDate": "2026-01-31"}"""));

        Assert.Empty(errors);
        Assert.Equal([42L, "open", new DateOnly(2026, 1, 31)], result!.Arguments.Select(a => a.Value));
        Assert.Equal(["@CustomerId", "@Status", "@FromDate"], result.Arguments.Select(a => a.SourceName));
        Assert.DoesNotContain("open", result.RedactedJson, StringComparison.Ordinal);
        Assert.Contains("[text, 4 characters]", result.RedactedJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{}""", "customerId", "required")]
    [InlineData("""{"customerId": "abc"}""", "customerId", "whole number")]
    [InlineData("""{"customerId": 1.5}""", "customerId", "whole number")]
    [InlineData("""{"customerId": 99999999999}""", "customerId", "between")]
    [InlineData("""{"customerId": 1, "status": "far-too-long-status"}""", "status", "at most 10")]
    [InlineData("""{"customerId": 1, "fromDate": "31/01/2026"}""", "fromDate", "YYYY-MM-DD")]
    [InlineData("""{"customerId": 1, "sql": "DROP TABLE x"}""", "sql", "Unknown argument")]
    public void Validator_RejectsBadArguments(string json, string field, string message)
    {
        var (result, errors) = ToolArgumentValidator.Validate(Tool(ToolOperation.Read, OrderParameters), Args(json));

        Assert.Null(result);
        Assert.Contains(message, errors[field][0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsNonObjectArguments() =>
        Assert.True(ToolArgumentValidator.Validate(Tool(ToolOperation.Read, OrderParameters), Args("[1,2]")).Errors.ContainsKey("arguments"));

    [Fact]
    public void JsonSchema_DescribesParametersStrictly()
    {
        var schema = ToolJsonSchema.For(Tool(ToolOperation.Read, OrderParameters));

        Assert.Equal("object", (string?)schema["type"]);
        Assert.False((bool)schema["additionalProperties"]!);
        Assert.Equal(["customerId"], schema["required"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("integer", (string?)schema["properties"]!["customerId"]!["type"]);
        Assert.Equal(10, (int)schema["properties"]!["status"]!["maxLength"]!);
        Assert.Equal("date", (string?)schema["properties"]!["fromDate"]!["format"]);
    }

    [Fact]
    public void Tool_Lifecycle_ApprovalAndChanges()
    {
        var tool = Tool(ToolOperation.Read);
        Assert.Equal(ToolStatus.PendingReview, tool.Status);
        Assert.False(tool.IsAvailable);
        Assert.True(tool.AllowsRole(SystemRoles.Viewer));

        tool.Approve(Guid.NewGuid(), Now);
        Assert.True(tool.IsAvailable);

        tool.SourceChanged("hash-2");
        Assert.Equal((ToolStatus.Disabled, ToolDisabledReason.ProcedureChanged), (tool.Status, tool.DisabledReason));

        tool.Reject("not needed");
        Assert.Throws<DomainException>(() => tool.Approve(Guid.NewGuid(), Now));
        tool.Reopen();
        Assert.Equal(ToolStatus.PendingReview, tool.Status);
    }

    [Fact]
    public void Tool_AdminOperations_CanNeverBeApproved()
    {
        var tool = Tool(ToolOperation.Admin);

        Assert.Throws<DomainException>(() => tool.Approve(Guid.NewGuid(), Now));
    }

    [Fact]
    public void Tool_WriteDefaults_RequireConfirmationAndRestrictRoles()
    {
        var tool = Tool(ToolOperation.Write);

        Assert.True(tool.RequiresConfirmation);
        Assert.False(tool.AllowsRole(SystemRoles.Analyst));
        Assert.True(tool.AllowsRole(SystemRoles.Admin));
        Assert.Throws<DomainException>(() => tool.SetLimits(0, 10, 4096));
        Assert.Throws<DomainException>(() => tool.SetAllowedRoles(["NotARole"]));
    }

    [Fact]
    public void Commands_SqlServerProcedure_UsesStoredProcedureCommandWithParameters()
    {
        var procedure = Procedure("GetOrders", parameters:
        [
            Parameter("@CustomerId", "int", 1),
            Parameter("@Status", "nvarchar", 2, hasDefault: true),
            Parameter("@Total", "money", 3, direction: ParameterDirection.Out),
        ]);
        using var connection = new SqlConnection();

        using var command = CustomerProcedureInvoker.BuildCommand(connection, DatabaseProviderKind.SqlServer, Invocation(procedure, ("@CustomerId", ToolParameterType.Integer, 42L)));

        Assert.Equal(System.Data.CommandType.StoredProcedure, command.CommandType);
        Assert.Equal("[dbo].[GetOrders]", command.CommandText);
        Assert.Equal(["@CustomerId", "@Total"], command.Parameters.Cast<SqlParameter>().Select(p => p.ParameterName)); // omitted optional → server default
        Assert.Equal(42, command.Parameters[0].Value);
        Assert.Equal(System.Data.ParameterDirection.Output, command.Parameters[1].Direction);
    }

    [Fact]
    public void Commands_PostgresFunction_UsesNamedNotationAndPlaceholders()
    {
        var procedure = new StoredProcedure(Guid.NewGuid(), "reporting", "top \"customers\"", "x", Now);
        procedure.Update(RoutineKind.TableFunction, null, null, true, ReadsOnly, "STABLE", "h", null,
            [Parameter("p_min", "numeric", 1), Parameter("p_limit", "integer", 2, hasDefault: true)], [], Now);
        using var connection = new NpgsqlConnection();

        using var command = CustomerProcedureInvoker.BuildCommand(connection, DatabaseProviderKind.PostgreSql, Invocation(procedure, ("p_min", ToolParameterType.Number, 10m)));

        Assert.Equal("SELECT * FROM \"reporting\".\"top \"\"customers\"\"\"(\"p_min\" => @p0)", command.CommandText);
        Assert.Equal(10m, command.Parameters[0].Value);
    }

    [Fact]
    public void Commands_MySqlScalarFunction_PassesNullForOmittedArguments()
    {
        var procedure = new StoredProcedure(Guid.NewGuid(), "crm", "balance_for", "x", Now);
        procedure.Update(RoutineKind.ScalarFunction, null, null, true, ReadsOnly, null, "h", null,
            [Parameter("customer_id", "int", 1), Parameter("as_of", "date", 2, hasDefault: true)], [], Now);
        using var connection = new MySqlConnection();

        using var command = CustomerProcedureInvoker.BuildCommand(connection, DatabaseProviderKind.MySql, Invocation(procedure, ("customer_id", ToolParameterType.Integer, 7L)));

        Assert.Equal("SELECT `crm`.`balance_for`(@p0, NULL) AS `result`", command.CommandText);
    }

    [Theory]
    [InlineData("integer", typeof(int))]
    [InlineData("int4", typeof(int))]
    [InlineData("smallint", typeof(short))]
    [InlineData("bigint", typeof(long))]
    public void Commands_IntegersAreSentAtTheDeclaredWidth(string dataType, Type expected) =>
        Assert.IsType(expected, CustomerProcedureInvoker.Narrow(dataType, 7L));

    [Fact]
    public void CircuitBreaker_OpensAfterRepeatedFailures_AndClosesAfterTheCooldown()
    {
        var gate = new ConnectionGate(new ExecutionOptions { CircuitBreakerFailures = 2, CircuitBreakerSeconds = 30 });

        gate.RecordFailure(Now);
        gate.ThrowIfOpen(Now); // one failure: still closed
        gate.RecordFailure(Now);

        var ex = Assert.Throws<ProcedureExecutionException>(() => gate.ThrowIfOpen(Now.AddSeconds(5)));
        Assert.Equal(ProcedureFailure.Unavailable, ex.Failure);
        gate.ThrowIfOpen(Now.AddSeconds(31));
    }

    private static ProcedureInvocation Invocation(StoredProcedure procedure, params (string Name, ToolParameterType Type, object Value)[] arguments) =>
        new(procedure.ConnectionId, procedure, arguments.Select(a => new ProcedureArgument(a.Name, a.Type, a.Value)).ToList(), true, 30, 100, 1024 * 1024);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
