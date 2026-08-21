namespace Agent.Common.Security;

/// <summary>
/// Seam for encrypting credential fields at rest (GitHub #6, F-2). Lives in
/// ZeroCommon so the settings stores (<c>LlmSettingsStore</c>,
/// <c>VoiceSettingsStore</c>) — which are called from ZeroCommon itself
/// (<c>LlmGateway</c>) as well as the WPF host — can protect API keys without
/// ZeroCommon taking a Win32 dependency. The concrete Windows-DPAPI
/// implementation is injected by the WPF/CLI host at startup via
/// <see cref="SecretProtection.Protector"/>; the default is a no-op
/// passthrough so headless tests and any non-Windows host keep working.
///
/// <para><b>Portability contract</b> — a DPAPI-backed protector binds the
/// ciphertext to the current Windows user profile master key. The SAME
/// machine + SAME account still decrypts across app updates/reinstalls (the
/// master key lives in the Windows profile, not the app). Copying the file to
/// another machine, a fresh OS install, or a different account CANNOT decrypt
/// — that is the intended at-rest protection. Implementations MUST therefore
/// treat a failed <see cref="Unprotect"/> as "no secret" (return null) so the
/// caller falls back to re-entry rather than crashing.</para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// Wraps <paramref name="plaintext"/> into a self-describing at-rest token
    /// that this protector can later <see cref="Unprotect"/>. Empty input
    /// returns empty. Implementations MUST be idempotent: passing an
    /// already-protected token returns it unchanged.
    /// </summary>
    string Protect(string plaintext);

    /// <summary>
    /// Reverses <see cref="Protect"/>. A value with no protection marker is
    /// treated as legacy plaintext and returned as-is (it migrates to
    /// ciphertext on the next save). Returns <c>null</c> when a marked token
    /// cannot be decrypted (wrong machine/user, corrupt blob) so the caller
    /// can fall back to "no secret" / re-entry.
    /// </summary>
    string? Unprotect(string token);
}
