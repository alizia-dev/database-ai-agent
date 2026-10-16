using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Database.Providers;

/// <summary>
/// Shared connection-test flow: open with a timeout, read the version, inspect privileges. Driver exceptions are mapped
/// to <see cref="ConnectionFailureKind"/> categories; raw messages (which can echo host or user names) are not returned.
/// </summary>
internal abstract class CustomerDatabaseProviderBase(IOptions<DatabaseConnectionOptions> options, ILogger logger) : ICustomerDatabaseProvider
{
    public const string ApplicationName = "DatabaseAIAgent";

    protected DatabaseConnectionOptions Settings => options.Value;

    public abstract DatabaseProviderKind Kind { get; }

    public abstract string DisplayName { get; }

    public abstract int DefaultPort { get; }

    public abstract string LeastPrivilegeSetupScript { get; }

    public abstract DbConnection CreateConnection(ResolvedConnection connection, int connectTimeoutSeconds);

    public abstract ParsedConnectionString ParseConnectionString(string connectionString);

    public async Task<ConnectionTestResult> TestAsync(ResolvedConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Settings.ConnectTimeoutSeconds + Settings.TestCommandTimeoutSeconds));

        try
        {
            await using var db = CreateConnection(connection, Settings.ConnectTimeoutSeconds);
            await db.OpenAsync(timeout.Token);
            var version = await ReadServerVersionAsync(db, timeout.Token);

            PermissionSummary? permissions;
            try
            {
                permissions = await InspectPermissionsAsync(db, timeout.Token);
            }
            catch (DbException ex)
            {
                // Inspection is advisory; a locked-down account may not see the catalog views.
                logger.LogInformation("Could not inspect {Provider} privileges ({ErrorType})", Kind, ex.GetType().Name);
                permissions = PermissionSummary.Unknown;
            }

            return new ConnectionTestResult(ConnectionFailureKind.None, version, permissions, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConnectionTestResult.Failed(ConnectionFailureKind.Timeout, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = Classify(ex);
            logger.LogInformation("{Provider} connection test failed: {Failure} ({ErrorType})", Kind, failure, ex.GetType().Name);
            return ConnectionTestResult.Failed(failure, stopwatch.Elapsed);
        }
    }

    protected abstract Task<string?> ReadServerVersionAsync(DbConnection connection, CancellationToken cancellationToken);

    protected abstract Task<PermissionSummary> InspectPermissionsAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>Provider-specific error codes first, then generic network/TLS/timeout causes.</summary>
    protected abstract ConnectionFailureKind ClassifyProviderError(Exception exception);

    private ConnectionFailureKind Classify(Exception exception)
    {
        var specific = ClassifyProviderError(exception);
        if (specific != ConnectionFailureKind.Unknown)
        {
            return specific;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AuthenticationException:
                    return ConnectionFailureKind.TlsFailure;
                case TimeoutException:
                    return ConnectionFailureKind.Timeout;
                case SocketException socket when socket.SocketErrorCode == SocketError.TimedOut:
                    return ConnectionFailureKind.Timeout;
                case SocketException:
                    return ConnectionFailureKind.HostUnreachable;
            }
        }

        return ConnectionFailureKind.Unknown;
    }

    protected static string FirstLine(string? text) =>
        text is null ? string.Empty : text.Split('\n', 2)[0].Trim();

    /// <summary>Keys present in a pasted connection string, so unsupported ones can be reported as ignored.</summary>
    protected static IReadOnlyList<string> UnsupportedKeys(string connectionString, IReadOnlySet<string> supported)
    {
        var generic = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return generic.Keys.Cast<string>()
            .Where(k => !supported.Contains(k.Replace(" ", string.Empty, StringComparison.Ordinal)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
