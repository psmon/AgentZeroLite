namespace Agent.Common.Llm.Providers;

/// <summary>
/// Provider names — kept as string constants so settings JSON survives across
/// catalog evolution (renames/additions). <see cref="Webnori"/> targets the
/// a1 (workhorse) host, <see cref="WebnoriA2"/> targets the a2 (experimental)
/// host. Both share the same bundled test key.
/// </summary>
public static class ExternalProviderNames
{
    /// <summary>Webnori a1 — workhorse host (Gemma 4 + GPT-OSS + embeddings).</summary>
    public const string Webnori = "Webnori";

    /// <summary>Webnori a2 — experimental host (large/small comparison models).</summary>
    public const string WebnoriA2 = "WebnoriA2";

    public const string OpenAI = "OpenAI";
    public const string LMStudio = "LMStudio";
    public const string Ollama = "Ollama";

    public static readonly IReadOnlyList<string> All = new[] { Webnori, WebnoriA2, OpenAI, LMStudio, Ollama };
}

/// <summary>
/// Webnori is a free Gemma-toolchain-friendly host bundled with the app so
/// anyone trying out AgentZero Lite can exercise the OpenAI-compatible
/// External LLM path without first obtaining their own credentials.
///
/// <para>Two sibling hosts ship as separate provider entries so the user can
/// pick which catalog to talk to:</para>
/// <list type="bullet">
///   <item><description><b>a1</b> (<see cref="BaseUrl"/>) — workhorse: Gemma 4
///   E4B + GPT-OSS 20B + nomic embeddings. The default that audio STT uses.</description></item>
///   <item><description><b>a2</b> (<see cref="BaseUrlA2"/>) — experimental
///   comparison group: Qwen3.6-27B + Nemotron-3-Nano-4B.</description></item>
/// </list>
///
/// <para>The endpoints accept unauthenticated calls too — <see cref="ApiKey"/>
/// is shipped on purpose as a test credential, not a secret, and the app
/// includes it on every request so traffic carries consistent identification
/// while you evaluate the app. For sustained / production use, switch to
/// Local or your own provider.</para>
/// </summary>
public static class WebnoriDefaults
{
    /// <summary>a1 — workhorse host (Gemma 4 / GPT-OSS / embeddings).</summary>
    public const string BaseUrl = "https://a1.webnori.com";

    /// <summary>a2 — experimental host (Qwen3.6-27B / Nemotron-3-Nano-4B).</summary>
    public const string BaseUrlA2 = "https://a2.webnori.com";

    /// <summary>
    /// Bundled test key — provided so AgentZero Lite users can try the
    /// External LLM path immediately. Same key works on both a1 and a2.
    /// Not a secret; safe to view in source.
    /// </summary>
    public const string ApiKey = "sk-lm-AbUGwC6s:nnbfoTZZKnjYF5rcffYM";

    /// <summary>Default a1 model — the Gemma 4 lane the local toolchain mirrors.</summary>
    public const string DefaultModel = "google/gemma-4-e4b";

    /// <summary>Default a2 model — pick the larger Qwen entry for first-touch.</summary>
    public const string DefaultModelA2 = "qwen/qwen3.6-27b";

    /// <summary>a1 catalog snapshot — used as a fallback when the live /v1/models call fails.</summary>
    public static readonly IReadOnlyList<string> KnownModels = new[]
    {
        "google/gemma-4-e4b",
        "openai/gpt-oss-20b",
        "text-embedding-nomic-embed-text-v1.5",
    };

    /// <summary>a2 catalog snapshot — used as a fallback when the live /v1/models call fails.</summary>
    public static readonly IReadOnlyList<string> KnownModelsA2 = new[]
    {
        "qwen/qwen3.6-27b",
        "nvidia/nemotron-3-nano-4b",
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

    /// <summary>Webnori a1 — workhorse host.</summary>
    public static ILlmProvider CreateWebnori(TimeSpan? timeout = null)
        => new OpenAiCompatibleProvider(ExternalProviderNames.Webnori,
            WebnoriDefaults.BaseUrl, WebnoriDefaults.ApiKey, timeout);

    /// <summary>Webnori a2 — experimental host.</summary>
    public static ILlmProvider CreateWebnoriA2(TimeSpan? timeout = null)
        => new OpenAiCompatibleProvider(ExternalProviderNames.WebnoriA2,
            WebnoriDefaults.BaseUrlA2, WebnoriDefaults.ApiKey, timeout);
}
