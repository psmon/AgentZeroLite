using AgentOne.Services;

namespace AgentOne.Llm;

public static class ChatProviderFactory
{
    public static readonly string[] Known = ["echo", "openai"];

    /// <summary>Builds the provider named by <paramref name="config"/>. Unknown names are a usage error, not a fallback.</summary>
    public static IChatProvider Create(AgentConfig config) => config.Provider.ToLowerInvariant() switch
    {
        "echo" => new EchoChatProvider(),
        "openai" => new OpenAiCompatChatProvider(config),
        _ => throw new ChatProviderException(
            $"unknown provider '{config.Provider}' (known: {string.Join(", ", Known)})")
    };
}
