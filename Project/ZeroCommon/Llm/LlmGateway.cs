using Agent.Common.Llm.Providers;

namespace Agent.Common.Llm;

/// <summary>
/// Single front-door for "open a chat session against whatever the user has
/// chosen as their active backend". TestBot, AgentBot regular chat, and
/// AIMODE entry-point all route through this so the active-backend toggle
/// in Settings is the only switch the user has to flip.
///
/// Local path delegates to <see cref="LlmService.OpenSession"/> (requires
/// LoadAsync first). External path constructs a fresh
/// <see cref="ExternalChatSession"/> per call — REST is stateless so there's
/// nothing to "load". Errors surface as exceptions caught by the caller.
/// </summary>
public static class LlmGateway
{
    /// <summary>
    /// True when a chat session can be opened RIGHT NOW for the active backend.
    /// For Local that means LLM is loaded; for External that means a provider
    /// + non-empty model id resolve cleanly. Does NOT probe the network.
    /// </summary>
    public static bool IsActiveAvailable()
    {
        var s = LlmSettingsStore.Load();
        return s.ActiveBackend switch
        {
            LlmActiveBackend.Local => LlmService.Llm is not null,
            LlmActiveBackend.External => HasUsableExternal(s),
            _ => false,
        };
    }

    private static bool HasUsableExternal(LlmRuntimeSettings s)
    {
        var model = s.ResolveExternalModel();
        if (string.IsNullOrEmpty(model)) return false;

        // Constructor-time validation: OpenAI without a key is unusable, others
        // accept empty key. We mirror the factory's tolerance.
        if (s.External.Provider == ExternalProviderNames.OpenAI
            && string.IsNullOrWhiteSpace(s.External.OpenAIApiKey))
            return false;

        return s.External.Provider == ExternalProviderNames.Webnori
            || s.External.Provider == ExternalProviderNames.OpenAI
            || s.External.Provider == ExternalProviderNames.LMStudio
            || s.External.Provider == ExternalProviderNames.Ollama;
    }

    /// <summary>
    /// Opens a chat session for the active backend. Caller owns disposal.
    /// Throws when the active backend isn't ready (e.g., Local requested but
    /// LlmService not loaded; External configured for OpenAI without a key).
    /// </summary>
    public static ILocalChatSession OpenSession()
    {
        var s = LlmSettingsStore.Load();
        return s.ActiveBackend switch
        {
            LlmActiveBackend.Local => LlmService.OpenSession(),
            LlmActiveBackend.External => OpenExternalSession(s),
            _ => throw new InvalidOperationException($"Unknown ActiveBackend: {s.ActiveBackend}"),
        };
    }

    private static ILocalChatSession OpenExternalSession(LlmRuntimeSettings s)
    {
        var provider = s.CreateExternalProvider()
            ?? throw new InvalidOperationException($"Unknown external provider '{s.External.Provider}'.");
        var model = s.ResolveExternalModel();
        if (string.IsNullOrEmpty(model))
        {
            (provider as IDisposable)?.Dispose();
            throw new InvalidOperationException(
                $"No model selected for {s.External.Provider}. Open Settings → LLM → External and pick one.");
        }

        return new ExternalChatSession(provider, model, s.External.MaxTokens, s.Temperature);
    }
}
