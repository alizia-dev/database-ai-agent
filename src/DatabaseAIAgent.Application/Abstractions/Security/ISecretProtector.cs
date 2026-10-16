namespace DatabaseAIAgent.Application.Abstractions.Security;

/// <summary>
/// Authenticated encryption for secrets stored in the platform database (customer database credentials).
/// <paramref name="associatedData"/> binds a ciphertext to its owner (e.g. the connection ID), so it cannot be copied
/// onto another record. Implementations support key rotation: new values use the current key, old values stay readable.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext, string associatedData);

    /// <exception cref="SecretProtectionException">The value was tampered with, belongs to another record, or its key is unavailable.</exception>
    string Unprotect(string protectedValue, string associatedData);
}

public sealed class SecretProtectionException(string message, Exception? innerException = null) : Exception(message, innerException)
{
    public SecretProtectionException()
        : this("The protected value could not be decrypted.")
    {
    }

    public SecretProtectionException(string message)
        : this(message, null)
    {
    }
}
