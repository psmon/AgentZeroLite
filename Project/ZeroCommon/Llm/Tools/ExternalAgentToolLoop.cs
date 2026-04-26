using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common.Llm.Providers;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// AIMODE tool loop that drives an external OpenAI-compatible provider
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
public sealed class ExternalAgentToolLoop : IAgentToolLoop
{
    private readonly ILlmProvider _provider;
    private readonly string _model;
    private readonly IAgentToolHost _host;
    private readonly AgentToolLoopOptions _opts;
    private readonly List<LlmMessage> _messages = [];
    private bool _isFirstUserSend = true;
    private int _userSendCount;
    private bool _disposed;

    public int UserSendCount => _userSendCount;

    public ExternalAgentToolLoop(ILlmProvider provider, string model, IAgentToolHost host,
        AgentToolLoopOptions? opts = null)
    {
        _provider = provider;
        _model = model;
        _host = host;
        _opts = opts ?? new AgentToolLoopOptions();
    }

    public async Task<AgentToolSession> RunAsync(string userRequest, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ExternalAgentToolLoop));

        _userSendCount++;
        var turns = new List<ToolTurn>();
        string? failure = null;

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

            string assistantText;
            try
            {
                assistantText = await GenerateOneTurnAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = $"provider call failed at iteration {iter}: {ex.Message}";
                break;
            }

            _messages.Add(LlmMessage.Assistant(assistantText));

            var rawJson = ExtractFirstJsonObject(assistantText);
            if (rawJson is null)
            {
                failure = $"model emitted no JSON envelope at iteration {iter} (non-Gemma toolchain mismatch?): \"{Truncate(assistantText, 200)}\"";
                break;
            }

            ToolCall call;
            try
            {
                call = AgentToolLoop.ParseToolCall(rawJson);
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

            try { _opts.OnTurnCompleted?.Invoke(turn); } catch { /* UI errors must not break the loop */ }

            _messages.Add(LlmMessage.User(
                $"--- TOOL RESULT ---\n{toolResult}\n\n(Reply with the next single JSON tool call. Call \"done\" when satisfied.)"));
        }

        return new AgentToolSession(
            turns,
            FinalMessage: failure ?? $"max iterations ({_opts.MaxIterations}) reached without 'done'",
            TerminatedCleanly: false,
            FailureReason: failure);
    }

    private async Task<string> GenerateOneTurnAsync(CancellationToken ct)
    {
        var request = new LlmRequest
        {
            Model = _model,
            Messages = _messages.ToList(),
            Temperature = _opts.Temperature,
            MaxTokens = _opts.MaxTokensPerTurn,
        };

        var sb = new StringBuilder();
        var chunksSeen = 0;
        var interval = Math.Max(1, _opts.ProgressTokenInterval);
        try { _opts.OnGenerationProgress?.Invoke("generating", 0); } catch { }
        await foreach (var chunk in _provider.StreamAsync(request, ct))
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
        return sb.ToString().Trim();
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

    // Tool dispatch is deliberately duplicated from AgentToolLoop instead of
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
