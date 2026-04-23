using LLama.Native;

namespace Agent.Common.Llm;

public sealed class LlmRuntimeSettings
{
    // Which entry from LlmModelCatalog to load. Persisted as a string id so
    // the JSON file is stable across catalog additions/reorders.
    public string ModelId { get; set; } = LlmModelCatalog.Default.Id;

    public LocalLlmBackend Backend { get; set; } = LocalLlmBackend.Cpu;

    public uint ContextSize { get; set; } = 4096;

    public int MaxTokens { get; set; } = 256;

    public float Temperature { get; set; } = 0.7f;

    public int GpuLayerCount { get; set; } = 999;

    // -1 means "auto" — pick first discrete GPU via VulkanDeviceEnumerator.
    // Otherwise the Vulkan device index shown by `vulkaninfo --summary` (GPU0, GPU1, ...).
    public int VulkanDeviceIndex { get; set; } = -1;

    // Advanced — KV cache / attention tuning (exposed for manual testing when
    // the default combination crashes on a given GPU/driver/model).
    // Flash Attention: default ON. Early testing suspected FA as a Gemma 4 +
    // Vulkan crash source, but after fixing the VK_KHR_shader_bfloat16 env-var
    // propagation bug the real culprit was identified and FA itself is safe +
    // required for V-cache quantization (Q8_0/Q4_0 V) to work.
    public bool FlashAttention { get; set; } = true;

    public bool NoKqvOffload { get; set; } = false;            // true = keep KV cache in system RAM (safer, slower)

    public GGMLType KvCacheTypeK { get; set; } = GGMLType.GGML_TYPE_F16;
    public GGMLType KvCacheTypeV { get; set; } = GGMLType.GGML_TYPE_F16;

    // true (default) = mmap the GGUF into system RAM address space (fast loads,
    // keeps pages resident). false = regular read; releases RAM after GPU upload.
    // Turn off when system RAM is tight.
    public bool UseMemoryMap { get; set; } = true;

    public LocalLlmOptions ToOptions(string modelPath) => new()
    {
        ModelPath = modelPath,
        Backend = Backend,
        ContextSize = ContextSize,
        MaxTokens = MaxTokens,
        Temperature = Temperature,
        GpuLayerCount = GpuLayerCount,
        FlashAttention = FlashAttention,
        NoKqvOffload = NoKqvOffload,
        KvCacheTypeK = KvCacheTypeK,
        KvCacheTypeV = KvCacheTypeV,
        UseMemoryMap = UseMemoryMap
    };
}
