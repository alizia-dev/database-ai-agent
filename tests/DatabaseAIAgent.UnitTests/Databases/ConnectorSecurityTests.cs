using System.Net;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Application.Abstractions.Security;
using DatabaseAIAgent.Database;
using DatabaseAIAgent.Database.Networking;
using DatabaseAIAgent.Database.Providers;
using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Security.Encryption;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;

namespace DatabaseAIAgent.UnitTests.Databases;

public sealed class ConnectorSecurityTests
{
    private static readonly IOptions<DatabaseConnectionOptions> Defaults = Options.Create(new DatabaseConnectionOptions());

    [Theory]
    [InlineData("127.0.0.1", HostRejection.Loopback)]
    [InlineData("::1", HostRejection.Loopback)]
    [InlineData("169.254.169.254", HostRejection.ReservedAddress)]
    [InlineData("fd00:ec2::254", HostRejection.ReservedAddress)]
    [InlineData("fe80::1", HostRejection.ReservedAddress)]
    [InlineData("0.0.0.0", HostRejection.ReservedAddress)]
    [InlineData("224.0.0.1", HostRejection.ReservedAddress)]
    [InlineData("255.255.255.255", HostRejection.ReservedAddress)]
    [InlineData("100.100.100.200", HostRejection.ReservedAddress)]
    [InlineData("::ffff:169.254.169.254", HostRejection.ReservedAddress)]
    [InlineData("10.1.2.3", HostRejection.PrivateNetwork)]
    [InlineData("172.20.0.4", HostRejection.PrivateNetwork)]
    [InlineData("192.168.1.10", HostRejection.PrivateNetwork)]
    [InlineData("100.64.0.1", HostRejection.PrivateNetwork)]
    [InlineData("fd12:3456::1", HostRejection.PrivateNetwork)]
    [InlineData("203.0.113.10", HostRejection.None)]
    [InlineData("8.8.8.8", HostRejection.None)]
    [InlineData("172.32.0.1", HostRejection.None)]
    public void Classify_BlocksInternalAndReservedTargets(string address, HostRejection expected) =>
        Assert.Equal(expected, HostAddressGuard.Classify(IPAddress.Parse(address), new DatabaseConnectionOptions()));

    [Fact]
    public void Classify_PrivateAndLoopbackOnlyWhenExplicitlyAllowed()
    {
        var onPremises = new DatabaseConnectionOptions { AllowPrivateNetworks = true, AllowLoopback = true };

        Assert.Equal(HostRejection.None, HostAddressGuard.Classify(IPAddress.Parse("10.1.2.3"), onPremises));
        Assert.Equal(HostRejection.None, HostAddressGuard.Classify(IPAddress.Parse("127.0.0.1"), onPremises));
        // Metadata endpoints stay blocked regardless.
        Assert.Equal(HostRejection.ReservedAddress, HostAddressGuard.Classify(IPAddress.Parse("169.254.169.254"), onPremises));
    }

    [Theory]
    [InlineData("db.example.com", true)]
    [InlineData("sql-01.internal.example.co.uk", true)]
    [InlineData("203.0.113.10", true)]
    [InlineData("2001:db8::10", true)]
    [InlineData("db.example.com;Password=x", false)]
    [InlineData("tcp:db.example.com", false)]
    [InlineData("db.example.com,1433", false)]
    [InlineData("db.example.com\\SQLEXPRESS", false)]
    [InlineData("https://db.example.com", false)]
    [InlineData("db example.com", false)]
    [InlineData("-bad.example.com", false)]
    [InlineData("fe80::1%eth0", false)]
    [InlineData("", false)]
    public void HostSyntax_AllowsOnlyPlainHostNamesAndIpLiterals(string host, bool valid) =>
        Assert.Equal(valid, HostAddressGuard.IsSyntacticallyValid(host));

    [Fact]
    public async Task Guard_RejectsLocalhostByName()
    {
        var check = await new HostAddressGuard(Defaults).CheckAsync("localhost", CancellationToken.None);

        Assert.Equal(HostRejection.Loopback, check.Rejection);
    }

    [Fact]
    public void Encryption_RoundTripsAndBindsToTheOwnerRecord()
    {
        var protector = Protector(Key(1));
        var owner = Guid.NewGuid().ToString();

        var encrypted = protector.Protect("p@ss;word", owner);

        Assert.StartsWith("v1.", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("p@ss", encrypted, StringComparison.Ordinal);
        Assert.Equal("p@ss;word", protector.Unprotect(encrypted, owner));
        Assert.NotEqual(encrypted, protector.Protect("p@ss;word", owner)); // random nonce
        Assert.Throws<SecretProtectionException>(() => protector.Unprotect(encrypted, Guid.NewGuid().ToString()));
    }

    [Fact]
    public void Encryption_DetectsTampering()
    {
        var protector = Protector(Key(1));
        var encrypted = protector.Protect("secret", "owner");
        var chars = encrypted.ToCharArray();
        chars[^5] = chars[^5] == 'A' ? 'B' : 'A';

        Assert.Throws<SecretProtectionException>(() => protector.Unprotect(new string(chars), "owner"));
        Assert.Throws<SecretProtectionException>(() => protector.Unprotect("v1.unknownkey.AAAA", "owner"));
        Assert.Throws<SecretProtectionException>(() => protector.Unprotect("not-protected", "owner"));
    }

    [Fact]
    public void Encryption_KeyRotationKeepsOldValuesReadable()
    {
        var oldKey = Key(1);
        var encryptedWithOld = Protector(oldKey).Protect("secret", "owner");

        var rotated = Protector(Key(2), oldKey);

        Assert.Equal("secret", rotated.Unprotect(encryptedWithOld, "owner"));
        Assert.Throws<SecretProtectionException>(() => Protector(Key(2)).Unprotect(encryptedWithOld, "owner"));
    }

    [Fact]
    public void SqlServer_ConnectionStringCannotBeInjectedThroughFieldValues()
    {
        var provider = new SqlServerDatabaseProvider(Defaults, NullLogger<SqlServerDatabaseProvider>.Instance);
        var malicious = new ResolvedConnection(DatabaseProviderKind.SqlServer, "db.example.com", 1433, "sales;Integrated Security=true", "app", "x;Integrated Security=true;AttachDBFilename=c:\\evil.mdf", TlsMode.VerifyFull);

        var builder = new SqlConnectionStringBuilder(provider.CreateConnection(malicious, 10).ConnectionString);

        Assert.False(builder.IntegratedSecurity);
        Assert.Equal(string.Empty, builder.AttachDBFilename);
        Assert.Equal("x;Integrated Security=true;AttachDBFilename=c:\\evil.mdf", builder.Password);
        Assert.Equal("sales;Integrated Security=true", builder.InitialCatalog);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.False(builder.PersistSecurityInfo);
        Assert.Equal("tcp:db.example.com,1433", builder.DataSource);
    }

    [Fact]
    public void MySql_DisablesLocalFileReadsAndUserVariables()
    {
        var provider = new MySqlDatabaseProvider(Defaults, NullLogger<MySqlDatabaseProvider>.Instance);

        var builder = new MySqlConnectionStringBuilder(provider.CreateConnection(Connection(DatabaseProviderKind.MySql, TlsMode.Required), 10).ConnectionString);

        Assert.False(builder.AllowLoadLocalInfile);
        Assert.False(builder.AllowUserVariables);
        Assert.False(builder.AllowPublicKeyRetrieval);
        Assert.Equal(MySqlSslMode.Required, builder.SslMode);
    }

    [Theory]
    [InlineData(TlsMode.Disabled, SslMode.Disable)]
    [InlineData(TlsMode.Preferred, SslMode.Prefer)]
    [InlineData(TlsMode.Required, SslMode.Require)]
    [InlineData(TlsMode.VerifyFull, SslMode.VerifyFull)]
    public void Postgres_MapsTlsModes(TlsMode tls, SslMode expected)
    {
        var provider = new PostgresDatabaseProvider(Defaults, NullLogger<PostgresDatabaseProvider>.Instance);

        var builder = new NpgsqlConnectionStringBuilder(provider.CreateConnection(Connection(DatabaseProviderKind.PostgreSql, tls), 10).ConnectionString);

        Assert.Equal(expected, builder.SslMode);
        Assert.False(builder.IncludeErrorDetail);
    }

    [Fact]
    public void ParsingPastedStrings_DropsDangerousOptions()
    {
        var sql = new SqlServerDatabaseProvider(Defaults, NullLogger<SqlServerDatabaseProvider>.Instance)
            .ParseConnectionString("Server=tcp:db.example.com,14330;Database=crm;User ID=app;Password=pw;Encrypt=True;AttachDBFilename=c:\\x.mdf;Integrated Security=true");
        var mysql = new MySqlDatabaseProvider(Defaults, NullLogger<MySqlDatabaseProvider>.Instance)
            .ParseConnectionString("Server=db.example.com;Database=shop;User ID=app;Password=pw;AllowLoadLocalInfile=true");

        Assert.Equal(("db.example.com", 14330, "crm", "app", "pw"), (sql.Host, sql.Port, sql.Database, sql.Username, sql.Password));
        Assert.Contains("attachdbfilename", sql.IgnoredOptions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("integrated security", sql.IgnoredOptions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("allowloadlocalinfile", mysql.IgnoredOptions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsingPastedStrings_RejectsNamedInstancesAndMultipleHosts()
    {
        var sql = new SqlServerDatabaseProvider(Defaults, NullLogger<SqlServerDatabaseProvider>.Instance);
        var postgres = new PostgresDatabaseProvider(Defaults, NullLogger<PostgresDatabaseProvider>.Instance);

        Assert.Throws<ArgumentException>(() => sql.ParseConnectionString("Server=db\\SQLEXPRESS;Database=x;User ID=a;Password=b"));
        Assert.Throws<ArgumentException>(() => postgres.ParseConnectionString("Host=a.example.com,b.example.com;Database=x;Username=a;Password=b"));
    }

    [Fact]
    public void ResolvedConnection_ToStringNeverIncludesCredentials()
    {
        var text = Connection(DatabaseProviderKind.PostgreSql, TlsMode.VerifyFull).ToString();

        Assert.DoesNotContain("secret-password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("app_user", text, StringComparison.Ordinal);
    }

    /// <summary>Exercises the real drivers end to end (no Docker needed): nothing listens on local port 1.</summary>
    [Theory]
    [InlineData(DatabaseProviderKind.SqlServer)]
    [InlineData(DatabaseProviderKind.PostgreSql)]
    [InlineData(DatabaseProviderKind.MySql)]
    public async Task RealDriver_ClosedPort_IsReportedAsUnreachableWithoutLeakingDetails(DatabaseProviderKind kind)
    {
        var options = Options.Create(new DatabaseConnectionOptions { ConnectTimeoutSeconds = 5 });
        ICustomerDatabaseProvider provider = kind switch
        {
            DatabaseProviderKind.SqlServer => new SqlServerDatabaseProvider(options, NullLogger<SqlServerDatabaseProvider>.Instance),
            DatabaseProviderKind.PostgreSql => new PostgresDatabaseProvider(options, NullLogger<PostgresDatabaseProvider>.Instance),
            _ => new MySqlDatabaseProvider(options, NullLogger<MySqlDatabaseProvider>.Instance),
        };

        var result = await provider.TestAsync(new ResolvedConnection(kind, "127.0.0.1", 1, "db", "user", "pw", TlsMode.Disabled), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failure, new[] { ConnectionFailureKind.HostUnreachable, ConnectionFailureKind.Timeout });
    }

    private static ResolvedConnection Connection(DatabaseProviderKind provider, TlsMode tls) =>
        new(provider, "db.example.com", 5432, "sales", "app_user", "secret-password", tls);

    private static string Key(byte seed) => Convert.ToBase64String(Enumerable.Repeat(seed, 32).ToArray());

    private static AesGcmSecretProtector Protector(string key, params string[] previous) =>
        new(Options.Create(new EncryptionOptions { Key = key, PreviousKeys = [.. previous] }));
}
