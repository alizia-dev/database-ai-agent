using DatabaseAIAgent.Database.Discovery;
using DatabaseAIAgent.Domain.Databases;

namespace DatabaseAIAgent.UnitTests.Databases;

public sealed class RoutineDefinitionAnalyzerTests
{
    private static RoutineBehavior SqlServer(string definition) => RoutineDefinitionAnalyzer.Analyze(DatabaseProviderKind.SqlServer, definition, null);

    [Fact]
    public void PlainSelect_IsReadOnly()
    {
        var behavior = SqlServer("""
            CREATE PROCEDURE dbo.GetOutstandingInvoices @MinBalance decimal(12,2) = 0
            AS
            BEGIN
                SET NOCOUNT ON;
                -- UPDATE comments must not count
                /* DELETE FROM dbo.Invoices */
                SELECT [Insert Date], c.Name, 'DELETE ME' AS Note
                FROM dbo.Invoices i WITH (NOLOCK) JOIN dbo.Customers c ON c.Id = i.CustomerId
                WHERE i.Balance > @MinBalance;
            END
            """);

        Assert.True(behavior.DefinitionAvailable);
        Assert.True(behavior.IsReadOnly);
    }

    [Fact]
    public void TempTablesAndTableVariables_DoNotCountAsWrites()
    {
        var behavior = SqlServer("""
            CREATE PROC dbo.Report AS
            DECLARE @t TABLE (Id int);
            CREATE TABLE #work (Id int);
            INSERT INTO #work SELECT Id FROM dbo.Orders;
            INSERT @t SELECT Id FROM #work;
            UPDATE #work SET Id = Id + 1;
            DELETE FROM @t WHERE Id < 0;
            SELECT * FROM #work;
            """);

        Assert.True(behavior.IsReadOnly);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE p AS UPDATE dbo.Customers SET CreditLimit = 5", true, false, false)]
    [InlineData("CREATE PROCEDURE p AS INSERT INTO dbo.Audit (Id) VALUES (1)", true, false, false)]
    [InlineData("CREATE PROCEDURE p AS MERGE dbo.Stock AS t USING src ON 1=1 WHEN MATCHED THEN UPDATE SET Qty = 1;", true, false, false)]
    [InlineData("CREATE PROCEDURE p AS DELETE FROM dbo.Orders WHERE Id = 1", false, true, false)]
    [InlineData("CREATE PROCEDURE p AS TRUNCATE TABLE dbo.Logs", false, true, false)]
    [InlineData("CREATE PROCEDURE p AS DROP TABLE dbo.Logs", false, false, true)]
    [InlineData("CREATE PROCEDURE p AS GRANT SELECT ON dbo.Orders TO public", false, false, true)]
    public void Writes_Deletes_AndDdl_AreDetected(string definition, bool modifies, bool deletes, bool ddl)
    {
        var behavior = SqlServer(definition);

        Assert.Equal((modifies, deletes, ddl), (behavior.ModifiesData, behavior.DeletesData, behavior.ChangesSchema));
        Assert.False(behavior.IsReadOnly);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE p @sql nvarchar(max) AS EXEC sp_executesql @sql", true, false)]
    [InlineData("CREATE PROCEDURE p @sql nvarchar(max) AS EXEC(@sql)", true, false)]
    [InlineData("CREATE PROCEDURE p @sql nvarchar(max) AS EXEC @sql", true, false)]
    [InlineData("CREATE PROCEDURE p AS EXEC dbo.OtherProc 1", false, true)]
    [InlineData("CREATE PROCEDURE p AS DECLARE @rc int; EXEC @rc = dbo.OtherProc", false, true)]
    public void DynamicSqlAndNestedCalls_AreNeverConsideredReadOnly(string definition, bool dynamic, bool calls)
    {
        var behavior = SqlServer(definition);

        Assert.Equal((dynamic, calls), (behavior.UsesDynamicSql, behavior.CallsOtherRoutines));
        Assert.False(behavior.IsReadOnly);
    }

    [Fact]
    public void SelectForUpdate_IsNotAWrite()
    {
        var behavior = RoutineDefinitionAnalyzer.Analyze(DatabaseProviderKind.PostgreSql, "BEGIN SELECT id FROM orders WHERE id = p_id FOR UPDATE; END", "VOLATILE");

        Assert.False(behavior.ModifiesData);
    }

    [Fact]
    public void Postgres_ExecuteIsDynamic_AndCallIsNested()
    {
        Assert.True(RoutineDefinitionAnalyzer.Analyze(DatabaseProviderKind.PostgreSql, "BEGIN EXECUTE format('SELECT * FROM %I', t); END", null).UsesDynamicSql);
        Assert.True(RoutineDefinitionAnalyzer.Analyze(DatabaseProviderKind.PostgreSql, "BEGIN CALL refresh_totals(); END", null).CallsOtherRoutines);
        Assert.True(RoutineDefinitionAnalyzer.Analyze(DatabaseProviderKind.PostgreSql, "SELECT id, name FROM customers", "STABLE").IsReadOnly);
    }

    [Fact]
    public void MySql_DeclaredModifiesSqlData_CountsEvenWithoutDefinition()
    {
        var hidden = RoutineDefinitionAnalyzer.Analyze(DatabaseProviderKind.MySql, null, "MODIFIES SQL DATA");
        var prepared = RoutineDefinitionAnalyzer.Analyze(DatabaseProviderKind.MySql, "BEGIN PREPARE s FROM @q; EXECUTE s; END", "READS SQL DATA");

        Assert.False(hidden.DefinitionAvailable);
        Assert.True(hidden.ModifiesData);
        Assert.False(hidden.IsReadOnly);
        Assert.True(prepared.UsesDynamicSql);
    }

    [Fact]
    public void UnreadableDefinition_IsUnknownAndNotReadOnly()
    {
        var behavior = SqlServer(null!);

        Assert.Equal(RoutineBehavior.Unknown, behavior);
        Assert.False(behavior.IsReadOnly);
    }

    [Fact]
    public void SqlServerParameterDefaults_AreReadFromTheHeader()
    {
        var defaults = RoutineDefinitionAnalyzer.SqlServerParametersWithDefaults("""
            CREATE PROCEDURE dbo.GetCustomerOrders
                @CustomerId int,
                @FromDate date = NULL,
                @ToDate date = '2099-12-31',
                @Top int = 100 -- default
            AS
            SELECT @x = 1
            """);

        Assert.Equal(["@FromDate", "@ToDate", "@Top"], defaults.Order());
    }

    [Fact]
    public void Hash_ChangesWithTheDefinition()
    {
        Assert.Equal(RoutineDefinitionAnalyzer.Hash("SELECT 1"), RoutineDefinitionAnalyzer.Hash("SELECT 1"));
        Assert.NotEqual(RoutineDefinitionAnalyzer.Hash("SELECT 1"), RoutineDefinitionAnalyzer.Hash("SELECT 2"));
        Assert.Null(RoutineDefinitionAnalyzer.Hash(null));
    }
}
