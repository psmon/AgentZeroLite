using System.Runtime.CompilerServices;
using Agent.Common.Security;
using AgentZeroWpf.Security;

namespace AgentTest;

/// <summary>
/// Installs the WPF host's credential protector for this test process.
///
/// <para>Why this file exists: several tests read the operator's real
/// <c>%LOCALAPPDATA%\AgentZeroLite\llm-settings.json</c> so they can exercise the LLM
/// that is actually configured. That file's API keys are DPAPI-sealed by the GUI, and a
/// test process is not the GUI — nobody assigned
/// <see cref="SecretProtection.Protector"/>, so the passthrough default was active and
/// the sealed key never opened. Measured before this: <c>YouTubeClassifyTests</c> sent
/// <c>dpapi:v1:AQAAA…</c> to LM Studio as the API key and failed with HTTP 401
/// "Malformed LM Studio API token", which reads as a broken provider rather than a
/// harness that cannot read the settings it chose to use.</para>
///
/// <para>A module initializer rather than a fixture, because the protector is
/// process-wide state that must be in place before the first test touches a settings
/// store — there is no test class to hang it off, and every class would have to remember
/// to join a collection.</para>
/// </summary>
internal static class TestHostSecretProtection
{
    [ModuleInitializer]
    internal static void Install()
    {
        // Guarded, and never fatal: this is the same protector App.OnStartup installs, so
        // tests decrypt exactly what the GUI wrote. If it cannot be created the
        // passthrough stays — which, since the fix in SecretProtection, reports a sealed
        // key as "no secret" and lets a SkippableFact skip instead of sending ciphertext.
        if (!OperatingSystem.IsWindows()) return;
        try { SecretProtection.Protector = new DpapiSettingsProtector(); }
        catch { }
    }
}
