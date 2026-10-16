using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Database;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Database.Discovery;
using DatabaseAIAgent.Database.Execution;
using DatabaseAIAgent.Domain.Tools;
using DatabaseAIAgent.Database.Providers;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace DatabaseAIAgent.IntegrationTests;

internal static class Accounts
{
    public const string Database = "crm";
    public const string Reporter = "reporter";
    public const string ReporterPassword = "Rep0rter!Pass#2026";
    public static readonly IOptions<DatabaseConnectionOptions> Options = Microsoft.Extensions.Options.Options.Create(new DatabaseConnectionOptions { ConnectTimeoutSeconds = 10 });
}

/// <summary>Shared assertions: the same behaviour is required from every provider.</summary>
public abstract class ProviderContractTests<TContainer>(ContainerFixture<TContainer> fixture)
    where TContainer : DotNet.Testcontainers.Containers.IContainer
{
    protected abstract ICustomerDatabaseProvider Provider { get; }

    protected abstract ResolvedConnection Admin { get; }

    protected ResolvedConnection Reporter => Admin with { Username = Accounts.Reporter, Password = Accounts.ReporterPassword };

    protected abstract ISchemaDiscoverer Discoverer { get; }

    [Fact]
    public async Task Discovery_ReadsTablesKeysRoutinesAndBehaviour()
    {
        fixture.SkipIfUnavailable();

        var discovered = await DiscoverAsync(Admin);

        var customers = discovered.Tables.Single(t => t.Name.Equals("customers", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, customers.Columns.Count);
        Assert.True(customers.Columns.Single(c => c.Name.Equals("id", StringComparison.OrdinalIgnoreCase)).IsPrimaryKey);
        Assert.Contains(discovered.Relationships, r => r.FromTable.Equals("invoices", StringComparison.OrdinalIgnoreCase) && r.ToTable.Equals("customers", StringComparison.OrdinalIgnoreCase));

        var reader = discovered.Routines.Single(r => r.Name.Equals(ReaderRoutine, StringComparison.OrdinalIgnoreCase));
        var writer = discovered.Routines.Single(r => r.Name.Equals(WriterRoutine, StringComparison.OrdinalIgnoreCase));
        Assert.True(Analyze(reader).IsReadOnly);
        Assert.True(Analyze(writer).ModifiesData);
        Assert.False(Analyze(writer).IsReadOnly);
    }

    [Fact]
    public async Task Discovery_WithLeastPrivilegeAccount_SeesTheGrantedRoutine()
    {
        fixture.SkipIfUnavailable();

        var discovered = await DiscoverAsync(Reporter);

        var reader = discovered.Routines.Single(r => r.Name.Equals(ReaderRoutine, StringComparison.OrdinalIgnoreCase));
        Assert.True(reader.CanExecute);
    }

    protected virtual string ReaderRoutine => "GetCustomers";

    protected virtual string WriterRoutine => "SetCreditLimit";

    private DatabaseAIAgent.Domain.Databases.RoutineBehavior Analyze(DiscoveredRoutine routine) =>
        DatabaseAIAgent.Database.Discovery.RoutineDefinitionAnalyzer.Analyze(Provider.Kind, routine.Definition, routine.DeclaredDataAccess);

    private async Task<DiscoveredDatabase> DiscoverAsync(ResolvedConnection connection)
    {
        await using var db = Provider.CreateConnection(connection, 10);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await Discoverer.DiscoverAsync(db, new DiscoveryLimits(1000, 1000, 60), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Invoker_RunsTheGrantedReadRoutineWithTheLeastPrivilegeAccount()
    {
        fixture.SkipIfUnavailable();
        var procedure = await ProcedureAsync(ReaderRoutine);

        var result = await InvokerFor(Reporter).InvokeAsync(
            new ProcedureInvocation(procedure.ConnectionId, procedure, [], ReadOnly: true, 30, 100, 1024 * 1024), TestContext.Current.CancellationToken);

        Assert.Equal(["id", "name", "balance"], result.Columns.Select(c => c.ToLowerInvariant()));
        Assert.Single(result.Rows);
    }

    [Fact]
    public async Task Invoker_ReadOnlyCall_CannotChangeData()
    {
        fixture.SkipIfUnavailable();
        var procedure = await ProcedureAsync(WriterRoutine);
        var idParameter = procedure.Parameters.OrderBy(p => p.Ordinal).First();
        var limitParameter = procedure.Parameters.OrderBy(p => p.Ordinal).Last();

        // Even a misclassified writer cannot persist changes when run as read-only: PostgreSQL and MySQL reject the
        // write; SQL Server's transaction is rolled back.
        try
        {
            await InvokerFor(Admin).InvokeAsync(new ProcedureInvocation(procedure.ConnectionId, procedure,
                [
                    new ProcedureArgument(idParameter.Name, ToolParameterType.Integer, 1L),
                    new ProcedureArgument(limitParameter.Name, ToolParameterType.Number, 999m),
                ],
                ReadOnly: true, 30, 100, 1024 * 1024), TestContext.Current.CancellationToken);
        }
        catch (ProcedureExecutionException ex)
        {
            Assert.Equal(ProcedureFailure.ReadOnlyViolation, ex.Failure);
        }

        var reader = await ProcedureAsync(ReaderRoutine);
        var rows = await InvokerFor(Admin).InvokeAsync(
            new ProcedureInvocation(reader.ConnectionId, reader, [], ReadOnly: true, 30, 100, 1024 * 1024), TestContext.Current.CancellationToken);
        Assert.Equal(100m, Convert.ToDecimal(rows.Rows[0][2], System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<StoredProcedure> ProcedureAsync(string name)
    {
        var routine = (await DiscoverAsync(Admin)).Routines.Single(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var procedure = new StoredProcedure(Guid.NewGuid(), routine.Schema, routine.Name, routine.Signature, DateTimeOffset.UtcNow);
        procedure.Update(routine.Kind, routine.Description, routine.ReturnType, routine.CanExecute, RoutineBehavior.Unknown, routine.DeclaredDataAccess, null, null,
            routine.Parameters.Select(p => new ProcedureParameter(p.Name, p.Ordinal, p.DataType, p.MaxLength, p.Precision, p.Scale, p.Direction, p.HasDefault)),
            [], DateTimeOffset.UtcNow);
        return procedure;
    }

    private CustomerProcedureInvoker InvokerFor(ResolvedConnection connection) => new(
        new DirectConnector(Provider, connection),
        new ConnectionGates(Microsoft.Extensions.Options.Options.Create(new ExecutionOptions())),
        new UtcClock(),
        NullLogger<CustomerProcedureInvoker>.Instance);

    private sealed class DirectConnector(ICustomerDatabaseProvider provider, ResolvedConnection connection) : ICustomerDatabaseConnector
    {
        public async Task<System.Data.Common.DbConnection> OpenAsync(Guid connectionId, CancellationToken cancellationToken)
        {
            var db = provider.CreateConnection(connection, 10);
            await db.OpenAsync(cancellationToken);
            return db;
        }
    }

    private sealed class UtcClock : DatabaseAIAgent.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    [Fact]
    public async Task AdministratorAccount_ConnectsAndIsFlaggedAsAdministrator()
    {
        fixture.SkipIfUnavailable();

        var result = await Provider.TestAsync(Admin, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Failure.ToString());
        Assert.False(string.IsNullOrEmpty(result.ServerVersion));
        Assert.True(result.Permissions!.IsAdministrator);
    }

    [Fact]
    public async Task LeastPrivilegeAccount_CanOnlyExecuteTheGrantedProcedure()
    {
        fixture.SkipIfUnavailable();

        var result = await Provider.TestAsync(Reporter, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Failure.ToString());
        Assert.False(result.Permissions!.IsAdministrator);
        Assert.False(result.Permissions.CanReadTablesDirectly);
        Assert.False(result.Permissions.CanWriteData);
        Assert.True(result.Permissions.ExecutableProcedureCount >= 1);
    }

    [Fact]
    public async Task WrongPassword_IsReportedAsAuthenticationFailure()
    {
        fixture.SkipIfUnavailable();

        var result = await Provider.TestAsync(Reporter with { Password = "definitely-wrong" }, TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionFailureKind.AuthenticationFailed, result.Failure);
    }

    [Fact]
    public async Task UnknownDatabase_IsReported()
    {
        fixture.SkipIfUnavailable();

        var result = await Provider.TestAsync(Admin with { Database = "does_not_exist" }, TestContext.Current.CancellationToken);

        Assert.Contains(result.Failure, new[] { ConnectionFailureKind.DatabaseNotFound, ConnectionFailureKind.PermissionDenied });
    }

    [Fact]
    public async Task ClosedPort_IsReportedAsUnreachable()
    {
        fixture.SkipIfUnavailable();

        var result = await Provider.TestAsync(Admin with { Port = 1 }, TestContext.Current.CancellationToken);

        Assert.Contains(result.Failure, new[] { ConnectionFailureKind.HostUnreachable, ConnectionFailureKind.Timeout });
    }
}

public sealed class SqlServerFixture : ContainerFixture<MsSqlContainer>
{
    protected override MsSqlContainer Build() => new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    protected override async Task SeedAsync()
    {
        await using var db = new SqlConnection(Container.GetConnectionString());
        await db.OpenAsync();
        foreach (var sql in new[]
        {
            $"CREATE DATABASE {Accounts.Database}",
            $"CREATE LOGIN {Accounts.Reporter} WITH PASSWORD = '{Accounts.ReporterPassword}'",
            $"USE {Accounts.Database}; CREATE TABLE dbo.Customers (Id int PRIMARY KEY, Name nvarchar(100), Balance decimal(12,2))",
            $"USE {Accounts.Database}; INSERT INTO dbo.Customers VALUES (1, N'Contoso', 100)",
            $"USE {Accounts.Database}; CREATE TABLE dbo.Invoices (Id int PRIMARY KEY, CustomerId int NOT NULL REFERENCES dbo.Customers (Id))",
            $"USE {Accounts.Database}; EXEC('CREATE PROCEDURE dbo.GetCustomers AS SELECT Id, Name, Balance FROM dbo.Customers')",
            $"USE {Accounts.Database}; EXEC('CREATE PROCEDURE dbo.SetCreditLimit @Id int, @Limit decimal(12,2) = 0 AS UPDATE dbo.Customers SET Balance = @Limit WHERE Id = @Id')",
            $"USE {Accounts.Database}; CREATE USER {Accounts.Reporter} FOR LOGIN {Accounts.Reporter}; GRANT VIEW DEFINITION TO {Accounts.Reporter}; GRANT EXECUTE ON OBJECT::dbo.GetCustomers TO {Accounts.Reporter}",
        })
        {
            await using var command = new SqlCommand(sql, db);
            await command.ExecuteNonQueryAsync();
        }
    }
}

public sealed class SqlServerProviderTests(SqlServerFixture fixture) : ProviderContractTests<MsSqlContainer>(fixture), IClassFixture<SqlServerFixture>
{
    protected override ICustomerDatabaseProvider Provider { get; } = new SqlServerDatabaseProvider(Accounts.Options, NullLogger<SqlServerDatabaseProvider>.Instance);

    protected override ISchemaDiscoverer Discoverer { get; } = new SqlServerSchemaDiscoverer(NullLogger<SqlServerSchemaDiscoverer>.Instance);

    // The container uses a self-signed certificate: encrypted, without certificate validation.
    protected override ResolvedConnection Admin => new(
        DatabaseProviderKind.SqlServer, fixture.Container.Hostname, fixture.Container.GetMappedPublicPort(MsSqlBuilder.MsSqlPort),
        Accounts.Database, "sa", MsSqlBuilder.DefaultPassword, TlsMode.Required);

    [Fact]
    public async Task VerifyFull_RejectsTheUntrustedSelfSignedCertificate()
    {
        fixture.SkipIfUnavailable();

        var result = await Provider.TestAsync(Admin with { TlsMode = TlsMode.VerifyFull }, TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionFailureKind.TlsFailure, result.Failure);
    }
}

public sealed class PostgresFixture : ContainerFixture<PostgreSqlContainer>
{
    protected override PostgreSqlContainer Build() =>
        new PostgreSqlBuilder("postgres:17-alpine").WithDatabase(Accounts.Database).WithUsername("admin").WithPassword("admin-password").Build();

    protected override async Task SeedAsync()
    {
        await using var db = new NpgsqlConnection(Container.GetConnectionString());
        await db.OpenAsync();
        const string sql = $"""
            CREATE TABLE customers (id int PRIMARY KEY, name text, balance numeric(12,2));
            INSERT INTO customers VALUES (1, 'Contoso', 100);
            CREATE TABLE invoices (id int PRIMARY KEY, customer_id int NOT NULL REFERENCES customers (id));
            CREATE FUNCTION set_credit_limit(p_id int, p_limit numeric DEFAULT 0) RETURNS void LANGUAGE plpgsql AS 'BEGIN UPDATE customers SET balance = p_limit WHERE id = p_id; END';
            CREATE FUNCTION get_customers() RETURNS TABLE (id int, name text, balance numeric) LANGUAGE sql SECURITY DEFINER AS 'SELECT id, name, balance FROM customers';
            CREATE ROLE {Accounts.Reporter} WITH LOGIN PASSWORD '{Accounts.ReporterPassword}' NOSUPERUSER NOCREATEDB NOCREATEROLE;
            GRANT CONNECT ON DATABASE {Accounts.Database} TO {Accounts.Reporter};
            REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION get_customers() TO {Accounts.Reporter};
            """;
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class PostgresProviderTests(PostgresFixture fixture) : ProviderContractTests<PostgreSqlContainer>(fixture), IClassFixture<PostgresFixture>
{
    protected override ICustomerDatabaseProvider Provider { get; } = new PostgresDatabaseProvider(Accounts.Options, NullLogger<PostgresDatabaseProvider>.Instance);

    protected override ISchemaDiscoverer Discoverer { get; } = new PostgresSchemaDiscoverer(NullLogger<PostgresSchemaDiscoverer>.Instance);

    protected override string ReaderRoutine => "get_customers";

    protected override string WriterRoutine => "set_credit_limit";

    protected override ResolvedConnection Admin => new(
        DatabaseProviderKind.PostgreSql, fixture.Container.Hostname, fixture.Container.GetMappedPublicPort(PostgreSqlBuilder.PostgreSqlPort),
        Accounts.Database, "admin", "admin-password", TlsMode.Disabled);
}

public sealed class MySqlFixture : ContainerFixture<MySqlContainer>
{
    protected override MySqlContainer Build() =>
        new MySqlBuilder("mysql:8.4").WithDatabase(Accounts.Database).WithUsername("root").WithPassword("root-password").Build();

    protected override async Task SeedAsync()
    {
        await using var db = new MySqlConnection(Container.GetConnectionString());
        await db.OpenAsync();
        foreach (var sql in new[]
        {
            "CREATE TABLE customers (id int PRIMARY KEY, name varchar(100), balance decimal(12,2))",
            "INSERT INTO customers VALUES (1, 'Contoso', 100)",
            "CREATE TABLE invoices (id int PRIMARY KEY, customer_id int NOT NULL, FOREIGN KEY (customer_id) REFERENCES customers (id))",
            "CREATE PROCEDURE GetCustomers() SQL SECURITY DEFINER SELECT id, name, balance FROM customers",
            "CREATE PROCEDURE SetCreditLimit(IN p_id int, IN p_limit decimal(12,2)) MODIFIES SQL DATA UPDATE customers SET balance = p_limit WHERE id = p_id",
            $"CREATE USER '{Accounts.Reporter}'@'%' IDENTIFIED BY '{Accounts.ReporterPassword}'",
            $"GRANT EXECUTE ON PROCEDURE {Accounts.Database}.GetCustomers TO '{Accounts.Reporter}'@'%'",
        })
        {
            await using var command = new MySqlCommand(sql, db);
            await command.ExecuteNonQueryAsync();
        }
    }
}

public sealed class MySqlProviderTests(MySqlFixture fixture) : ProviderContractTests<MySqlContainer>(fixture), IClassFixture<MySqlFixture>
{
    protected override ICustomerDatabaseProvider Provider { get; } = new MySqlDatabaseProvider(Accounts.Options, NullLogger<MySqlDatabaseProvider>.Instance);

    protected override ISchemaDiscoverer Discoverer { get; } = new MySqlSchemaDiscoverer();

    protected override ResolvedConnection Admin => new(
        DatabaseProviderKind.MySql, fixture.Container.Hostname, fixture.Container.GetMappedPublicPort(MySqlBuilder.MySqlPort),
        Accounts.Database, "root", "root-password", TlsMode.Preferred);
}
