using System.Text.Json.Serialization;
using AgentOne.Agent;
using AgentOne.Llm;

namespace AgentOne.Services;

/// <summary>
/// Human-facing JSON (the config file) — indented, because a person edits it.
///
/// Every type agent-one serializes is declared in one of these two contexts.
/// Native AOT trims the reflection-based serializer away, so the csproj sets
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> and a type missing
/// from here fails the same way in Debug as in the published binary — and any
/// call that would fall back to reflection shows up as an IL2026/IL3050 warning
/// at build time rather than as a crash on a user's machine.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(Credentials))]
public partial class AgentOneJson : JsonSerializerContext;

/// <summary>
/// Machine-facing JSON — wire payloads, JSONL session lines, <c>--json</c>
/// output and the tool envelope. One line each, never indented.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolCall))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(ChatCompletionChunk))]
[JsonSerializable(typeof(ModelListResponse))]
[JsonSerializable(typeof(SessionEntry))]
[JsonSerializable(typeof(RunReport))]
public partial class AgentOneWireJson : JsonSerializerContext;

/// <summary>One line of a session transcript (~/.agent-one/sessions/*.jsonl).</summary>
public sealed class SessionEntry
{
    [JsonPropertyName("ts")]
    public string Timestamp { get; set; } = "";

    /// <summary>"prompt", "step", or "result".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("tool")]
    public string? Tool { get; set; }

    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

/// <summary>What <c>agent-one run --json</c> prints: one object, machine-readable, on stdout.</summary>
public sealed class RunReport
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("stopReason")]
    public string StopReason { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("steps")]
    public int Steps { get; set; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("session")]
    public string? Session { get; set; }
}
