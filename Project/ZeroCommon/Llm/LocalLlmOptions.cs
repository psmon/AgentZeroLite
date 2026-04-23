namespace Agent.Common.Llm;

public sealed record LocalLlmOptions
{
    public required string ModelPath { get; init; }

    public LocalLlmBackend Backend { get; init; } = LocalLlmBackend.Cpu;

    public uint ContextSize { get; init; } = 4096;

    public int MaxTokens { get; init; } = 256;

    public float Temperature { get; init; } = 0.7f;

    public int GpuLayerCount { get; init; } = 999;
}
