using System.Text.Json;
using DatabaseAIAgent.Application.Abstractions.Security;

namespace DatabaseAIAgent.Database.Connections;

/// <summary>Encrypts a connection's username and password as one value bound to the connection ID.</summary>
public sealed class ConnectionCredentials(ISecretProtector protector)
{
    public string Protect(Guid connectionId, string username, string password) =>
        protector.Protect(JsonSerializer.Serialize(new Stored(username, password)), AssociatedData(connectionId));

    public (string Username, string Password) Unprotect(Guid connectionId, string protectedValue)
    {
        var stored = JsonSerializer.Deserialize<Stored>(protector.Unprotect(protectedValue, AssociatedData(connectionId)))
            ?? throw new SecretProtectionException("Stored credentials are empty.");
        return (stored.U, stored.P);
    }

    private static string AssociatedData(Guid connectionId) => $"database-connection:{connectionId:N}";

    private sealed record Stored(string U, string P);
}
