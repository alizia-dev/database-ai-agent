using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Database.Networking;
using DatabaseAIAgent.Domain.Billing;
using DatabaseAIAgent.Domain.Common;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.Extensions.Logging;

namespace DatabaseAIAgent.Database.Connections;

/// <summary>
/// Manages connections to customer databases. Every save is preceded by a live connection test; credentials are
/// encrypted before they reach the platform database and are never returned. Deleting a connection removes only the
/// platform's metadata and credentials; the customer's database is never modified.
/// </summary>
public sealed class DatabaseConnectionService(
    IEnumerable<ICustomerDatabaseProvider> providers,
    IDatabaseConnectionRepository connections,
    IEntitlementService entitlements,
    HostAddressGuard hostGuard,
    ConnectionCredentials credentials,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<DatabaseConnectionService> logger)
{
    private static readonly Error NotFound = Error.NotFound("database.connection_not_found", "That database connection was not found.");

    private sealed record Resolved(ResolvedConnection Connection, IReadOnlyList<string> IgnoredOptions);

    public IReadOnlyList<DatabaseProviderDto> ListProviders() =>
        providers.OrderBy(p => p.Kind).Select(p => new DatabaseProviderDto(p.Kind, p.DisplayName, p.DefaultPort, p.LeastPrivilegeSetupScript)).ToList();

    public async Task<IReadOnlyList<DatabaseConnectionDto>> ListAsync(CancellationToken cancellationToken) =>
        (await connections.ListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<Result<DatabaseConnectionDto>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await connections.FindAsync(id, cancellationToken) is { } connection ? ToDto(connection) : NotFound;

    /// <summary>Tests connection details without storing anything.</summary>
    public async Task<Result<ConnectionTestDto>> TestAsync(ConnectionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var access = await CheckProductionAccessAsync(cancellationToken);
        if (!access.IsSuccess)
        {
            return access.Error;
        }

        var resolved = Resolve(input, existing: null);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        return await RunTestAsync(resolved.Value, cancellationToken);
    }

    public async Task<Result<SavedConnectionDto>> CreateAsync(ConnectionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var access = await CheckProductionAccessAsync(cancellationToken);
        if (!access.IsSuccess)
        {
            return access.Error;
        }

        var limit = (await entitlements.GetCurrentAsync(cancellationToken)).Limits.MaxDatabaseConnections;
        if (limit is { } max && await connections.CountAsync(cancellationToken) >= max)
        {
            return Error.Forbidden("billing.limit_reached", $"Your plan allows {max} database connection{(max == 1 ? string.Empty : "s")}. Upgrade your plan to add more.");
        }

        if (await connections.NameExistsAsync(input.Name, exceptId: null, cancellationToken))
        {
            return Error.Conflict("database.name_taken", "A connection with this name already exists.");
        }

        var resolved = Resolve(input, existing: null);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        var test = await RunTestAsync(resolved.Value, cancellationToken);
        if (!test.Value.Succeeded)
        {
            return Error.Validation("database.connection_failed", test.Value.Message);
        }

        var c = resolved.Value.Connection;
        DatabaseConnection connection;
        try
        {
            connection = new DatabaseConnection(input.Name, c.Provider, c.Host, c.Port, c.Database, c.TlsMode, currentUser.RequiredUserId);
        }
        catch (DomainException ex)
        {
            return Error.Validation("database.invalid_connection", ex.Message);
        }

        connection.SetEncryptedCredentials(credentials.Protect(connection.Id, c.Username, c.Password));
        connection.RecordTest(clock.UtcNow, ConnectionFailureKind.None, test.Value.ServerVersion, test.Value.Permissions);
        connections.Add(connection);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {UserId} added {Provider} database connection {ConnectionId}", currentUser.UserId, c.Provider, connection.Id);
        return new SavedConnectionDto(ToDto(connection), test.Value);
    }

    public async Task<Result<SavedConnectionDto>> UpdateAsync(Guid id, ConnectionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var connection = await connections.FindAsync(id, cancellationToken);
        if (connection is null)
        {
            return NotFound;
        }

        if (await connections.NameExistsAsync(input.Name, id, cancellationToken))
        {
            return Error.Conflict("database.name_taken", "A connection with this name already exists.");
        }

        var resolved = Resolve(input, connection);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        var test = await RunTestAsync(resolved.Value, cancellationToken);
        if (!test.Value.Succeeded)
        {
            return Error.Validation("database.connection_failed", test.Value.Message);
        }

        var c = resolved.Value.Connection;
        try
        {
            connection.Rename(input.Name);
            connection.UpdateEndpoint(c.Provider, c.Host, c.Port, c.Database, c.TlsMode);
        }
        catch (DomainException ex)
        {
            unitOfWork.DiscardChanges();
            return Error.Validation("database.invalid_connection", ex.Message);
        }

        connection.SetEncryptedCredentials(credentials.Protect(connection.Id, c.Username, c.Password));
        connection.RecordTest(clock.UtcNow, ConnectionFailureKind.None, test.Value.ServerVersion, test.Value.Permissions);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {UserId} updated database connection {ConnectionId}", currentUser.UserId, connection.Id);
        return new SavedConnectionDto(ToDto(connection), test.Value);
    }

    /// <summary>Re-tests a saved connection with its stored credentials and records the outcome.</summary>
    public async Task<Result<SavedConnectionDto>> RetestAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await connections.FindAsync(id, cancellationToken);
        if (connection is null)
        {
            return NotFound;
        }

        var (username, password) = credentials.Unprotect(connection.Id, connection.EncryptedCredentials);
        var resolved = new Resolved(
            new ResolvedConnection(connection.Provider, connection.Host, connection.Port, connection.DatabaseName, username, password, connection.TlsMode),
            []);
        var test = await RunTestAsync(resolved, cancellationToken);

        connection.RecordTest(clock.UtcNow, test.Value.Failure, test.Value.ServerVersion, test.Value.Permissions);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SavedConnectionDto(ToDto(connection), test.Value);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await connections.FindAsync(id, cancellationToken);
        if (connection is null)
        {
            return NotFound;
        }

        connections.Remove(connection);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} deleted database connection {ConnectionId}", currentUser.UserId, id);
        return Result.Success();
    }

    private async Task<Result> CheckProductionAccessAsync(CancellationToken cancellationToken) =>
        (await entitlements.GetCurrentAsync(cancellationToken)).HasFeature(PlanFeatures.ProductionAccess)
            ? Result.Success()
            : Error.Forbidden("billing.upgrade_required", "Your current plan does not include connecting your own database. Choose a subscription plan to continue.");

    private Result<Resolved> Resolve(ConnectionInput input, DatabaseConnection? existing)
    {
        var provider = Provider(input.Provider);
        if (provider is null)
        {
            return Error.Validation("database.unsupported_provider", "This database type is not supported.");
        }

        ParsedConnectionString? parsed = null;
        if (!string.IsNullOrWhiteSpace(input.ConnectionString))
        {
            try
            {
                parsed = provider.ParseConnectionString(input.ConnectionString);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                // Driver parse errors can echo parts of the string; return a generic message.
                return Error.Validation(
                    "database.invalid_connection_string",
                    ex is ArgumentException { Message: var m } && m.StartsWith("Named instances", StringComparison.Ordinal) || ex.Message.StartsWith("Multiple hosts", StringComparison.Ordinal)
                        ? ex.Message
                        : $"This is not a valid {provider.DisplayName} connection string.",
                    new Dictionary<string, string[]> { ["connectionString"] = ["The connection string could not be read."] });
            }
        }

        var host = (input.Host ?? parsed?.Host)?.Trim();
        var port = input.Port ?? parsed?.Port ?? (existing?.Provider == provider.Kind ? existing.Port : provider.DefaultPort);
        var database = (input.Database ?? parsed?.Database)?.Trim();
        var username = input.Username ?? parsed?.Username;
        var password = input.Password ?? parsed?.Password;
        var tls = input.TlsMode ?? parsed?.TlsMode ?? existing?.TlsMode ?? TlsMode.VerifyFull;

        if (existing is not null && string.IsNullOrEmpty(password))
        {
            // Keep the stored credentials when no new password is supplied.
            (username, password) = credentials.Unprotect(existing.Id, existing.EncryptedCredentials);
        }

        var fields = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            fields["name"] = ["Enter a name for this connection."];
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            fields["host"] = ["Enter the server host."];
        }
        else if (!HostAddressGuard.IsSyntacticallyValid(host))
        {
            fields["host"] = [new HostCheck(HostRejection.InvalidHostName).Message];
        }

        if (port is < 1 or > 65535)
        {
            fields["port"] = ["Port must be between 1 and 65535."];
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            fields["database"] = ["Enter the database name."];
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            fields["username"] = ["Enter the username."];
        }

        if (string.IsNullOrEmpty(password))
        {
            fields["password"] = ["Enter the password."];
        }

        if (fields.Count > 0)
        {
            return Error.Validation("database.invalid_connection", "Please correct the highlighted fields.", fields);
        }

        return new Resolved(
            new ResolvedConnection(provider.Kind, host!, port, database!, username!.Trim(), password!, tls),
            parsed?.IgnoredOptions ?? []);
    }

    private async Task<Result<ConnectionTestDto>> RunTestAsync(Resolved resolved, CancellationToken cancellationToken)
    {
        var connection = resolved.Connection;
        var hostCheck = await hostGuard.CheckAsync(connection.Host, cancellationToken);
        if (!hostCheck.Allowed)
        {
            return new ConnectionTestDto(false, ConnectionFailureKind.HostNotAllowed, hostCheck.Message, null, null, [], resolved.IgnoredOptions, 0);
        }

        var result = await Provider(connection.Provider)!.TestAsync(connection, cancellationToken);
        return new ConnectionTestDto(
            result.Succeeded,
            result.Failure,
            ConnectionMessages.For(result.Failure),
            result.ServerVersion,
            result.Permissions,
            result.Succeeded ? ConnectionMessages.Warnings(connection.TlsMode, result.Permissions) : [],
            resolved.IgnoredOptions,
            (long)result.Duration.TotalMilliseconds);
    }

    private ICustomerDatabaseProvider? Provider(DatabaseProviderKind kind) => providers.FirstOrDefault(p => p.Kind == kind);

    private static DatabaseConnectionDto ToDto(DatabaseConnection c) => new(
        c.Id,
        c.Name,
        c.Provider,
        c.Host,
        c.Port,
        c.DatabaseName,
        c.TlsMode,
        c.Status,
        c.LastTestedAt,
        c.LastFailure,
        c.Status == ConnectionStatus.Failed ? ConnectionMessages.For(c.LastFailure) : null,
        c.ServerVersion,
        c.Permissions,
        c.Status == ConnectionStatus.Connected ? ConnectionMessages.Warnings(c.TlsMode, c.Permissions) : [],
        c.CreatedAt);
}
