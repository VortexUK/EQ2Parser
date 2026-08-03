using System.Security.Cryptography;
using System.Text;

namespace EQ2Parser.App.Services;

/// <summary>
/// DPAPI-at-rest for the EQ2Lexicon API token: settings.json only ever
/// holds the CurrentUser-scoped ciphertext, so a copied settings file — or
/// a PersistedJsonFile quarantine copy — never leaks the credential. A blob
/// from a different Windows user/machine unprotects to null and the user
/// simply re-pastes their token.
/// </summary>
internal static class TokenProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EQ2Parser.LexiconApiToken.v1");

    /// <summary>Encrypt for the current Windows user; null if DPAPI fails.</summary>
    public static string? Protect(string token)
    {
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Decrypt a stored blob; null when absent, corrupt, or
    /// protected by a different user/machine.</summary>
    public static string? Unprotect(string? blob)
    {
        if (string.IsNullOrEmpty(blob))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return null; // FormatException (bad base64) or CryptographicException
        }
    }
}
