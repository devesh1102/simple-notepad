using System.Security.Cryptography;
using System.Text;

namespace SimpleNotepad.Services;

/// <summary>
/// Encrypts and decrypts small secrets (API keys, connection strings) at rest using the
/// Windows Data Protection API (DPAPI) scoped to the current user. The ciphertext can only
/// be decrypted by the same Windows user account on the same machine.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SimpleNotepad.SecretProtector.v1");

    /// <summary>
    /// Encrypts the given plaintext and returns a Base64 string, or null when input is empty.
    /// </summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return null;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipherBytes);
    }

    /// <summary>
    /// Decrypts a Base64 string produced by <see cref="Protect"/>. Returns null when the input
    /// is empty or cannot be decrypted (e.g. copied from another user/machine).
    /// </summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return null;
        }

        try
        {
            var cipherBytes = Convert.FromBase64String(protectedBase64);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
