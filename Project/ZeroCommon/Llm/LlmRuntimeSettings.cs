using Agent.Common.Llm.Providers;
using LLama.Native;

namespace Agent.Common.Llm;

/// <summary>
/// Which engine answers TestBot/AgentBot/AIMODE prompts.
/// Local = on-device GGUF via LLamaSharp (everything below ContextSize/Backend etc).
/// External = OpenAI-compatible REST (Webnori/OpenAI/LMStudio/Ollama). Externally
/// served models tune themselves server-side, so the only knob exposed here is
/// MaxTokens.
/// </summary>
public enum LlmActiveBackend { Local, External }

public sealed class LlmRuntimeSettings
{
    // ── Active backend selector ──
    // Default = External + Webnori + gemma-4-e4b. New AgentZeroLite installs
    // get a working AIMODE without downloading multi-GB GGUFs first; user can
    // switch to Local from the LLM tab.
    public LlmActiveBackend ActiveBackend { get; set; } = LlmActiveBackend.External;

    public ExternalLlmSettings External { get; set; } = new();

    // Which entry from LlmModelCatalog to load. Persisted as a string id so
    // the JSON file is stable across catalog additions/reorders.
    public string ModelId { get; set; } = LlmModelCatalog.Default.Id;

    public LocalLlmBackend Backend { get; set; } = LocalLlmBackend.Cpu;

    public uint ContextSize { get; set; } = 4096;

    public int MaxTokens { get; set; } = 256;

    /// <summary>
    /// PER-TURN cap for the AIMODE agent loop's tool-call generation
    /// (<see cref="Agent.Common.Llm.Tools.LocalAgentLoop"/>). Distinct from
    /// <see cref="MaxTokens"/> which sizes the TestBot's free-form answer
    /// length. AIMODE turns ship a single JSON tool envelope, but the `done`
    /// summary can be long when relayed across terminals — and the GBNF
    /// root rule ends with a trailing `ws*` so an undersized cap truncates
    /// valid JSON mid-string. 2048 fits any realistic envelope (including
    /// long Korean summaries with embedded quotes) while bounding the
    /// trailing-whitespace stall to ~10-20s on CPU. Lower it only if you
    /// observe slow per-turn completion that hits the ceiling.
    /// </summary>
    /// <remarks>
    /// Field name kept as <c>AgentToolLoopMaxTokens</c> (not renamed to
    /// <c>AgentLoopMaxTokens</c>) for JSON-persistence backward compat —
    /// existing users have this key in their settings file.
    /// </remarks>
    public int AgentToolLoopMaxTokens { get; set; } = 2048;

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

    /// <summary>
    /// Builds the external <see cref="ILlmProvider"/> from the persisted
    /// settings (Webnori/OpenAI/LMStudio/Ollama). Returns null when
    /// <c>External.Provider</c> is unrecognised — caller should surface that
    /// to the user.
    /// </summary>
    public ILlmProvider? CreateExternalProvider()
    {
        return External.Provider switch
        {
            ExternalProviderNames.Webnori => LlmProviderFactory.CreateWebnori(),
            ExternalProviderNames.WebnoriA2 => LlmProviderFactory.CreateWebnoriA2(),
            ExternalProviderNames.OpenAI => LlmProviderFactory.CreateOpenAI(External.OpenAIApiKey, External.OpenAIBaseUrl),
            ExternalProviderNames.LMStudio => LlmProviderFactory.CreateLmStudio(External.LMStudioBaseUrl, External.LMStudioApiKey),
            ExternalProviderNames.Ollama => LlmProviderFactory.CreateOllama(External.OllamaBaseUrl),
            _ => null,
        };
    }

    /// <summary>Resolves the model id to send to the active external provider.</summary>
    public string ResolveExternalModel()
    {
        if (!string.IsNullOrEmpty(External.SelectedModel))
            return External.SelectedModel;
        return External.Provider switch
        {
            ExternalProviderNames.Webnori => WebnoriDefaults.DefaultModel,
            ExternalProviderNames.WebnoriA2 => WebnoriDefaults.DefaultModelA2,
            _ => "",
        };
    }

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
