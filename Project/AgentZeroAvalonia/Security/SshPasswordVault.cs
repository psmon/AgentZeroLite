using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Agent.Common;
using Agent.Common.Security;

namespace AgentZeroAvalonia.Security;

/// <summary>
/// The ssh-password vault for CLI definitions — the same blob format as the WPF host's
/// <c>DpapiSecretProtector</c> (raw base64, entropy <c>AgentZeroLite.SshPassword.v1</c>,
/// no marker prefix), because both GUIs read the same <c>CliDefinitions</c> rows.
///
/// <para>This host used to seal that column with <see cref="SecretProtection"/>, whose
/// entropy and <c>dpapi:v1:</c> marker belong to the settings files: a password saved
/// here could not be read by the WPF host, and vice versa. Writing goes through the
/// shared format now; reading still accepts a marked token so the passwords already
/// stored by this host keep working until they are next saved.</para>
///
/// <para>Off Windows there is no DPAPI, so the AES-GCM key file
/// (<see cref="SecretProtection"/>) is the store; SSH definitions are Windows-only for
/// now anyway.</para>
/// </summary>
public static class SshPasswordVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentZeroLite.SshPassword.v1");

    /// <summary>Seals a typed password for the database. Empty in → empty out.</summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        if (!OperatingSystem.IsWindows()) return SecretProtection.Protect(plaintext);
        return Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// Opens a stored password, or returns null when this account cannot (different
    /// machine or user, corrupt blob) so the caller can ask for it again instead of
    /// launching with a wrong one.
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;

        // Written by this host before the formats converged, or by the non-Windows path.
        if (stored.StartsWith("dpapi:v1:", StringComparison.Ordinal) ||
            stored.StartsWith(AesGcmFileSecretProtector.Marker, StringComparison.Ordinal))
        {
            var opened = SecretProtection.Unprotect(stored);
            return string.IsNullOrEmpty(opened) ? null : opened;
        }

        if (!OperatingSystem.IsWindows()) return null;
        return UnprotectWindows(stored);
    }

    [SupportedOSPlatform("windows")]
    private static string? UnprotectWindows(string stored)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            AppLogger.Log($"[Ssh] stored password could not be decrypted ({ex.GetType().Name}) — re-enter it in Settings → CLI.");
            return null;
        }
    }
}
