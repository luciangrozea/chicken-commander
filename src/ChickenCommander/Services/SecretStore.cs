using System.Security.Cryptography;
using System.Text;

namespace ChickenCommander.Services;

/// <summary>
/// Encrypts saved passwords with Windows DPAPI: only the same Windows user on the same machine can decrypt them,
/// so settings.json never contains a password in clear text.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = "ChickenCommander.v1"u8.ToArray();

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Null when the value cannot be decrypted (other user / machine, or corrupted).</summary>
    public static string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
