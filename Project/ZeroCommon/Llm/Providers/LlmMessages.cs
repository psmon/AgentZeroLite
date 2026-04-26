namespace Agent.Common.Llm.Providers;

public static class LlmRole
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
}

public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string text) => new(LlmRole.System, text);
    public static LlmMessage User(string text) => new(LlmRole.User, text);
    public static LlmMessage Assistant(string text) => new(LlmRole.Assistant, text);
}

public sealed record LlmRequest
{
    public string Model { get; init; } = "";
    public List<LlmMessage> Messages { get; init; } = [];
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
}

public sealed record LlmResponse
{
    public string Text { get; init; } = "";
    public string? FinishReason { get; init; }
}

public sealed record LlmStreamChunk
{
    public string Text { get; init; } = "";
    public string? FinishReason { get; init; }
}

public sealed record LlmModelInfo
{
    public string Id { get; init; } = "";
}
