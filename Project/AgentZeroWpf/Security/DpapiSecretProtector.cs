using System.Security.Cryptography;
using System.Text;
using Agent.Common;

namespace AgentZeroWpf.Security;

/// <summary>
/// Windows DPAPI (CurrentUser scope) wrapper for the M0021 ssh password vault.
/// The encrypted blob is bound to the current Windows user account — copying
/// the SQLite DB to another machine or user does NOT yield the plaintext back.
/// We never persist plaintext anywhere; encryption happens inside the dialog
/// before the entity is written, and decryption happens lazily when the
/// terminal launcher needs to drop the password onto the clipboard.
/// </summary>
internal static class DpapiSecretProtector
{
    private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("AgentZeroLite.SshPassword.v1");

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, _entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string? Unprotect(string? encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return null;
        try
        {
            var encrypted = Convert.FromBase64String(encryptedBase64);
            var bytes = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            AppLogger.Log($"[Ssh] DPAPI Unprotect failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
