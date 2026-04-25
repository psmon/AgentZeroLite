using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Common.Llm;
using Agent.Common.Llm.Tools;
using Xunit.Abstractions;

namespace ZeroCommon.Tests;

[Trait("Category", "AgentToolLoop")]
[Trait("Backend", "Cpu")]
[Trait("Model", "Gemma4-E4B")]
public sealed class AgentToolLoopTests
{
    private readonly ITestOutputHelper _output;

    private static readonly string ModelPath =
        Environment.GetEnvironmentVariable("GEMMA_MODEL_PATH")
        ?? @"D:\Code\AI\GemmaNet\models\gemma-4-E4B-it-UD-Q4_K_XL.gguf";

    public AgentToolLoopTests(ITestOutputHelper output) => _output = output;

    private static LocalLlmOptions Opts() => new()
    {
        ModelPath = ModelPath,
        Backend = LocalLlmBackend.Cpu,    // CPU per maintainer direction (stability)
        ContextSize = 4096,                // tool loop needs more room than Q&A tests
        MaxTokens = 192,                   // unused at LLM level here; loop overrides per turn
        Temperature = 0.1f,
    };

    /// <summary>
    /// T0 — sanity: with GBNF + the tool system prompt, Gemma's first turn
    /// produces grammar-valid JSON matching the {tool, args} schema. Predicts
    /// "yes" because GBNF enforces structure at the sampler — the model has
    /// no choice but to emit valid shape. If this fails, GBNF wiring is wrong
    /// at the LLamaSharp level, not a model-quality issue.
    /// </summary>
    [SkippableFact]
    public async Task T0_grammar_constrained_first_turn_emits_valid_tool_call_json()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());
        var host = new MockAgentToolHost();
        await using var loop = new AgentToolLoop(llm, host, new AgentToolLoopOptions { MaxIterations = 1 });

        var sw = Stopwatch.StartNew();
        var session = await loop.RunAsync(
            "What terminals are open right now?");
        sw.Stop();

        _output.WriteLine($"T0 elapsed={sw.ElapsedMilliseconds}ms turns={session.TurnCount} clean={session.TerminatedCleanly} final=\"{session.FinalMessage}\"");
        foreach (var t in session.Turns)
            _output.WriteLine($"  call: {t.Call.Tool} args={t.Call.Args.ToJsonString()} result_len={t.ToolResult.Length}");

        // GBNF sampler enforcement => structure is guaranteed. Failure here means
        // the LLamaSharp Grammar wiring is broken, not a model-quality issue.
        Assert.True(session.TurnCount >= 1 || session.TerminatedCleanly,
            $"expected at least one tool call; failure='{session.FailureReason}', final='{session.FinalMessage}'");

        // Either the model called a real tool (turn recorded) OR it called done
        // immediately (clean termination). Both are GBNF-valid outputs; both
        // prove the loop + grammar wiring works. The next test (T1G) checks
        // semantic correctness.
    }

    /// <summary>
    /// T1G — semantic: when asked about open terminals, Gemma's first call
    /// should be <c>list_terminals</c> (the only zero-arg discovery tool).
    /// Statistical assertion — non-deterministic LLMs may pick read_terminal
    /// or done occasionally, so we accept "list_terminals OR a known tool"
    /// here and tighten in T2G when there's a tool result to react to.
    /// </summary>
    [SkippableFact]
    public async Task T1G_first_call_for_discovery_question_is_a_known_tool()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());
        var host = new MockAgentToolHost();
        await using var loop = new AgentToolLoop(llm, host, new AgentToolLoopOptions { MaxIterations = 1 });

        var session = await loop.RunAsync(
            "List the terminals that are currently open.");

        // Single-iteration loop: either a tool fired (turn recorded), or done was called.
        var firstCallName = session.Turns.Count > 0
            ? session.Turns[0].Call.Tool
            : (session.TerminatedCleanly ? AgentToolGrammar.DoneToolName : "(none)");

        _output.WriteLine($"T1G first_call={firstCallName} clean={session.TerminatedCleanly}");

        Assert.Contains(firstCallName, AgentToolGrammar.KnownTools);
    }

    /// <summary>
    /// T2G — multi-turn: after we feed the model a list_terminals result,
    /// it should either call read_terminal/send_to_terminal/send_key (further
    /// action) OR done (satisfied). Verifies multi-turn KV cache works under
    /// grammar enforcement and the model recognizes the result format.
    /// </summary>
    [SkippableFact]
    public async Task T2G_multi_turn_after_list_result_picks_known_followup()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());
        var host = new MockAgentToolHost
        {
            ListTerminalsResult =
                """
                {"groups":[{"index":0,"name":"main","tabs":[
                  {"index":0,"title":"Claude","running":true},
                  {"index":1,"title":"Codex","running":true}
                ]}]}
                """,
        };
        await using var loop = new AgentToolLoop(llm, host, new AgentToolLoopOptions { MaxIterations = 4 });

        var session = await loop.RunAsync(
            "Find out what's in the Claude terminal right now and tell me.");

        _output.WriteLine($"T2G turns={session.TurnCount} clean={session.TerminatedCleanly} final=\"{session.FinalMessage}\"");
        foreach (var t in session.Turns)
            _output.WriteLine($"  call: {t.Call.Tool} args={t.Call.Args.ToJsonString()}");

        // Two reasonable outcomes:
        //   (a) clean termination via done — model decided list result was enough
        //   (b) turn recorded with a known tool — model wants to read further
        var allCallsKnown = session.Turns.All(t => AgentToolGrammar.KnownTools.Contains(t.Call.Tool));
        Assert.True(allCallsKnown,
            $"all calls must be from the known tool set; saw: [{string.Join(", ", session.Turns.Select(t => t.Call.Tool))}]");
        Assert.True(session.TurnCount > 0 || session.TerminatedCleanly,
            $"expected progress; failure='{session.FailureReason}'");
    }

    /// <summary>
    /// T_parser — pure unit test, no model load. Verifies the JSON parsing
    /// helpers handle the GBNF output shape correctly. Runs even without a
    /// model file present (no Skip).
    /// </summary>
    [Fact]
    public void Parser_extracts_tool_and_args_from_grammar_output()
    {
        var raw = """{"tool": "read_terminal", "args": {"group": 0, "tab": 1, "last_n": 500}}""";
        var call = AgentToolLoop.ParseToolCall(raw);

        Assert.Equal("read_terminal", call.Tool);
        Assert.Equal(0, call.Args["group"]!.GetValue<int>());
        Assert.Equal(1, call.Args["tab"]!.GetValue<int>());
        Assert.Equal(500, call.Args["last_n"]!.GetValue<int>());
    }

    [Fact]
    public void Parser_handles_empty_args_object_for_zero_arg_tool()
    {
        var raw = """{"tool": "list_terminals", "args": {}}""";
        var call = AgentToolLoop.ParseToolCall(raw);

        Assert.Equal("list_terminals", call.Tool);
        Assert.Empty(call.Args);
    }

    [Fact]
    public void Parser_extracts_string_args_with_done_message()
    {
        var raw = """{"tool": "done", "args": {"message": "All set."}}""";
        var call = AgentToolLoop.ParseToolCall(raw);

        Assert.Equal("done", call.Tool);
        Assert.Equal("All set.", call.Args["message"]!.GetValue<string>());
    }
}

/// <summary>
/// Test double for <see cref="IAgentToolHost"/>. Records calls and returns
/// scripted results so we can assert what the LLM did without standing up the
/// real terminal/actor topology.
/// </summary>
internal sealed class MockAgentToolHost : IAgentToolHost
{
    public List<(string Tool, string Args)> Calls { get; } = new();

    public string ListTerminalsResult { get; set; } =
        """{"groups":[{"index":0,"name":"main","tabs":[]}]}""";

    public Func<int, int, int, string> ReadTerminalResultFor { get; set; } =
        (g, t, n) => $"(no output for {g}:{t})";

    public Task<string> ListTerminalsAsync(CancellationToken ct)
    {
        Calls.Add(("list_terminals", "{}"));
        return Task.FromResult(ListTerminalsResult);
    }

    public Task<string> ReadTerminalAsync(int group, int tab, int lastN, CancellationToken ct)
    {
        Calls.Add(("read_terminal", $"{{\"group\":{group},\"tab\":{tab},\"last_n\":{lastN}}}"));
        return Task.FromResult(ReadTerminalResultFor(group, tab, lastN));
    }

    public Task<bool> SendToTerminalAsync(int group, int tab, string text, CancellationToken ct)
    {
        Calls.Add(("send_to_terminal", JsonSerializer.Serialize(new { group, tab, text })));
        return Task.FromResult(true);
    }

    public Task<bool> SendKeyAsync(int group, int tab, string key, CancellationToken ct)
    {
        Calls.Add(("send_key", JsonSerializer.Serialize(new { group, tab, key })));
        return Task.FromResult(true);
    }
}
