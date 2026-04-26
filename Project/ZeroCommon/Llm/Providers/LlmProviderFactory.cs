namespace Agent.Common.Llm.Providers;

/// <summary>
/// Provider names — kept as string constants so settings JSON survives across
/// catalog evolution (renames/additions).
/// </summary>
public static class ExternalProviderNames
{
    public const string Webnori = "Webnori";
    public const string OpenAI = "OpenAI";
    public const string LMStudio = "LMStudio";
    public const string Ollama = "Ollama";

    public static readonly IReadOnlyList<string> All = new[] { Webnori, OpenAI, LMStudio, Ollama };
}

/// <summary>
/// Webnori is a free-tier LM-Studio-compatible host run by the project owner
/// for contributors. The key is intentionally exposed (and rotates without
/// notice) — uptime/latency are best-effort. If you hit quota or 5xx, switch
/// to Local or your own provider.
/// </summary>
public static class WebnoriDefaults
{
    public const string BaseUrl = "https://a2.webnori.com";
    public const string ApiKey = "sk-lm-fo31fDrG:BmuPhh2BJWRUjkx3W80C";

    /// <summary>The Gemma 4 model AgentZeroLite standardises its toolchain on.</summary>
    public const string DefaultModel = "google/gemma-4-e4b";

    public static readonly IReadOnlyList<string> KnownModels = new[]
    {
        "google/gemma-4-e4b",
        "google/gemma-4-26b-a4b",
    };
}

public static class OllamaDefaults
{
    public const string BaseUrl = "http://localhost:11434";
}

public static class OpenAiDefaults
{
    public const string BaseUrl = "https://api.openai.com";
}

public static class LlmProviderFactory
{
    public static ILlmProvider CreateOpenAI(string apiKey, string? baseUrl = null, TimeSpan? timeout = null)
        => new OpenAiCompatibleProvider(ExternalProviderNames.OpenAI,
            string.IsNullOrEmpty(baseUrl) ? OpenAiDefaults.BaseUrl : baseUrl, apiKey, timeout);

    public static ILlmProvider CreateLmStudio(string baseUrl, string apiKey = "", TimeSpan? timeout = null)
        => new OpenAiCompatibleProvider(ExternalProviderNames.LMStudio, baseUrl, apiKey, timeout);

    public static ILlmProvider CreateOllama(string? baseUrl = null, TimeSpan? timeout = null)
        => new OpenAiCompatibleProvider(ExternalProviderNames.Ollama,
            string.IsNullOrEmpty(baseUrl) ? OllamaDefaults.BaseUrl : baseUrl, apiKey: "", timeout);

    public static ILlmProvider CreateWebnori(TimeSpan? timeout = null)
        => new OpenAiCompatibleProvider(ExternalProviderNames.Webnori,
            WebnoriDefaults.BaseUrl, WebnoriDefaults.ApiKey, timeout);
}
