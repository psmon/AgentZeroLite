using System.Security.Cryptography;
using System.Text;
using Agent.Common;
using Agent.Common.Security;

namespace ZeroWearable.Security;

/// <summary>
/// Copy of the WPF host's <c>DpapiSettingsProtector</c> (Project/AgentZeroWpf/Security):
/// same <c>dpapi:v1:</c> marker, same entropy, so the <c>llm-settings.json</c> the GUI
/// sealed decrypts here. Without it this process ran on the passthrough default,
/// <see cref="SecretProtection.Unprotect"/> correctly refused the sealed token, and the
/// External provider was called with no key at all — LM Studio answered 401
/// "none was provided" for every watch question.
/// </summary>
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
