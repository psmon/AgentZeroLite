using System.Text.Json.Serialization;

namespace AgentOne.Llm;

/// <summary>One turn of the conversation, in the shape every OpenAI-compatible endpoint expects.</summary>
public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    public static ChatMessage System(string text) => new() { Role = "system", Content = text };
    public static ChatMessage User(string text) => new() { Role = "user", Content = text };
    public static ChatMessage Assistant(string text) => new() { Role = "assistant", Content = text };
}
