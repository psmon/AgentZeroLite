using System.Text.Json.Serialization;

namespace AgentOne.Llm;

// The subset of the OpenAI chat-completions wire format agent-one actually
// uses. Kept minimal on purpose: every OpenAI-compatible server (Ollama,
// LM Studio, vLLM, llama.cpp's server, OpenRouter, Azure) implements these
// fields, while the extensions beyond them differ per vendor.

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = [];

    // Null leaves the field out: OpenAI's reasoning models (gpt-5*, o1/o3/o4*) answer
    // 400 "unsupported_value" to any temperature but the default 1.
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; } = 0.2;

    // Max output tokens for this call. Null leaves the field out, letting the server decide —
    // which on a local server (LM Studio, llama.cpp) is often a small default that truncates a
    // long answer or cuts a tool-call JSON mid-string (a multibyte byte at the cut then reads as
    // malformed JSON). OpenAI reasoning models reject max_tokens (they use max_completion_tokens),
    // so the provider leaves it null for them, like temperature.
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<ChatChoice>? Choices { get; set; }

    [JsonPropertyName("error")]
    public ApiError? Error { get; set; }
}

public sealed class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class ApiError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

/// <summary>One server-sent chunk of a streamed completion.</summary>
public sealed class ChatCompletionChunk
{
    [JsonPropertyName("choices")]
    public List<ChunkChoice>? Choices { get; set; }
}

public sealed class ChunkChoice
{
    [JsonPropertyName("delta")]
    public ChunkDelta? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class ChunkDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>A thinking model's reasoning, streamed before its answer (LM Studio, vLLM, DeepSeek).</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    /// <summary>The same, as some servers name it.</summary>
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
}
