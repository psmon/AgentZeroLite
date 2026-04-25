using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Drives an on-device LLM through a multi-turn tool-calling session against
/// an <see cref="IAgentToolHost"/>. Output is constrained at the sampler
/// level by <see cref="AgentToolGrammar.Gbnf"/> so every turn produces
/// parseable JSON; the loop dispatches to the host, appends the result to
/// context, and continues until the model emits <c>done</c> or
/// <see cref="AgentToolLoopOptions.MaxIterations"/> is reached.
///
/// Model-family specifics (chat markers, anti-prompts) live in
/// <see cref="ChatTemplate"/>. The constructor takes the template; default is
/// <see cref="ChatTemplates.Gemma"/> (back-compat for callers built when this
/// class was Gemma-only). Pass <see cref="ChatTemplates.Llama31"/> for
/// Nemotron Nano. Per CLAUDE.md "no premature abstractions" we keep this
/// single class with template injection rather than splitting into
/// <c>GemmaAgentToolLoop</c> + <c>NemotronAgentToolLoop</c> — the divergence
/// is small (markers + anti-prompts only).
/// </summary>
public sealed class AgentToolLoop : IAsyncDisposable
{
    private readonly LlamaSharpLocalLlm _llm;
    private readonly IAgentToolHost _host;
    private readonly AgentToolLoopOptions _opts;
    private readonly ChatTemplate _template;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;
    private readonly Grammar _grammar;
    private bool _firstTurn = true;
    private bool _isFirstUserSend = true;  // emit system prompt only on the very first RunAsync; reuse retains KV cache for follow-ups
    private int _userSendCount;
    private bool _disposed;

    /// <summary>Number of user sends dispatched into this loop.</summary>
    public int UserSendCount => _userSendCount;

    public AgentToolLoop(
        LlamaSharpLocalLlm llm,
        IAgentToolHost host,
        AgentToolLoopOptions? opts = null,
        ChatTemplate? template = null)
    {
        _llm = llm;
        _host = host;
        _opts = opts ?? new AgentToolLoopOptions();
        _template = template ?? ChatTemplates.Gemma;
        var (weights, modelParams) = llm.GetInternals();
        _context = weights.CreateContext(modelParams);
        _executor = new InteractiveExecutor(_context);
        _grammar = new Grammar(AgentToolGrammar.Gbnf, AgentToolGrammar.GrammarRootRule);
    }

    /// <summary>
    /// Runs the tool loop for the given user request. Returns when the model
    /// calls <c>done</c>, when iterations are exhausted, or when the host
    /// throws.
    /// </summary>
    public async Task<AgentToolSession> RunAsync(string userRequest, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AgentToolLoop));

        _userSendCount++;
        var turns = new List<ToolTurn>();
        string? failure = null;

        for (var iter = 0; iter < _opts.MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            // First iteration of the very first send carries the system
            // prompt + tool catalog. First iteration of any FOLLOW-UP send
            // carries only the new user request — the system prompt is
            // already in the KV cache and re-sending it would waste tokens
            // and confuse the model. Continuation tool-result turns follow
            // the standard tool-result format.
            string turnInput;
            if (iter == 0)
            {
                turnInput = _isFirstUserSend
                    ? FormatFirstTurn(userRequest)
                    : FormatFollowupUserTurn(userRequest);
                _isFirstUserSend = false;
            }
            else
            {
                turnInput = FormatToolResultTurn(turns[^1].ToolResult);
            }

            var rawJson = await GenerateOneTurnAsync(turnInput, ct);
            ToolCall call;
            try
            {
                call = ParseToolCall(rawJson);
            }
            catch (JsonException ex)
            {
                failure = $"model returned unparseable JSON at iteration {iter}: {ex.Message}; raw=\"{Truncate(rawJson, 200)}\"";
                break;
            }

            if (!AgentToolGrammar.KnownTools.Contains(call.Tool))
            {
                failure = $"model called unknown tool '{call.Tool}' at iteration {iter}";
                break;
            }

            if (call.Tool == AgentToolGrammar.DoneToolName)
            {
                var doneMsg = call.Args.TryGetPropertyValue("message", out var m) && m is JsonValue v
                    ? v.GetValue<string>()
                    : "(no message)";
                return new AgentToolSession(turns, doneMsg, TerminatedCleanly: true, FailureReason: null);
            }

            string toolResult;
            try
            {
                toolResult = await ExecuteToolAsync(call, ct);
            }
            catch (Exception ex)
            {
                toolResult = $"{{\"error\":\"{EscapeJsonString(ex.Message)}\"}}";
            }
            var turn = new ToolTurn(call, toolResult);
            turns.Add(turn);

            // Stream this turn to the UI immediately so the user sees the
            // agent's progress in real time. Callback runs on the task that
            // owns this loop — UI implementations marshal to dispatcher.
            try { _opts.OnTurnCompleted?.Invoke(turn); } catch { /* UI errors must not break the loop */ }
        }

        return new AgentToolSession(
            turns,
            FinalMessage: failure ?? $"max iterations ({_opts.MaxIterations}) reached without 'done'",
            TerminatedCleanly: false,
            FailureReason: failure);
    }

    private async Task<string> GenerateOneTurnAsync(string promptForThisTurn, CancellationToken ct)
    {
        var prompt = _template.Format(promptForThisTurn, _firstTurn);
        _firstTurn = false;

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _opts.MaxTokensPerTurn,
            AntiPrompts = _template.AntiPrompts,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = _opts.Temperature,
                Grammar = _grammar,
                GrammarOptimization = DefaultSamplingPipeline.GrammarOptimizationMode.Extended,
            },
        };

        var sb = new StringBuilder();
        await foreach (var tok in _executor.InferAsync(prompt, inferenceParams, ct))
            sb.Append(tok);

        return StripTrailingAntiPrompt(sb.ToString()).Trim();
    }

    private async Task<string> ExecuteToolAsync(ToolCall call, CancellationToken ct)
    {
        switch (call.Tool)
        {
            case "list_terminals":
                return await _host.ListTerminalsAsync(ct);

            case "read_terminal":
            {
                var (g, t) = ReadIntPair(call.Args, "group", "tab");
                var n = ReadInt(call.Args, "last_n", 2000);
                var text = await _host.ReadTerminalAsync(g, t, n, ct);
                return JsonSerializer.Serialize(new { ok = true, text });
            }

            case "send_to_terminal":
            {
                var (g, t) = ReadIntPair(call.Args, "group", "tab");
                var text = ReadString(call.Args, "text", "");
                var ok = await _host.SendToTerminalAsync(g, t, text, ct);
                return JsonSerializer.Serialize(new { ok });
            }

            case "send_key":
            {
                var (g, t) = ReadIntPair(call.Args, "group", "tab");
                var key = ReadString(call.Args, "key", "");
                var ok = await _host.SendKeyAsync(g, t, key, ct);
                return JsonSerializer.Serialize(new { ok });
            }

            default:
                return $"{{\"error\":\"unknown tool {EscapeJsonString(call.Tool)}\"}}";
        }
    }

    private static string FormatFirstTurn(string userRequest)
        => $"{AgentToolGrammar.SystemPrompt}\n\n--- USER REQUEST ---\n{userRequest}";

    private static string FormatFollowupUserTurn(string userRequest)
        => $"--- USER REQUEST ---\n{userRequest}";

    private static string FormatToolResultTurn(string toolResultJson)
        => $"--- TOOL RESULT ---\n{toolResultJson}\n\n(Reply with the next single JSON tool call. Call \"done\" when satisfied.)";

    internal static ToolCall ParseToolCall(string rawJson)
    {
        // Grammar guarantees structural validity at the sampler level, but the
        // emitted text may still have leading/trailing whitespace. Trim and parse.
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var tool = root.GetProperty("tool").GetString()
            ?? throw new JsonException("missing 'tool' string");
        var argsElement = root.GetProperty("args");
        var argsObj = JsonNode.Parse(argsElement.GetRawText())?.AsObject()
            ?? new JsonObject();
        return new ToolCall(tool, argsObj);
    }

    private static (int, int) ReadIntPair(JsonObject args, string a, string b)
        => (ReadInt(args, a, 0), ReadInt(args, b, 0));

    private static int ReadInt(JsonObject args, string key, int fallback)
        => args.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<int>(out var i)
            ? i
            : fallback;

    private static string ReadString(JsonObject args, string key, string fallback)
        => args.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s)
            ? s
            : fallback;

    private static string EscapeJsonString(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private string StripTrailingAntiPrompt(string text)
    {
        foreach (var anti in _template.AntiPrompts)
            for (var len = anti.Length; len > 0; len--)
                if (text.EndsWith(anti[..len], StringComparison.Ordinal))
                    return text[..^len];
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

public sealed record AgentToolLoopOptions
{
    /// <summary>Max model turns before the loop gives up without seeing <c>done</c>.</summary>
    public int MaxIterations { get; init; } = 8;

    /// <summary>
    /// Max tokens emitted per model turn. Pretty-printed JSON for tool calls
    /// (especially with `send_to_terminal` carrying a multi-word `text` arg)
    /// can run 80–250 tokens; under-sizing this caps the model mid-JSON and
    /// the parser sees a truncated `{ "tool": "x", "args": {` payload.
    /// 384 absorbs all 5 tools' typical envelopes with headroom.
    /// </summary>
    public int MaxTokensPerTurn { get; init; } = 384;

    /// <summary>
    /// Sampling temperature. 0.0 = greedy (most deterministic). The tool-call
    /// path values reproducibility over creativity, and a non-zero temperature
    /// can wander into grammar-dead-end states (especially on Vulkan +
    /// Llama-3.1 tokenizer where the GBNF and tokenizer interact poorly,
    /// producing 18s for 3 chars). Greedy avoids that class of stall.
    /// </summary>
    public float Temperature { get; init; } = 0.0f;

    /// <summary>
    /// Optional callback fired after each completed turn (tool call + result)
    /// so the UI can stream the agent's reasoning chain to the user instead
    /// of showing a single dump after the whole loop finishes. The callback
    /// runs on the loop's task — implementations should marshal to UI
    /// thread themselves (Dispatcher.BeginInvoke etc.).
    ///
    /// IMPORTANT: anything the callback does has NO effect on the LLM context
    /// — the loop's KV cache only sees the tool_result text, not whatever the
    /// callback renders. Safe to use for UI-side bubbles without risk of
    /// model-feedback loops.
    /// </summary>
    public Action<ToolTurn>? OnTurnCompleted { get; init; }
}
