using System.Text;
using System.Text.Json;
using Agent.Common.Security;
using Agent.Common.Voice;
using Agent.Common.Llm;
using Xunit;

namespace ZeroCommon.Tests;

/// <summary>
/// Contract tests for the credential-at-rest seam (GitHub #6). These exercise
/// <see cref="SecretProtection"/> and the store-level encrypt/decrypt shape
/// with a fake marker-based protector — no Win32/DPAPI, so they run headless.
/// The real DPAPI implementation (AgentZeroWpf.Security.DpapiSettingsProtector)
/// honours the same marker contract asserted here.
/// </summary>
public sealed class SecretProtectionTests : IDisposable
{
    // A stand-in for DpapiSettingsProtector using the same marker scheme, but
    // reversible in-process (Base64 instead of DPAPI). "wrongKey" ciphertext
    // simulates a blob from another machine that cannot be decrypted.
    private sealed class FakeMarkerProtector : ISecretProtector
    {
        public const string Marker = "fake:v1:";
        public bool FailDecrypt { get; set; }

        public string Protect(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            if (plaintext.StartsWith(Marker, StringComparison.Ordinal)) return plaintext; // idempotent
            return Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        }

        public string? Unprotect(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            if (!token.StartsWith(Marker, StringComparison.Ordinal)) return token; // legacy plaintext
            if (FailDecrypt) return null; // wrong machine/user → re-entry
            return Encoding.UTF8.GetString(Convert.FromBase64String(token[Marker.Length..]));
        }
    }

    private readonly ISecretProtector _original = SecretProtection.Protector;

    public void Dispose() => SecretProtection.Protector = _original;

    [Fact]
    public void Default_protector_is_passthrough_plaintext()
    {
        SecretProtection.Protector = new PassthroughSecretProtector();
        Assert.Equal("sk-abc123", SecretProtection.Protect("sk-abc123"));
        Assert.Equal("sk-abc123", SecretProtection.Unprotect("sk-abc123"));
    }

    [Fact]
    public void Protect_then_unprotect_round_trips_the_secret()
    {
        SecretProtection.Protector = new FakeMarkerProtector();
        var stored = SecretProtection.Protect("sk-secret-key");
        Assert.StartsWith(FakeMarkerProtector.Marker, stored); // ciphertext at rest
        Assert.NotEqual("sk-secret-key", stored);
        Assert.Equal("sk-secret-key", SecretProtection.Unprotect(stored));
    }

    [Fact]
    public void Empty_and_null_are_passed_through_both_ways()
    {
        SecretProtection.Protector = new FakeMarkerProtector();
        Assert.Equal("", SecretProtection.Protect(""));
        Assert.Equal("", SecretProtection.Protect(null));
        Assert.Equal("", SecretProtection.Unprotect(""));
        Assert.Equal("", SecretProtection.Unprotect(null));
    }

    [Fact]
    public void Legacy_plaintext_without_marker_migrates_on_read()
    {
        // An existing user's file has a bare plaintext key from before #6.
        SecretProtection.Protector = new FakeMarkerProtector();
        Assert.Equal("sk-legacy-plain", SecretProtection.Unprotect("sk-legacy-plain"));
    }

    [Fact]
    public void Failed_decrypt_collapses_to_empty_for_reentry()
    {
        // Simulates the file copied to another machine / fresh OS install.
        SecretProtection.Protector = new FakeMarkerProtector { FailDecrypt = true };
        var stored = new FakeMarkerProtector().Protect("sk-secret-key"); // a valid marked blob
        Assert.Equal("", SecretProtection.Unprotect(stored)); // no crash, no key
    }

    [Fact]
    public void Protect_is_idempotent_on_already_protected_value()
    {
        SecretProtection.Protector = new FakeMarkerProtector();
        var once = SecretProtection.Protect("sk-secret-key");
        var twice = SecretProtection.Protect(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void VoiceSettings_field_shape_encrypts_at_rest_and_restores_on_load()
    {
        // Mirrors VoiceSettingsStore Save/Load field handling without file IO.
        SecretProtection.Protector = new FakeMarkerProtector();
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var v = new VoiceSettings { SttOpenAIApiKey = "sk-stt", TtsOpenAIApiKey = "sk-tts" };

        var stt = v.SttOpenAIApiKey; var tts = v.TtsOpenAIApiKey;
        v.SttOpenAIApiKey = SecretProtection.Protect(stt);
        v.TtsOpenAIApiKey = SecretProtection.Protect(tts);
        var json = JsonSerializer.Serialize(v, opts);
        v.SttOpenAIApiKey = stt; v.TtsOpenAIApiKey = tts; // finally-restore

        Assert.DoesNotContain("sk-stt", json); // plaintext never hits the file
        Assert.DoesNotContain("sk-tts", json);

        var loaded = JsonSerializer.Deserialize<VoiceSettings>(json)!;
        loaded.SttOpenAIApiKey = SecretProtection.Unprotect(loaded.SttOpenAIApiKey);
        loaded.TtsOpenAIApiKey = SecretProtection.Unprotect(loaded.TtsOpenAIApiKey);
        Assert.Equal("sk-stt", loaded.SttOpenAIApiKey);
        Assert.Equal("sk-tts", loaded.TtsOpenAIApiKey);
    }

    [Fact]
    public void LlmSettings_external_keys_encrypt_at_rest_and_restore_on_load()
    {
        SecretProtection.Protector = new FakeMarkerProtector();
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var s = new LlmRuntimeSettings();
        s.External.OpenAIApiKey = "sk-openai";
        s.External.LMStudioApiKey = "sk-lmstudio";

        var oa = s.External.OpenAIApiKey; var lm = s.External.LMStudioApiKey;
        s.External.OpenAIApiKey = SecretProtection.Protect(oa);
        s.External.LMStudioApiKey = SecretProtection.Protect(lm);
        var json = JsonSerializer.Serialize(s, opts);
        s.External.OpenAIApiKey = oa; s.External.LMStudioApiKey = lm;

        Assert.DoesNotContain("sk-openai", json);
        Assert.DoesNotContain("sk-lmstudio", json);

        var loaded = JsonSerializer.Deserialize<LlmRuntimeSettings>(json)!;
        loaded.External.OpenAIApiKey = SecretProtection.Unprotect(loaded.External.OpenAIApiKey);
        loaded.External.LMStudioApiKey = SecretProtection.Unprotect(loaded.External.LMStudioApiKey);
        Assert.Equal("sk-openai", loaded.External.OpenAIApiKey);
        Assert.Equal("sk-lmstudio", loaded.External.LMStudioApiKey);
    }
}
