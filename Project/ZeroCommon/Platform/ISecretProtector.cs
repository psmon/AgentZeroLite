using System.Security.Cryptography;
using System.Text;

namespace Agent.Common.Platform;

/// <summary>
/// 사용자/머신에 묶인 비밀(예: SSH 비밀번호 볼트) 암복호화 추상화.
/// WPF의 <c>DpapiSecretProtector</c>(Windows DPAPI 전용)를 cross-platform화한다.
///
/// 저장 형식: <c>{scheme}:{base64}</c> — scheme이 복호 경로를 결정한다.
///   • <c>dpapi</c> : Windows DPAPI (CurrentUser). 다른 사용자/머신에서 복호 불가.
///   • <c>aesg</c>  : AES-GCM + 사용자 로컬 키파일. (macOS Keychain 이관은 후속 —
///                    현재는 키파일 보관이라 DPAPI/Keychain보다 약함을 명시.)
/// </summary>
public interface ISecretProtector
{
    /// <summary>평문 → 보호된 문자열. null/빈 입력은 null 반환.</summary>
    string? Protect(string? plaintext);

    /// <summary>보호된 문자열 → 평문. 실패 시 null.</summary>
    string? Unprotect(string? protectedValue);
}

/// <summary>OS별 <see cref="ISecretProtector"/> 팩토리.</summary>
public static class SecretProtector
{
    public static ISecretProtector Create()
        => OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new AesGcmFileSecretProtector();
}

/// <summary>Windows DPAPI(CurrentUser). WPF 버전과 동일 entropy로 기존 볼트 호환.</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Scheme = "dpapi";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentZeroLite.SshPassword.v1");

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        if (!OperatingSystem.IsWindows()) return null;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var enc = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return $"{Scheme}:{Convert.ToBase64String(enc)}";
    }

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        if (!OperatingSystem.IsWindows()) return null;
        var payload = StripScheme(protectedValue, Scheme);
        if (payload is null) return null;
        try
        {
            var enc = Convert.FromBase64String(payload);
            var bytes = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            AppLogger.Log($"[Secret] DPAPI Unprotect failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string? StripScheme(string value, string scheme)
        => value.StartsWith($"{scheme}:", StringComparison.Ordinal)
            ? value[(scheme.Length + 1)..]
            : null;
}

/// <summary>
/// 비Windows(macOS/Linux): AES-GCM + 사용자 로컬 키파일.
/// 키는 %LOCALAPPDATA%/AgentZeroLite/secret.key (256-bit, 1회 생성).
/// 형식: aesg:base64(nonce[12] || tag[16] || ciphertext).
/// 후속: macOS Keychain / libsecret으로 키 보관 강화.
/// </summary>
internal sealed class AesGcmFileSecretProtector : ISecretProtector
{
    private const string Scheme = "aesg";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _keyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "secret.key");

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        try
        {
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
            return $"{Scheme}:{Convert.ToBase64String(blob)}";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Secret] AES Protect failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        if (!protectedValue.StartsWith($"{Scheme}:", StringComparison.Ordinal)) return null;
        try
        {
            var blob = Convert.FromBase64String(protectedValue[(Scheme.Length + 1)..]);
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
        catch (Exception ex)
        {
            AppLogger.Log($"[Secret] AES Unprotect failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private byte[] GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
            return Convert.FromBase64String(File.ReadAllText(_keyPath));

        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(_keyPath, Convert.ToBase64String(key));
        TryRestrictPermissions(_keyPath);
        return key;
    }

    // 비Windows: 소유자 전용(0600) 권한으로 키파일 보호.
    private static void TryRestrictPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best effort */ }
    }
}
