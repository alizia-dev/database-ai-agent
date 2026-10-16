using System.Security.Cryptography;
using System.Text;
using DatabaseAIAgent.Application.Abstractions.Security;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Security.Encryption;

public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";
    public const int KeyBytes = 32;

    /// <summary>Current key: 32 random bytes, base64 (<c>ENCRYPTION_KEY</c> / <c>Encryption__Key</c>). Used for new values.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Retired keys still accepted for decryption during rotation (base64, newest first).</summary>
    public List<string> PreviousKeys { get; set; } = [];

    internal static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(key).Length == KeyBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// AES-256-GCM authenticated encryption. Format: <c>v1.{keyId}.{base64url(nonce ‖ ciphertext ‖ tag)}</c>, where keyId
/// is derived from the key itself so the right key is selected after rotation. The associated data binds each
/// ciphertext to its owner record; tampering, a wrong owner or a wrong key all fail authentication.
/// </summary>
internal sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly (string Id, byte[] Key) _current;
    private readonly Dictionary<string, byte[]> _keys;

    public AesGcmSecretProtector(IOptions<EncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        var current = Convert.FromBase64String(settings.Key);
        _current = (KeyId(current), current);
        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [_current.Id] = current };
        foreach (var previous in settings.PreviousKeys.Where(EncryptionOptions.IsValidKey))
        {
            var key = Convert.FromBase64String(previous);
            _keys.TryAdd(KeyId(key), key);
        }
    }

    public string Protect(string plaintext, string associatedData)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var payload = new byte[NonceBytes + plain.Length + TagBytes];
        var nonce = payload.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_current.Key, TagBytes);
        aes.Encrypt(nonce, plain, payload.AsSpan(NonceBytes, plain.Length), payload.AsSpan(NonceBytes + plain.Length), Aad(associatedData));
        CryptographicOperations.ZeroMemory(plain);

        return $"{Version}.{_current.Id}.{WebEncoders.Base64UrlEncode(payload)}";
    }

    public string Unprotect(string protectedValue, string associatedData)
    {
        var parts = protectedValue?.Split('.') ?? [];
        if (parts.Length != 3 || parts[0] != Version)
        {
            throw new SecretProtectionException("The protected value has an unknown format.");
        }

        if (!_keys.TryGetValue(parts[1], out var key))
        {
            throw new SecretProtectionException("The key that protected this value is not configured.");
        }

        byte[] payload;
        try
        {
            payload = WebEncoders.Base64UrlDecode(parts[2]);
        }
        catch (FormatException ex)
        {
            throw new SecretProtectionException("The protected value is corrupted.", ex);
        }

        if (payload.Length < NonceBytes + TagBytes)
        {
            throw new SecretProtectionException("The protected value is corrupted.");
        }

        var cipherLength = payload.Length - NonceBytes - TagBytes;
        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(payload.AsSpan(0, NonceBytes), payload.AsSpan(NonceBytes, cipherLength), payload.AsSpan(NonceBytes + cipherLength), plain, Aad(associatedData));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SecretProtectionException("The protected value failed authentication.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] Aad(string associatedData) => Encoding.UTF8.GetBytes(associatedData ?? string.Empty);

    // A short, non-secret fingerprint of the key (truncated SHA-256 of a domain-separated input).
    private static string KeyId(byte[] key) =>
        Convert.ToHexStringLower(SHA256.HashData([.. "daa-key-id:"u8, .. key]))[..12];
}
