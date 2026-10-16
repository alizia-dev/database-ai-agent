using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Database;
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
            $"USE {Accounts.Database}; EXEC('CREATE PROCEDURE dbo.GetCustomers AS SELECT Id, Name, Balance FROM dbo.Customers')",
            $"USE {Accounts.Database}; CREATE USER {Accounts.Reporter} FOR LOGIN {Accounts.Reporter}; GRANT EXECUTE ON OBJECT::dbo.GetCustomers TO {Accounts.Reporter}",
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
            "CREATE PROCEDURE GetCustomers() SQL SECURITY DEFINER SELECT id, name, balance FROM customers",
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

    protected override ResolvedConnection Admin => new(
        DatabaseProviderKind.MySql, fixture.Container.Hostname, fixture.Container.GetMappedPublicPort(MySqlBuilder.MySqlPort),
        Accounts.Database, "root", "root-password", TlsMode.Preferred);
}
