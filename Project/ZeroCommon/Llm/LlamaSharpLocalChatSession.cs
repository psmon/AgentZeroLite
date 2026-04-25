using System.Runtime.CompilerServices;
using Agent.Common;
using Agent.Common.Llm.Tools;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Agent.Common.Llm;

/// <summary>
/// In-process multi-turn chat session bound to a single
/// <see cref="LLamaContext"/>. Chat-template specifics (markers,
/// anti-prompts) are externalised into <see cref="ChatTemplate"/> so the
/// same session class works for Gemma 4 and Llama-3.1 (Nemotron) without
/// duplicating loop logic. The template is selected by
/// <see cref="LlmService.OpenSession"/> from the loaded model's catalog
/// entry; defaults to <see cref="ChatTemplates.Gemma"/> for callers that
/// pre-date the multi-model rollout.
/// </summary>
public sealed class LlamaSharpLocalChatSession : ILocalChatSession
{
    private readonly LocalLlmOptions _options;
    private readonly ChatTemplate _template;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;

    private bool _firstTurn = true;
    private int _turnCount;
    private bool _disposed;

    internal LlamaSharpLocalChatSession(
        LocalLlmOptions options,
        LLamaWeights weights,
        ModelParams modelParams,
        ChatTemplate? template = null)
    {
        _options = options;
        _template = template ?? ChatTemplates.Gemma;

        AppLogger.Log($"[LLM] Session ctor: CreateContext begin (ctx={modelParams.ContextSize}, gpuLayers={modelParams.GpuLayerCount}, template={_template.FamilyId})");
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

        // ChatTemplate.Format selects the first-turn vs continuation marker
        // pair. LLamaSharp's anti-prompt match halts generation but leaves
        // the matched string in the emitted output, so the KV cache tail
        // already ends with the family's end-of-turn marker — that's why the
        // continuation format starts cleanly with the next user header
        // rather than re-emitting an end marker.
        var prompt = _template.Format(userMessage, _firstTurn);

        _firstTurn = false;
        _turnCount++;

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _options.MaxTokens,
            AntiPrompts = _template.AntiPrompts,
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
    private string StripTrailingAntiPrompt(string text)
    {
        foreach (var anti in _template.AntiPrompts)
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
