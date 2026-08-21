using System.Security.Cryptography;
using System.Text;
using Agent.Common;
using Agent.Common.Security;

namespace AgentZeroWpf.Security;

/// <summary>
/// Windows DPAPI (CurrentUser scope) implementation of the <see cref="ISecretProtector"/>
/// seam for at-rest credential fields in <c>llm-settings.json</c> /
/// <c>voice-settings.json</c> (GitHub #6, F-2). Separate from
/// <see cref="DpapiSecretProtector"/> (the M0021 ssh-password vault) so the two
/// domains have independent entropy and can evolve apart.
///
/// <para><b>Marker scheme</b> — protected values carry the <see cref="Marker"/>
/// prefix. This lets <see cref="Unprotect"/> distinguish (a) a value it wrote
/// from (b) a legacy plaintext key an existing user already has in the file.
/// Legacy plaintext is returned untouched and gets encrypted on the next save,
/// so migration needs no separate pass.</para>
///
/// <para><b>Portability</b> — the blob is bound to the current Windows user
/// profile master key. Same machine + same account keeps working across app
/// updates/reinstalls; a different machine, a fresh OS install, or a different
/// account cannot decrypt, and <see cref="Unprotect"/> returns null so the
/// settings store surfaces "no key" and the user re-enters it.</para>
/// </summary>
internal sealed class DpapiSettingsProtector : ISecretProtector
{
    // Version tag in the marker so a future entropy/scheme change stays
    // distinguishable from v1 blobs.
    private const string Marker = "dpapi:v1:";
    private static readonly byte[] _entropy = Encoding.UTF8.GetBytes("AgentZeroLite.Settings.v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        // Idempotent: never double-wrap an already-protected token.
        if (plaintext.StartsWith(Marker, StringComparison.Ordinal)) return plaintext;
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), _entropy, DataProtectionScope.CurrentUser);
        return Marker + Convert.ToBase64String(encrypted);
    }

    public string? Unprotect(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        // No marker → legacy plaintext written before #6. Return as-is; it will
        // be encrypted the next time the settings are saved.
        if (!token.StartsWith(Marker, StringComparison.Ordinal)) return token;
        try
        {
            var encrypted = Convert.FromBase64String(token[Marker.Length..]);
            var bytes = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Wrong machine/user or corrupt blob. Null signals the store to fall
            // back to "no key" so the user re-enters it — never a crash.
            AppLogger.Log($"[Settings] DPAPI Unprotect failed (re-entry needed): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
