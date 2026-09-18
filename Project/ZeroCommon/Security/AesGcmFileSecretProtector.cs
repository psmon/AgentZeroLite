using System.Security.Cryptography;
using System.Text;
using Agent.Common.Platform;

namespace Agent.Common.Security;

/// <summary>
/// At-rest protection for hosts without DPAPI (macOS, Linux) — M0033. AES-GCM with a
/// per-user key file under the data root (0600 on Unix). Weaker than DPAPI or the
/// Keychain, and said so: the key sits next to the data it protects, so it stops a
/// casual read of the settings file, not an attacker with the user's home folder.
/// Keychain-backed storage is the follow-up.
///
/// Token form: <c>aesg:v1:</c> + base64(nonce[12] ‖ tag[16] ‖ ciphertext). Values
/// without the marker are legacy plaintext and pass through; a marked value that
/// fails to decrypt returns null so the caller falls back to re-entry.
/// </summary>
public sealed class AesGcmFileSecretProtector : ISecretProtector
{
    public const string Marker = "aesg:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly string _keyPath;
    private readonly object _lock = new();
    private byte[]? _key;

    public AesGcmFileSecretProtector(string? keyPath = null)
        => _keyPath = keyPath ?? AppPaths.File("secret.key");

    public string KeyPath => _keyPath;

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        if (plaintext.StartsWith(Marker, StringComparison.Ordinal)) return plaintext;   // idempotent

        var key = GetOrCreateKey();
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize, cipher.Length);
        return Marker + Convert.ToBase64String(blob);
    }

    public string? Unprotect(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        if (!token.StartsWith(Marker, StringComparison.Ordinal)) return token;   // legacy plaintext
        try
        {
            var blob = Convert.FromBase64String(token[Marker.Length..]);
            if (blob.Length < NonceSize + TagSize) return null;
            var key = GetOrCreateKey();
            var nonce = blob.AsSpan(0, NonceSize);
            var tag = blob.AsSpan(NonceSize, TagSize);
            var cipher = blob.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException)
        {
            AppLogger.Log($"[Secret] AES-GCM Unprotect failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private byte[] GetOrCreateKey()
    {
        lock (_lock)
        {
            if (_key is not null) return _key;
            if (File.Exists(_keyPath))
            {
                var existing = File.ReadAllBytes(_keyPath);
                if (existing.Length == KeySize) return _key = existing;
            }
            var key = RandomNumberGenerator.GetBytes(KeySize);
            Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
            File.WriteAllBytes(_keyPath, key);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(_keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best effort */ }
            }
            return _key = key;
        }
    }
}
