using System.Runtime.CompilerServices;
using Agent.Common;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Agent.Common.Llm;

public sealed class LlamaSharpLocalLlm : ILocalLlm
{
    private static readonly object NativeInitLock = new();
    private static bool _nativeConfigured;

    private readonly LocalLlmOptions _options;
    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;

    private LlamaSharpLocalLlm(LocalLlmOptions options, LLamaWeights weights, ModelParams modelParams)
    {
        _options = options;
        _weights = weights;
        _modelParams = modelParams;
    }

    public static async Task<LlamaSharpLocalLlm> CreateAsync(LocalLlmOptions options, CancellationToken ct = default)
    {
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"GGUF model not found at {options.ModelPath}", options.ModelPath);

        ConfigureNativeOnce(options.Backend);

        var modelParams = new ModelParams(options.ModelPath)
        {
            ContextSize = options.ContextSize,
            GpuLayerCount = options.Backend == LocalLlmBackend.Vulkan ? options.GpuLayerCount : 0,
            FlashAttention = options.FlashAttention,
            NoKqvOffload = options.NoKqvOffload,
            TypeK = options.KvCacheTypeK,
            TypeV = options.KvCacheTypeV,
            UseMemorymap = options.UseMemoryMap
        };

        var weights = await LLamaWeights.LoadFromFileAsync(modelParams, ct);
        return new LlamaSharpLocalLlm(options, weights, modelParams);
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var token in StreamAsync(prompt, ct))
            sb.Append(token);
        return sb.ToString();
    }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var executor = new StatelessExecutor(_weights, _modelParams);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _options.MaxTokens,
            AntiPrompts = new[] { "<end_of_turn>", "<eos>" },
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = _options.Temperature
            }
        };

        var wrapped = WrapGemmaChatTemplate(prompt);

        await foreach (var token in executor.InferAsync(wrapped, inferenceParams, ct))
            yield return token;
    }

    public ILocalChatSession CreateSession()
        => new LlamaSharpLocalChatSession(_options, _weights, _modelParams);

    // Same-assembly accessor so the Agent.Common.Llm.Tools layer can build a
    // grammar-constrained InteractiveExecutor against the loaded weights
    // without going through ILocalChatSession (which is the Q&A surface and
    // intentionally doesn't take a Grammar).
    internal (LLamaWeights weights, ModelParams modelParams) GetInternals()
        => (_weights, _modelParams);

    public ValueTask DisposeAsync()
    {
        _weights.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string WrapGemmaChatTemplate(string userPrompt)
        => $"<start_of_turn>user\n{userPrompt}<end_of_turn>\n<start_of_turn>model\n";

    private static void ConfigureNativeOnce(LocalLlmBackend backend)
    {
        lock (NativeInitLock)
        {
            if (_nativeConfigured) return;

            var baseDir = AppContext.BaseDirectory;
            var subDir = backend switch
            {
                LocalLlmBackend.Cpu    => "win-x64-cpu",
                LocalLlmBackend.Vulkan => "win-x64-vulkan",
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
            };

            var nativeDir = Path.Combine(baseDir, "runtimes", subDir, "native");
            var llamaDll = Path.Combine(nativeDir, "llama.dll");

            if (!File.Exists(llamaDll))
                throw new FileNotFoundException(
                    $"Native llama.dll not found at {llamaDll}. " +
                    "Ensure the runtimes/{rid}/native folder is copied to output.", llamaDll);

            // Route llama.cpp's own log (normally stderr) into our AppLogger so
            // native failure reasons — Vulkan device errors, GGUF parse issues,
            // allocation failures — surface in app-log.txt instead of being lost.
            NativeLibraryConfig.All.WithLogCallback((level, msg) =>
            {
                if (level < LLamaLogLevel.Warning) return; // skip Debug/Info noise
                AppLogger.Log($"[llama.cpp][{level}] {msg.TrimEnd('\r', '\n')}");
            });

            NativeLibraryConfig.All.WithLibrary(llamaPath: llamaDll, mtmdPath: null);
            _nativeConfigured = true;
        }
    }
}
