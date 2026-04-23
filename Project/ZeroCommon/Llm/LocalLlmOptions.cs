using LLama.Native;

namespace Agent.Common.Llm;

public sealed record LocalLlmOptions
{
    public required string ModelPath { get; init; }

    public LocalLlmBackend Backend { get; init; } = LocalLlmBackend.Cpu;

    public uint ContextSize { get; init; } = 4096;

    public int MaxTokens { get; init; } = 256;

    public float Temperature { get; init; } = 0.7f;

    public int GpuLayerCount { get; init; } = 999;

    public bool FlashAttention { get; init; } = false;

    public bool NoKqvOffload { get; init; } = false;

    public GGMLType KvCacheTypeK { get; init; } = GGMLType.GGML_TYPE_F16;
    public GGMLType KvCacheTypeV { get; init; } = GGMLType.GGML_TYPE_F16;

    public bool UseMemoryMap { get; init; } = true;
}
