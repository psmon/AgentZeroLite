using Agent.Common.Llm.Providers;

namespace Agent.Common.Llm;

/// <summary>
/// Persisted external-provider configuration. Per-provider URL/Key slots are
/// kept independently so toggling Provider doesn't wipe credentials the user
/// already entered for another provider.
///
/// Stored inline within <see cref="LlmRuntimeSettings"/> in
/// <c>%LOCALAPPDATA%\AgentZeroLite\llm-settings.json</c>.
/// </summary>
public sealed class ExternalLlmSettings
{
    /// <summary>"Ollama" | "OpenAI" | "LMStudio". See <see cref="ExternalProviderNames"/>.</summary>
    /// <remarks>
    /// Ollama is the default because it needs no credential: a fresh install points at
    /// <c>localhost:11434</c> and works as soon as the user has Ollama running. A file
    /// written by an older build may still say "Webnori"; that host is gone, so the name
    /// simply resolves to no provider and the UI asks the user to pick one.
    /// </remarks>
    public string Provider { get; set; } = ExternalProviderNames.Ollama;

    /// <summary>
    /// Model id sent on each request. Empty means the user hasn't picked one yet
    /// (Refresh + select required) — no provider ships a hardcoded default model.
    /// </summary>
    public string SelectedModel { get; set; } = "";

    /// <summary>
    /// Per-request token cap. Externally-hosted models do all other tuning
    /// server-side, so this is the only knob the user can turn from inside
    /// AgentZeroLite. 4096 fits a long Gemma JSON envelope plus prose.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    // ── Per-provider credential slots ──
    // Independent so flipping Provider doesn't wipe sibling keys/URLs.

    public string OpenAIApiKey { get; set; } = "";
    public string OpenAIBaseUrl { get; set; } = "";   // empty → OpenAiDefaults.BaseUrl

    public string LMStudioApiKey { get; set; } = "";
    public string LMStudioBaseUrl { get; set; } = ""; // user must set (no localhost default — typical port 1234)

    public string OllamaBaseUrl { get; set; } = "";   // empty → OllamaDefaults.BaseUrl
}
