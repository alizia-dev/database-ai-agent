using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;
using DatabaseAIAgent.Tools.Generation;
using static DatabaseAIAgent.UnitTests.Tools.ToolFixtures;

namespace DatabaseAIAgent.UnitTests.Tools;

public sealed class ToolGenerationTests
{
    [Theory]
    [InlineData("GetCustomerOrders", "get_customer_orders")]
    [InlineData("usp_GetCustomerOrders", "get_customer_orders")]
    [InlineData("get-customer orders", "get_customer_orders")]
    [InlineData("HTMLReportV2", "html_report_v2")]
    [InlineData("2024_totals", "tool_2024_totals")]
    public void ToolNames_AreSnakeCase(string routine, string expected) =>
        Assert.Equal(expected, ToolNaming.UniqueToolName("dbo", routine, new HashSet<string>()));

    [Fact]
    public void ToolNames_AreMadeUnique()
    {
        var taken = new HashSet<string> { "get_orders", "sales_get_orders" };

        Assert.Equal("sales_get_orders_2", ToolNaming.UniqueToolName("sales", "get_orders", taken));
        Assert.Equal("get_orders_2", ToolNaming.UniqueToolName("dbo", "get_orders", taken));
        Assert.True(AiTool.IsValidName(ToolNaming.UniqueToolName("dbo", new string('x', 200), new HashSet<string>())));
    }

    [Theory]
    [InlineData("@CustomerId", "customerId")]
    [InlineData("p_min_balance", "minBalance")]
    [InlineData("in_from_date", "fromDate")]
    [InlineData("@1st", "p1st")]
    public void ParameterNames_AreCamelCase(string source, string expected) =>
        Assert.Equal(expected, ToolNaming.ParameterName(source));

    [Theory]
    [InlineData("int", ToolParameterType.Integer)]
    [InlineData("bigint", ToolParameterType.Integer)]
    [InlineData("numeric(12,2)", ToolParameterType.Number)]
    [InlineData("bit", ToolParameterType.Boolean)]
    [InlineData("tinyint(1)", ToolParameterType.Boolean)]
    [InlineData("nvarchar", ToolParameterType.String)]
    [InlineData("character varying(100)", ToolParameterType.String)]
    [InlineData("date", ToolParameterType.Date)]
    [InlineData("datetime2", ToolParameterType.DateTime)]
    [InlineData("timestamp with time zone", ToolParameterType.DateTime)]
    [InlineData("uniqueidentifier", ToolParameterType.Uuid)]
    public void SupportedTypes_Map(string dataType, ToolParameterType expected) =>
        Assert.Equal(expected, ToolParameterMapper.MapType(dataType)!.Value.Type);

    [Theory]
    [InlineData("varbinary")]
    [InlineData("xml")]
    [InlineData("geography")]
    [InlineData("integer[]")]
    [InlineData("dbo.OrderLines READONLY")]
    [InlineData("refcursor")]
    public void UnsafeTypes_AreUnsupported(string dataType) => Assert.Null(ToolParameterMapper.MapType(dataType));

    [Fact]
    public void Mapping_UsesDefaultsForRequiredness_ExcludesOutputs_AndBoundsValues()
    {
        var procedure = Procedure(parameters:
        [
            Parameter("@CustomerId", "int", 1),
            Parameter("@Status", "nvarchar", 2, hasDefault: true, maxLength: 20),
            Parameter("@Note", "nvarchar", 3, hasDefault: true, maxLength: -1),
            Parameter("@Total", "money", 4, direction: ParameterDirection.Out),
            Parameter("@Age", "tinyint", 5, hasDefault: true),
        ]);

        var (parameters, unsupported) = ToolParameterMapper.Map(procedure, 4000);

        Assert.Null(unsupported);
        Assert.Equal(["customerId", "status", "note", "age"], parameters.Select(p => p.Name));
        Assert.True(parameters[0].Required);
        Assert.False(parameters[1].Required);
        Assert.Equal(20, parameters[1].MaxLength);
        Assert.Equal(4000, parameters[2].MaxLength); // MAX → default cap
        Assert.Equal((0m, 255m), (parameters[3].Minimum!.Value, parameters[3].Maximum!.Value));
    }

    [Fact]
    public void Mapping_ReportsUnsupportedParameters()
    {
        var (_, unsupported) = ToolParameterMapper.Map(Procedure(parameters: [Parameter("@Photo", "varbinary")]), 4000);

        Assert.Contains("@Photo", unsupported, StringComparison.Ordinal);
    }

    [Fact]
    public void Classifier_ReadOnlyDefinition_IsRead() =>
        Assert.Equal((ToolOperation.Read, ToolRisk.Low, ClassificationSource.DefinitionAnalysis), Shape(ToolClassifier.Classify(Procedure(), DatabaseProviderKind.SqlServer)));

    [Theory]
    [InlineData(true, false, false, false, false, ToolOperation.Write, ToolRisk.High)]
    [InlineData(false, true, false, false, false, ToolOperation.Delete, ToolRisk.Critical)]
    [InlineData(false, false, true, false, false, ToolOperation.Admin, ToolRisk.Critical)]
    [InlineData(false, false, false, true, false, ToolOperation.Write, ToolRisk.Medium)]
    [InlineData(false, false, false, false, true, ToolOperation.Write, ToolRisk.Medium)]
    public void Classifier_AnythingBeyondReading_IsNotRead(bool modifies, bool deletes, bool ddl, bool dynamicSql, bool calls, ToolOperation operation, ToolRisk risk)
    {
        var behavior = new RoutineBehavior(true, modifies, deletes, ddl, dynamicSql, calls);

        var classification = ToolClassifier.Classify(Procedure(behavior: behavior), DatabaseProviderKind.SqlServer);

        Assert.Equal((operation, risk), (classification.Operation, classification.Risk));
        Assert.NotEmpty(classification.Reasons);
    }

    [Fact]
    public void Classifier_PostgresStableFunction_IsReadByDeclaration()
    {
        var function = Procedure(kind: RoutineKind.TableFunction, behavior: RoutineBehavior.Unknown, declaredDataAccess: "STABLE");

        Assert.Equal((ToolOperation.Read, ToolRisk.Low, ClassificationSource.Declaration), Shape(ToolClassifier.Classify(function, DatabaseProviderKind.PostgreSql)));
        // The same declaration on another engine is not trusted.
        Assert.NotEqual(ToolOperation.Read, ToolClassifier.Classify(function, DatabaseProviderKind.MySql).Operation);
    }

    [Theory]
    [InlineData("GetOrders", ToolOperation.Write, ToolRisk.Medium)]
    [InlineData("UpdateOrders", ToolOperation.Write, ToolRisk.High)]
    [InlineData("PurgeOldOrders", ToolOperation.Delete, ToolRisk.Critical)]
    public void Classifier_UnreadableDefinition_IsNeverRead(string name, ToolOperation operation, ToolRisk risk)
    {
        var classification = ToolClassifier.Classify(Procedure(name, behavior: RoutineBehavior.Unknown), DatabaseProviderKind.SqlServer);

        Assert.Equal((operation, risk, ClassificationSource.Unverified), Shape(classification));
    }

    private static (ToolOperation, ToolRisk, ClassificationSource) Shape(ToolClassification c) => (c.Operation, c.Risk, c.Source);
}
