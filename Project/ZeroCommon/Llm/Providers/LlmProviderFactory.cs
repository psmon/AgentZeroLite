namespace Agent.Common.Llm.Providers;

/// <summary>
/// Provider names — kept as string constants so settings JSON survives across
/// catalog evolution (renames/additions).
/// </summary>
/// <remarks>
/// A settings file may still name a provider that no longer ships (the bundled
/// Webnori a1/a2 hosts were removed when they went private). Unknown names are
/// tolerated rather than mapped: <see cref="LlmRuntimeSettings.CreateExternalProvider"/>
/// returns null and <see cref="LlmGateway.IsActiveAvailable"/> reports false, so the
/// app says "pick a provider" instead of silently talking to something else.
/// </remarks>
public static class ExternalProviderNames
{
    public const string OpenAI = "OpenAI";
    public const string LMStudio = "LMStudio";
    public const string Ollama = "Ollama";

    public static readonly IReadOnlyList<string> All = new[] { Ollama, OpenAI, LMStudio };
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
}
