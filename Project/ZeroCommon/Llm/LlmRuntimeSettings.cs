using Agent.Common.Llm.Providers;
using LLama.Native;

namespace Agent.Common.Llm;

/// <summary>
/// Which engine answers TestBot/AgentBot/AIMODE prompts.
/// Local = on-device GGUF via LLamaSharp (everything below ContextSize/Backend etc).
/// External = OpenAI-compatible REST (Ollama/OpenAI/LMStudio). Externally
/// served models tune themselves server-side, so the only knob exposed here is
/// MaxTokens.
/// </summary>
public enum LlmActiveBackend { Local, External }

public sealed class LlmRuntimeSettings
{
    // ── Active backend selector ──
    // Default = External + Ollama. It needs no credential and no multi-GB GGUF
    // download, so a fresh install has a usable AIMODE as soon as Ollama is
    // running locally; the user can switch to Local or another provider from
    // the LLM tab.
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

    /// <summary>
    /// How many model turns the AI-mode agent loop may take before it gives up without a
    /// <c>done</c> — the tool chain's length. One turn is one model call plus the tool
    /// call it asks for, so a task that reads a file, runs a command and answers needs
    /// three of them; a relay through another terminal (send + wait + read) needs three
    /// on its own, which is why the loop's own default is 12 rather than a handful.
    ///
    /// <para>This used to be reachable only by editing code: both hosts built
    /// <see cref="Tools.AgentLoopOptions"/> with the per-turn token cap and the
    /// temperature and left the turn budget at its default, so a longer job could only
    /// end in "max iterations (12) reached without 'done'". It is a setting because the
    /// right number depends on the work, not on the model.</para>
    ///
    /// <para>Read it through <see cref="ResolveAgentLoopMaxTurns"/> — a hand-edited file
    /// can hold 0 or 10 000, and neither should reach the loop.</para>
    /// </summary>
    public int AgentLoopMaxTurns { get; set; } = DefaultAgentLoopMaxTurns;

    /// <summary>The loop's own default, mirrored here so the stored value starts where the code did.</summary>
    public const int DefaultAgentLoopMaxTurns = 12;

    /// <summary>One turn: the model answers and that answer must be the final one — no tool may run.</summary>
    public const int MinAgentLoopMaxTurns = 1;

    /// <summary>
    /// An upper bound exists because a turn is a paid request and a loop that will not
    /// converge spends the whole budget. 200 is far past any hand-driven task while still
    /// being a number a runaway loop stops at.
    /// </summary>
    public const int MaxAgentLoopMaxTurns = 200;

    /// <summary>The stored turn budget, clamped to what the loop can actually be run with.</summary>
    public int ResolveAgentLoopMaxTurns() =>
        Math.Clamp(AgentLoopMaxTurns <= 0 ? DefaultAgentLoopMaxTurns : AgentLoopMaxTurns,
                   MinAgentLoopMaxTurns, MaxAgentLoopMaxTurns);

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
    /// settings (Ollama/OpenAI/LMStudio). Returns null when
    /// <c>External.Provider</c> is unrecognised — caller should surface that
    /// to the user.
    /// </summary>
    public ILlmProvider? CreateExternalProvider()
    {
        return External.Provider switch
        {
            ExternalProviderNames.OpenAI => LlmProviderFactory.CreateOpenAI(External.OpenAIApiKey, External.OpenAIBaseUrl),
            ExternalProviderNames.LMStudio => LlmProviderFactory.CreateLmStudio(External.LMStudioBaseUrl, External.LMStudioApiKey),
            ExternalProviderNames.Ollama => LlmProviderFactory.CreateOllama(External.OllamaBaseUrl),
            _ => null,
        };
    }

    /// <summary>Resolves the model id to send to the active external provider.</summary>
    public string ResolveExternalModel()
    {
        // No provider ships a hardcoded default model any more — an empty value means
        // the user has not picked one, and the caller turns that into a clear message.
        return External.SelectedModel;
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
