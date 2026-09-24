namespace Agent.Common.Security;

/// <summary>
/// Process-wide holder for the active <see cref="ISecretProtector"/> plus thin
/// null-safe helpers the settings stores call. Each host assigns
/// <see cref="Protector"/> once at startup (Windows DPAPI, AES-GCM elsewhere); until
/// then — and in headless tests — the default <see cref="PassthroughSecretProtector"/>
/// carries plaintext through unchanged so nothing breaks, and reports a value that was
/// sealed by a protector it does not have as "no secret" rather than passing the
/// ciphertext off as the secret.
/// </summary>
/// <summary>
/// The prefixes an at-rest credential token can carry. A protected value is
/// self-describing so a protector can tell its own ciphertext from a legacy plaintext
/// key, and so <see cref="SecretProtection.Unprotect"/> can recognise a value that is
/// still sealed after an unprotect attempt — which means it was NOT unsealed.
///
/// <para>The literals are duplicated in each host's protector (the DPAPI one lives in
/// AgentZeroWpf and, copied, in AgentZeroAvalonia) because ZeroCommon takes no Win32
/// dependency. A test in each project asserts that its real protector's output is
/// recognised here, so adding a scheme without listing it fails a test rather than
/// quietly disabling the guard below.</para>
/// </summary>
public static class SecretMarkers
{
    /// <summary>Windows DPAPI, CurrentUser scope — <c>AgentZeroWpf.Security.DpapiSettingsProtector</c>.</summary>
    public const string Dpapi = "dpapi:v1:";

    /// <summary>AES-GCM with a key file — the non-Windows protector in ZeroCommon.</summary>
    public const string AesGcm = AesGcmFileSecretProtector.Marker;

    public static IReadOnlyList<string> All { get; } = new[] { Dpapi, AesGcm };

    /// <summary>Whether this value is a protected token rather than a usable secret.</summary>
    public static bool LooksSealed(string? value) =>
        !string.IsNullOrEmpty(value) &&
        All.Any(m => value.StartsWith(m, StringComparison.Ordinal));
}

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
    /// wrong machine/user, corrupt blob, or no protector installed for the scheme the
    /// value was written with — collapses to "" so the caller sees "no key set" and can
    /// prompt for re-entry instead of crashing.
    /// </summary>
    /// <remarks>
    /// The second check is the one that was missing. A protector returns a value it does
    /// not recognise unchanged, which is right for a legacy plaintext key and badly wrong
    /// for another scheme's ciphertext: the sealed token then travels onward AS the
    /// credential. Measured — a headless process (which installs no protector, so the
    /// passthrough is active) read a DPAPI-sealed <c>llm-settings.json</c> and sent
    /// <c>dpapi:v1:AQAAA…</c> as the API key; the provider rejected it with HTTP 401 and
    /// echoed the token back into the error text, so a value encrypted at rest ended up
    /// on the wire and in logs. A token that still looks sealed after unprotecting was
    /// not unsealed, whoever the protector is.
    /// </remarks>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        var opened = Protector.Unprotect(stored) ?? "";
        return SecretMarkers.LooksSealed(opened) ? "" : opened;
    }
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

    /// <summary>
    /// Plaintext passes through; a sealed token returns null, because this protector
    /// cannot open one and <see cref="ISecretProtector"/> requires null — not the token —
    /// when a marked value cannot be decrypted. Returning it meant a process with no
    /// protector installed handed ciphertext to whatever wanted the secret.
    /// </summary>
    public string? Unprotect(string token) => SecretMarkers.LooksSealed(token) ? null : token;
}
