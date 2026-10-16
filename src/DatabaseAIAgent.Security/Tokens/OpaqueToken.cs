using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace DatabaseAIAgent.Security.Tokens;

/// <summary>
/// High-entropy opaque tokens (refresh tokens, invitations). The raw value is given to the client once; only its
/// SHA-256 hash is persisted, so a database leak does not expose usable tokens.
/// </summary>
internal static class OpaqueToken
{
    private const int TokenBytes = 32;

    public static (string Token, string Hash) Create()
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        return (token, Hash(token));
    }

    /// <summary>Lowercase hex SHA-256 (64 chars).</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Rejects obviously malformed input before it reaches the database.</summary>
    public static bool LooksValid(string? token) =>
        !string.IsNullOrEmpty(token) && token.Length is >= 40 and <= 64 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
