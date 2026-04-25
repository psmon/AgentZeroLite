using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Drives a Gemma-style on-device LLM through a multi-turn tool-calling
/// session against an <see cref="IAgentToolHost"/>. Output is constrained at
/// the sampler level by <see cref="AgentToolGrammar.Gbnf"/> so every turn
/// produces parseable JSON; the loop dispatches to the host, appends the
/// result to context, and continues until the model emits <c>done</c> or
/// <see cref="AgentToolLoopOptions.MaxIterations"/> is reached.
///
/// This is the Gemma 4 backend (no native tool tokens). The Nemotron Nano
/// path will be a sibling class (<c>NativeAgentToolLoop</c>) added in Phase 2;
/// shared driver code stays inline here until a third backend forces the
/// extraction of an <c>IAgentToolBackend</c> interface (per CLAUDE.md
/// "no premature abstractions").
/// </summary>
public sealed class AgentToolLoop : IAsyncDisposable
{
    private static readonly string[] AntiPrompts = { "<end_of_turn>", "<eos>" };

    private readonly LlamaSharpLocalLlm _llm;
    private readonly IAgentToolHost _host;
    private readonly AgentToolLoopOptions _opts;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;
    private readonly Grammar _grammar;
    private bool _firstTurn = true;
    private bool _disposed;

    public AgentToolLoop(LlamaSharpLocalLlm llm, IAgentToolHost host, AgentToolLoopOptions? opts = null)
    {
        _llm = llm;
        _host = host;
        _opts = opts ?? new AgentToolLoopOptions();
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

        var turns = new List<ToolTurn>();
        string? failure = null;

        for (var iter = 0; iter < _opts.MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            var turnInput = iter == 0
                ? FormatFirstTurn(userRequest)
                : FormatToolResultTurn(turns[^1].ToolResult);

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
            turns.Add(new ToolTurn(call, toolResult));
        }

        return new AgentToolSession(
            turns,
            FinalMessage: failure ?? $"max iterations ({_opts.MaxIterations}) reached without 'done'",
            TerminatedCleanly: false,
            FailureReason: failure);
    }

    private async Task<string> GenerateOneTurnAsync(string promptForThisTurn, CancellationToken ct)
    {
        var prompt = _firstTurn
            ? $"<start_of_turn>user\n{promptForThisTurn}<end_of_turn>\n<start_of_turn>model\n"
            : $"\n<start_of_turn>user\n{promptForThisTurn}<end_of_turn>\n<start_of_turn>model\n";
        _firstTurn = false;

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _opts.MaxTokensPerTurn,
            AntiPrompts = AntiPrompts,
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

    private static string StripTrailingAntiPrompt(string text)
    {
        foreach (var anti in AntiPrompts)
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

    /// <summary>Max tokens emitted per model turn. Tool-call JSONs are short; cap low.</summary>
    public int MaxTokensPerTurn { get; init; } = 192;

    /// <summary>Sampling temperature. Low for stable, deterministic-leaning tool calls.</summary>
    public float Temperature { get; init; } = 0.1f;
}
