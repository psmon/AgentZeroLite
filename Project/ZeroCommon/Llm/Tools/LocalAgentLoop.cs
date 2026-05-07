using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Drives an on-device LLM through a multi-turn agent loop against an
/// <see cref="IAgentToolbelt"/>. Output is constrained at the sampler level
/// by <see cref="AgentToolGrammar.Gbnf"/> so every turn produces parseable
/// JSON; the loop dispatches to the toolbelt, appends the result to
/// context, and continues until the model emits <c>done</c> or
/// <see cref="AgentLoopOptions.MaxIterations"/> is reached.
///
/// Model-family specifics (chat markers, anti-prompts) live in
/// <see cref="ChatTemplate"/>. The constructor takes the template; default is
/// <see cref="ChatTemplates.Gemma"/> (back-compat for callers built when this
/// class was Gemma-only). Pass <see cref="ChatTemplates.Llama31"/> for
/// Nemotron Nano. Per CLAUDE.md "no premature abstractions" we keep this
/// single class with template injection rather than splitting into
/// <c>GemmaLocalAgentLoop</c> + <c>NemotronLocalAgentLoop</c> — the
/// divergence is small (markers + anti-prompts only).
/// </summary>
public sealed class LocalAgentLoop : IAgentLoop
{
    private readonly LlamaSharpLocalLlm _llm;
    private readonly IAgentToolbelt _host;
    private readonly AgentLoopOptions _opts;
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

    public LocalAgentLoop(
        LlamaSharpLocalLlm llm,
        IAgentToolbelt host,
        AgentLoopOptions? opts = null,
        ChatTemplate? template = null)
    {
        _llm = llm;
        _host = host;
        _opts = opts ?? new AgentLoopOptions();
        _template = template ?? ChatTemplates.Gemma;
        var (weights, modelParams) = llm.GetInternals();
        _context = weights.CreateContext(modelParams);
        _executor = new InteractiveExecutor(_context);
        _grammar = new Grammar(AgentToolGrammar.Gbnf, AgentToolGrammar.GrammarRootRule);
    }

    /// <summary>
    /// Runs the agent loop for the given user request. Returns when the
    /// model calls <c>done</c>, when iterations are exhausted, or when the
    /// toolbelt throws.
    /// </summary>
    public async Task<AgentLoopRun> RunAsync(string userRequest, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalAgentLoop));

        _userSendCount++;
        var turns = new List<ToolTurn>();
        string? failure = null;
        var guards = new AgentLoopGuards();

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
                return new AgentLoopRun(turns, doneMsg, TerminatedCleanly: true, FailureReason: null)
                    { GuardStats = guards.Snapshot() };
            }

            // 2-stage repeat defense: first occurrence over the cap is fed
            // back to the model as an error toolResult so it can self-correct.
            // Only when the model ignores that feedback MaxConsecutiveBlocks
            // times in a row do we abort the whole session.
            var blockMsg = guards.CheckRepeat(call, _opts.MaxSameCallRepeats);
            if (blockMsg is not null)
            {
                if (guards.ShouldHardStop(_opts.MaxConsecutiveBlocks))
                {
                    failure = $"aborted after {guards.ConsecutiveBlocks} consecutive blocked repeats";
                    break;
                }
                var blockTurn = new ToolTurn(call, blockMsg);
                turns.Add(blockTurn);
                try { _opts.OnTurnCompleted?.Invoke(blockTurn); } catch { /* UI errors must not break the loop */ }
                continue;
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
            guards.RecordResult(call, toolResult);

            // Stream this turn to the UI immediately so the user sees the
            // agent's progress in real time. Callback runs on the task that
            // owns this loop — UI implementations marshal to dispatcher.
            try { _opts.OnTurnCompleted?.Invoke(turn); } catch { /* UI errors must not break the loop */ }
        }

        return new AgentLoopRun(
            turns,
            FinalMessage: failure ?? $"max iterations ({_opts.MaxIterations}) reached without 'done'",
            TerminatedCleanly: false,
            FailureReason: failure)
            { GuardStats = guards.Snapshot() };
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
        var tokensSeen = 0;
        var interval = Math.Max(1, _opts.ProgressTokenInterval);
        // Fire once at turn start so the UI can switch from "thinking" to
        // "generating" immediately (not only after 64 tokens land).
        try { _opts.OnGenerationProgress?.Invoke("generating", 0); } catch { }
        await foreach (var tok in _executor.InferAsync(prompt, inferenceParams, ct))
        {
            sb.Append(tok);
            tokensSeen++;
            if (tokensSeen % interval == 0)
            {
                try { _opts.OnGenerationProgress?.Invoke("generating", tokensSeen); }
                catch { /* UI errors must not break the loop */ }
            }
        }

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

            case "wait":
            {
                // Pure time delay — no host involvement. Clamped to [1, 30]
                // so the model can't accidentally stall the loop with wait(9999).
                // Returns a synthesized result so the next turn sees confirmation
                // and decides what to do next.
                var requested = ReadInt(call.Args, "seconds", 5);
                var seconds = Math.Clamp(requested, 1, 30);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                return JsonSerializer.Serialize(new { ok = true, waited_seconds = seconds });
            }

            // ---- OS-control bridge (mission M0014) -----------------------
            // Toolbelt returns a JSON envelope ({"ok":...}) directly. The loop
            // forwards it verbatim — no re-serialization, no truncation.

            case "os_list_windows":
            {
                var filter = ReadString(call.Args, "title_filter", "");
                return await _host.OsListWindowsAsync(string.IsNullOrEmpty(filter) ? null : filter, ct);
            }

            case "os_screenshot":
            {
                var hwnd = ReadLong(call.Args, "hwnd", 0);
                var grayscale = ReadBool(call.Args, "grayscale", true);
                return await _host.OsScreenshotAsync(hwnd, grayscale, ct);
            }

            case "os_activate":
            {
                var hwnd = ReadLong(call.Args, "hwnd", 0);
                return await _host.OsActivateAsync(hwnd, ct);
            }

            case "os_element_tree":
            {
                var hwnd = ReadLong(call.Args, "hwnd", 0);
                var depth = Math.Clamp(ReadInt(call.Args, "depth", 30), 1, 50);
                var search = ReadString(call.Args, "search", "");
                return await _host.OsElementTreeAsync(hwnd, depth, string.IsNullOrEmpty(search) ? null : search, ct);
            }

            case "os_mouse_click":
            {
                var x = ReadInt(call.Args, "x", 0);
                var y = ReadInt(call.Args, "y", 0);
                var right = ReadBool(call.Args, "right", false);
                var dbl = ReadBool(call.Args, "double", false);
                return await _host.OsMouseClickAsync(x, y, right, dbl, ct);
            }

            case "os_key_press":
            {
                var key = ReadString(call.Args, "key", "");
                return await _host.OsKeyPressAsync(key, ct);
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
        // Grammar guarantees structural validity for the local LLamaSharp loop
        // (GBNF sampler), but the *external* loop (Webnori/OpenAI/etc.) has no
        // grammar hook — Gemma 4 / non-Gemma can emit JSON that omits "tool"
        // or "args". JsonElement.GetProperty would throw KeyNotFoundException
        // ("The given key was not present in the dictionary."), which is *not*
        // a JsonException, so callers can't catch it as one. Use TryGetProperty
        // and raise JsonException for both shape errors so all malformed-shape
        // failures funnel through the same catch in the loop.
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tool", out var toolEl))
            throw new JsonException("missing 'tool' field");
        if (toolEl.ValueKind != JsonValueKind.String)
            throw new JsonException($"'tool' must be a string, got {toolEl.ValueKind}");
        var tool = toolEl.GetString()
            ?? throw new JsonException("'tool' is null");
        var argsObj = root.TryGetProperty("args", out var argsElement)
            ? JsonNode.Parse(argsElement.GetRawText())?.AsObject() ?? new JsonObject()
            : new JsonObject();
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

    private static long ReadLong(JsonObject args, string key, long fallback)
        => args.TryGetPropertyValue(key, out var v) && v is JsonValue jv
            && (jv.TryGetValue<long>(out var l) || (jv.TryGetValue<int>(out var i) && (l = i) == l))
            ? l
            : fallback;

    private static bool ReadBool(JsonObject args, string key, bool fallback)
        => args.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b)
            ? b
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

public sealed record AgentLoopOptions
{
    /// <summary>
    /// Max model turns before the loop gives up without seeing <c>done</c>.
    /// 12 budgets ~3 round-trips (send + wait + read = 3 calls) for a Mode 2
    /// relay; for autonomous N-turn discussions plan ~4N tool calls so a
    /// 5-turn conversation needs the caller to bump this further if desired.
    /// </summary>
    public int MaxIterations { get; init; } = 12;

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

    /// <summary>
    /// Optional callback fired periodically while the model is generating
    /// tokens for ONE turn. Lets the UI distinguish "model is slow" from
    /// "model is stuck" — without this the user sees nothing for 5–60s on
    /// a CPU first-turn, then either a sudden tool call or silence forever.
    ///
    /// Fires every <see cref="ProgressTokenInterval"/> tokens of streamed
    /// output. The callback receives a phase tag ("generating") and the
    /// running token count for the current turn (resets to 0 between turns).
    /// Same threading caveat as <see cref="OnTurnCompleted"/>: marshal to
    /// UI thread inside the callback if needed.
    /// </summary>
    public Action<string, int>? OnGenerationProgress { get; init; }

    /// <summary>
    /// Number of streamed tokens between progress callbacks. 64 keeps the
    /// UI responsive (~1 update / 0.5–4s depending on CPU/GPU) without
    /// flooding the dispatcher. Tuneable for tests.
    /// </summary>
    public int ProgressTokenInterval { get; init; } = 64;

    /// <summary>
    /// Maximum times a model may emit the *same* (tool + canonicalized args)
    /// call before the loop intercepts and feeds an error toolResult back.
    /// 3 mirrors AgentWin's ReActActor.MaxSameCallRepeats — enough to cover
    /// legitimate "send → wait → send same again" patterns once or twice
    /// while still catching genuine loops.
    /// </summary>
    public int MaxSameCallRepeats { get; init; } = 3;

    /// <summary>
    /// Hard stop after N consecutive blocked calls. When the model ignores
    /// the block-feedback message and tries the same call yet again, escalate
    /// from "warn the model" to "abort the session". 3 keeps the session
    /// alive long enough for self-correction without burning the whole
    /// MaxIterations budget on rejected calls.
    /// </summary>
    public int MaxConsecutiveBlocks { get; init; } = 3;

    /// <summary>
    /// How many transient HTTP errors (502/503/504/408/429/timeout/etc.) a
    /// REST-backed loop may absorb before declaring the iteration failed.
    /// Local LLamaSharp loops never trip this — the field exists on the
    /// shared options record because both backends share <see cref="AgentLoopGuards"/>.
    /// </summary>
    public int MaxLlmRetries { get; init; } = 1;
}
