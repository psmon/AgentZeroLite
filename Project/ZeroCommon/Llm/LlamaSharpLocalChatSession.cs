using System.Runtime.CompilerServices;
using Agent.Common;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Agent.Common.Llm;

public sealed class LlamaSharpLocalChatSession : ILocalChatSession
{
    private static readonly string[] AntiPrompts = { "<end_of_turn>", "<eos>" };

    private readonly LocalLlmOptions _options;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;

    private bool _firstTurn = true;
    private int _turnCount;
    private bool _disposed;

    internal LlamaSharpLocalChatSession(LocalLlmOptions options, LLamaWeights weights, ModelParams modelParams)
    {
        _options = options;

        AppLogger.Log($"[LLM] Session ctor: CreateContext begin (ctx={modelParams.ContextSize}, gpuLayers={modelParams.GpuLayerCount})");
        _context = weights.CreateContext(modelParams);
        AppLogger.Log("[LLM] Session ctor: CreateContext done");

        _executor = new InteractiveExecutor(_context);
        AppLogger.Log("[LLM] Session ctor: InteractiveExecutor ready");
    }

    public int TurnCount => _turnCount;

    public async Task<string> SendAsync(string userMessage, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var tok in SendStreamAsync(userMessage, ct))
            sb.Append(tok);
        return StripTrailingAntiPrompt(sb.ToString()).TrimEnd();
    }

    public async IAsyncEnumerable<string> SendStreamAsync(string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LlamaSharpLocalChatSession));

        // LLamaSharp's anti-prompt match halts generation but leaves the matched
        // string in the emitted output. So after turn 1 the KV cache already ends
        // with "<end_of_turn>"; turn 2 only needs a separator before the next user
        // turn, not another "<end_of_turn>".
        var prompt = _firstTurn
            ? $"<start_of_turn>user\n{userMessage}<end_of_turn>\n<start_of_turn>model\n"
            : $"\n<start_of_turn>user\n{userMessage}<end_of_turn>\n<start_of_turn>model\n";

        _firstTurn = false;
        _turnCount++;

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _options.MaxTokens,
            AntiPrompts = AntiPrompts,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = _options.Temperature
            }
        };

        await foreach (var tok in _executor.InferAsync(prompt, inferenceParams, ct))
            yield return tok;
    }

    // Anti-prompt matches in LLamaSharp can leave the matched text — or a partial
    // prefix of it when detokenization boundaries split the special token — at the
    // end of the emitted stream. Strip any trailing prefix of known anti-prompts.
    private static string StripTrailingAntiPrompt(string text)
    {
        foreach (var anti in AntiPrompts)
        {
            for (var len = anti.Length; len > 0; len--)
            {
                if (text.EndsWith(anti[..len], StringComparison.Ordinal))
                    return text[..^len];
            }
        }
        return text;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _context.Dispose();
        return ValueTask.CompletedTask;
    }
}
