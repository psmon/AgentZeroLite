using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common;
using Agent.Common.Llm.Providers;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// AIMODE agent loop that drives an external OpenAI-compatible provider
/// (Webnori/OpenAI/LMStudio/Ollama) instead of an on-device LLamaSharp model.
///
/// Toolchain stance: <b>Gemma 4 is the standard.</b> We send the same
/// <see cref="AgentToolGrammar.SystemPrompt"/> + tool catalog as the local
/// loop, but <i>without</i> GBNF — REST providers don't expose a grammar
/// hook. Gemma 4 follows the in-context schema reliably; non-Gemma models
/// (OpenAI's gpt-*, Llama-3.x via Ollama, etc.) will likely emit free-form
/// prose around or instead of the JSON envelope. That mismatch is
/// <b>by design</b>: the user has explicitly scoped non-Gemma compatibility
/// as follow-up work, so this loop fails fast (with a clear failure
/// reason) rather than special-casing each model family.
///
/// Stateful concerns the local loop solves with KV cache (rolling system
/// prompt + history) we solve here with an explicit <see cref="LlmMessage"/>
/// list — REST is stateless so each turn replays the full history. The
/// provider's context window bounds growth; we don't trim client-side yet.
/// </summary>
public sealed class ExternalAgentLoop : IAgentLoop
{
    private readonly ILlmProvider _provider;
    private readonly string _model;
    private readonly IAgentToolbelt _host;
    private readonly AgentLoopOptions _opts;
    private readonly List<LlmMessage> _messages = [];
    private bool _isFirstUserSend = true;
    private int _userSendCount;
    private bool _disposed;

    public int UserSendCount => _userSendCount;

    public ExternalAgentLoop(ILlmProvider provider, string model, IAgentToolbelt host,
        AgentLoopOptions? opts = null)
    {
        _provider = provider;
        _model = model;
        _host = host;
        _opts = opts ?? new AgentLoopOptions();
    }

    public async Task<AgentLoopRun> RunAsync(string userRequest, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExternalAgentLoop));

        _userSendCount++;
        var turns = new List<ToolTurn>();
        string? failure = null;
        var guards = new AgentLoopGuards();

        // First send seeds the system prompt + tool catalog. Follow-up sends
        // append only the new user request — the system message is already in
        // the messages[] list (REST history) and re-sending it would inflate
        // every prompt.
        if (_isFirstUserSend)
        {
            _messages.Add(LlmMessage.System(AgentToolGrammar.SystemPrompt));
            _messages.Add(LlmMessage.User($"--- USER REQUEST ---\n{userRequest}"));
            _isFirstUserSend = false;
        }
        else
        {
            _messages.Add(LlmMessage.User($"--- USER REQUEST ---\n{userRequest}"));
        }

        for (var iter = 0; iter < _opts.MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            var (assistantText, callError) = await CallProviderWithRetryAsync(iter, guards, ct);
            if (callError is not null)
            {
                failure = callError;
                break;
            }

            _messages.Add(LlmMessage.Assistant(assistantText!));

            var rawJson = ExtractFirstJsonObject(assistantText!);
            if (rawJson is null)
            {
                failure = $"model emitted no JSON envelope at iteration {iter} (non-Gemma toolchain mismatch?): \"{Truncate(assistantText!, 200)}\"";
                break;
            }

            ToolCall call;
            try
            {
                call = LocalAgentLoop.ParseToolCall(rawJson);
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

            // Same 2-stage repeat defense as LocalAgentLoop. The block message
            // is also fed back through the REST history so the model sees it
            // on the next turn just like any other tool result.
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
                _messages.Add(LlmMessage.User(
                    $"--- TOOL RESULT ---\n{blockMsg}\n\n(Reply with the next single JSON tool call. Call \"done\" when satisfied.)"));
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

            try { _opts.OnTurnCompleted?.Invoke(turn); } catch { /* UI errors must not break the loop */ }

            _messages.Add(LlmMessage.User(
                $"--- TOOL RESULT ---\n{toolResult}\n\n(Reply with the next single JSON tool call. Call \"done\" when satisfied.)"));
        }

        return new AgentLoopRun(
            turns,
            FinalMessage: failure ?? $"max iterations ({_opts.MaxIterations}) reached without 'done'",
            TerminatedCleanly: false,
            FailureReason: failure)
            { GuardStats = guards.Snapshot() };
    }

    /// <summary>
    /// Generates one turn with transient-HTTP retry. Exhausting
    /// <see cref="AgentLoopOptions.MaxLlmRetries"/> turns the next failure
    /// into the loop's overall <c>failure</c>; non-transient exceptions bubble
    /// out immediately so we don't burn the budget on permanent errors
    /// (auth, model-not-found, etc.). OperationCanceledException always
    /// propagates.
    /// </summary>
    private async Task<(string? Text, string? Error)> CallProviderWithRetryAsync(
        int iter, AgentLoopGuards guards, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return (await GenerateOneTurnAsync(ct), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (AgentLoopGuards.IsTransientHttpError(ex)
                    && guards.TryConsumeLlmRetry(_opts.MaxLlmRetries))
                {
                    var backoff = guards.CurrentBackoff();
                    if (backoff > TimeSpan.Zero)
                        await Task.Delay(backoff, ct);
                    continue;
                }
                return (null, $"provider call failed at iteration {iter}: {ex.Message}");
            }
        }
    }

    private async Task<string> GenerateOneTurnAsync(CancellationToken ct)
    {
        // M0017 후속 #2: bound each turn with TurnTimeout. Webnori (and other
        // OpenAI-compatible SSE endpoints) occasionally hold the connection
        // open without yielding chunks OR end-of-stream OR an error, so the
        // bare `await foreach` would hang forever. Linked CTS lets the caller
        // still cancel via the outer `ct` while we add our own deadline.
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        turnCts.CancelAfter(_opts.TurnTimeout);
        var turnCt = turnCts.Token;

        var request = new LlmRequest
        {
            Model = _model,
            Messages = _messages.ToList(),
            Temperature = _opts.Temperature,
            MaxTokens = _opts.MaxTokensPerTurn,
        };

        var startedAt = DateTime.UtcNow;
        AppLogger.Log(
            $"[ExternalAgentLoop] turn start — model={_model} historyMsgs={_messages.Count} " +
            $"timeout={_opts.TurnTimeout.TotalSeconds:F0}s maxTokens={_opts.MaxTokensPerTurn}");

        var sb = new StringBuilder();
        var chunksSeen = 0;
        var interval = Math.Max(1, _opts.ProgressTokenInterval);
        try { _opts.OnGenerationProgress?.Invoke("generating", 0); } catch { }
        try
        {
            await foreach (var chunk in _provider.StreamAsync(request, turnCt))
            {
                if (string.IsNullOrEmpty(chunk.Text)) continue;
                sb.Append(chunk.Text);
                // Approximate progress via SSE chunk count — real token count is not
                // exposed by the OpenAI streaming protocol. Each chunk is typically
                // 1–4 tokens, so chunksSeen ≈ tokens / 2.
                chunksSeen++;
                if (chunksSeen % interval == 0)
                {
                    try { _opts.OnGenerationProgress?.Invoke("generating", chunksSeen); }
                    catch { }
                }
            }
        }
        catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Our deadline fired, not the outer caller's cancel. Surface as
            // TimeoutException so CallProviderWithRetryAsync routes it through
            // AgentLoopGuards.IsTransientHttpError (matches on "timeout") and
            // the MaxLlmRetries budget engages instead of bubbling out as an
            // OperationCanceledException (which would propagate immediately).
            var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
            AppLogger.Log(
                $"[ExternalAgentLoop] turn TIMEOUT after {elapsed:F1}s chunks={chunksSeen} " +
                $"chars={sb.Length} (limit={_opts.TurnTimeout.TotalSeconds:F0}s)");
            throw new TimeoutException(
                $"External provider stalled for {_opts.TurnTimeout.TotalSeconds:F0}s with no end-of-stream " +
                $"(chunks received: {chunksSeen}, chars buffered: {sb.Length}).");
        }

        var elapsedOk = (DateTime.UtcNow - startedAt).TotalSeconds;
        var text = sb.ToString().Trim();
        var preview = text.Length > 60 ? text[..60].Replace('\n', ' ') + "…" : text.Replace('\n', ' ');
        AppLogger.Log(
            $"[ExternalAgentLoop] turn ok — {elapsedOk:F1}s chunks={chunksSeen} chars={text.Length} preview=\"{preview}\"");
        return text;
    }

    /// <summary>
    /// Pulls the first balanced top-level JSON object from possibly-noisy
    /// model output. Tolerates markdown fences, leading prose, and trailing
    /// commentary. Returns null when no opening brace is found or the object
    /// is unterminated.
    /// </summary>
    internal static string? ExtractFirstJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    // Tool dispatch is deliberately duplicated from LocalAgentLoop instead of
    // extracted into a shared helper. The 5-tool switch is small and the two
    // loops have different evolution pressures (local will gain GBNF tweaks;
    // external will gain message-trimming / retry policies) — premature
    // abstraction would entangle them.
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
                var requested = ReadInt(call.Args, "seconds", 5);
                var seconds = Math.Clamp(requested, 1, 30);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                return JsonSerializer.Serialize(new { ok = true, waited_seconds = seconds });
            }

            // ---- OS-control bridge (mission M0014) -----------------------
            // Mirror of LocalAgentLoop.ExecuteToolAsync. The two switches stay
            // hand-aligned by convention; the small duplication is preferred
            // over premature abstraction (per CLAUDE.md guidance).

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

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        if (_provider is IDisposable d) d.Dispose();
        return ValueTask.CompletedTask;
    }
}
