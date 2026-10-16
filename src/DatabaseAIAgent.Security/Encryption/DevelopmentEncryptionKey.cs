using System.Security.Cryptography;

namespace DatabaseAIAgent.Security.Encryption;

/// <summary>
/// Development convenience only: a per-machine key stored outside the repository (user profile), so encrypted test
/// credentials survive restarts. Every other environment must supply <c>ENCRYPTION_KEY</c> from a secret manager.
/// </summary>
internal static class DevelopmentEncryptionKey
{
    public static string LoadOrCreate()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DatabaseAIAgent");
        var path = Path.Combine(directory, "development-encryption.key");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (EncryptionOptions.IsValidKey(existing))
            {
                return existing;
            }
        }

        Directory.CreateDirectory(directory);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(EncryptionOptions.KeyBytes));
        File.WriteAllText(path, key);
        return key;
    }
}
