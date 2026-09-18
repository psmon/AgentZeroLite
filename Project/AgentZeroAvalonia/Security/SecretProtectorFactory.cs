using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Agent.Common;
using Agent.Common.Security;

namespace AgentZeroAvalonia.Security;

/// <summary>Windows → DPAPI (same marker and entropy as the WPF host, so one settings file serves both); elsewhere → AES-GCM key file.</summary>
public static class SecretProtectorFactory
{
    public static ISecretProtector Create()
        => OperatingSystem.IsWindows()
            ? new DpapiSettingsProtector()
            : new AesGcmFileSecretProtector();
}

/// <summary>
/// Copy of the WPF host's <c>DpapiSettingsProtector</c> (Project/AgentZeroWpf/Security):
/// same <c>dpapi:v1:</c> marker, same entropy, so an <c>llm-settings.json</c> written by
/// either GUI decrypts in the other. The WPF file is untouched.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiSettingsProtector : ISecretProtector
{
    private const string Marker = "dpapi:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentZeroLite.Settings.v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        if (plaintext.StartsWith(Marker, StringComparison.Ordinal)) return plaintext;
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Marker + Convert.ToBase64String(encrypted);
    }

    public string? Unprotect(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        if (!token.StartsWith(Marker, StringComparison.Ordinal)) return token;
        try
        {
            var encrypted = Convert.FromBase64String(token[Marker.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            AppLogger.Log($"[Settings] DPAPI Unprotect failed (re-entry needed): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
