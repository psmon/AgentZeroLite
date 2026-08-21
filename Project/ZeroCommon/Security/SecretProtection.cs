namespace Agent.Common.Security;

/// <summary>
/// Process-wide holder for the active <see cref="ISecretProtector"/> plus thin
/// null-safe helpers the settings stores call. The WPF/CLI host assigns
/// <see cref="Protector"/> once at startup (Windows DPAPI); until then — and in
/// headless tests — the default <see cref="PassthroughSecretProtector"/> keeps
/// values as plaintext so nothing breaks.
/// </summary>
public static class SecretProtection
{
    /// <summary>
    /// The active protector. Defaults to a no-op passthrough; the host replaces
    /// it at startup. Settable so tests can inject a fake.
    /// </summary>
    public static ISecretProtector Protector { get; set; } = new PassthroughSecretProtector();

    /// <summary>Encrypts a credential field for at-rest storage (empty-safe).</summary>
    public static string Protect(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? "" : Protector.Protect(plaintext);

    /// <summary>
    /// Decrypts a stored credential field (empty-safe). A decrypt failure —
    /// wrong machine/user, corrupt blob — collapses to "" so the caller sees
    /// "no key set" and can prompt for re-entry instead of crashing.
    /// </summary>
    public static string Unprotect(string? stored)
        => string.IsNullOrEmpty(stored) ? "" : (Protector.Unprotect(stored) ?? "");
}

/// <summary>
/// No-op protector: stores credentials as plaintext. This is the pre-#6
/// behaviour and the safe default for hosts that do not (or cannot) provide
/// OS-backed encryption — headless tests, non-Windows shells. It never fails a
/// round-trip, so a host that starts plaintext and later gains a DPAPI
/// protector migrates transparently on the next save.
/// </summary>
public sealed class PassthroughSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => plaintext;
    public string? Unprotect(string token) => token;
}
