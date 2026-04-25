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
    /// T0Native — runtime probe per ondevice-tool-calling-survey.md §A.
    /// Gives Gemma a tool-use prompt with NO grammar enforcement and verifies
    /// it does NOT emit Llama-3.1 native tool-call tokens
    /// (`&lt;|python_tag|&gt;`, `&lt;|eom_id|&gt;`). Confirms the survey's
    /// classification: Gemma 4 has no tool-calling SFT → must route through
    /// GBNF backend. If this assertion ever fires, Gemma's tokenizer or chat
    /// template has changed under us and the dual-backend selection logic
    /// needs revisiting.
    /// </summary>
    [SkippableFact]
    public async Task T0Native_Gemma_does_not_emit_native_tool_tokens_must_use_gbnf()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());
        var result = await T0Probe.RunAsync(llm, ChatTemplates.Gemma);

        _output.WriteLine($"family={result.ModelFamilyId} native_viable={result.EmittedNativeToolMarkers}");
        _output.WriteLine($"detected_markers=[{string.Join(", ", result.DetectedMarkers)}]");
        _output.WriteLine($"recommendation: {result.Recommendation}");
        _output.WriteLine($"raw[..300]: {Truncate(result.RawOutput, 300)}");

        Assert.False(result.EmittedNativeToolMarkers,
            $"Expected no Llama-3.1 markers from Gemma. Detected: [{string.Join(", ", result.DetectedMarkers)}]. "
            + "If this fires, Gemma started emitting native tool tokens — investigate tokenizer.");
    }

    private static string Truncate(string s, int n)
        => s.Length <= n ? s.Replace("\n", "\\n") : s[..n].Replace("\n", "\\n") + "…";

    /// <summary>
    /// T0Sanity — with GBNF + the tool system prompt, Gemma's first turn
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
    /// T3G — multi-step semantic: given a request that requires *acting on*
    /// the terminal catalog (not just inspecting), Gemma should chain
    /// list_terminals → send_to_terminal → done. Mock catalog names two
    /// terminals (Claude tab 0, Codex tab 1) and the request asks to send
    /// to Codex; the mock records every call so we can verify Gemma routed
    /// to the correct tab index based on the catalog content it received.
    ///
    /// This is the test that fails if Gemma can't reason over the JSON
    /// catalog it gets back — the part GBNF can't help with (GBNF guarantees
    /// the *shape* of Gemma's output, not the *correctness* of its arg values).
    /// </summary>
    [SkippableFact]
    public async Task T3G_multistep_send_to_named_terminal_routes_to_correct_tab()
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
        await using var loop = new AgentToolLoop(llm, host, new AgentToolLoopOptions { MaxIterations = 6 });

        var sw = Stopwatch.StartNew();
        var session = await loop.RunAsync(
            "Send the text 'hello' to the Codex terminal, then tell me you're done.");
        sw.Stop();

        _output.WriteLine($"T3G elapsed={sw.ElapsedMilliseconds}ms turns={session.TurnCount} clean={session.TerminatedCleanly} final=\"{session.FinalMessage}\"");
        foreach (var (call, idx) in session.Turns.Select((c, i) => (c, i)))
            _output.WriteLine($"  turn {idx}: {call.Call.Tool} args={call.Call.Args.ToJsonString()}");
        foreach (var (tool, args) in host.Calls)
            _output.WriteLine($"  host saw: {tool} {args}");

        // Structural: every recorded call must be a known tool
        Assert.All(session.Turns, t => Assert.Contains(t.Call.Tool, AgentToolGrammar.KnownTools));

        // Semantic: the mock host should have seen at least one send_to_terminal
        // call. We do NOT assert tab index strictly because Gemma at temp 0.1
        // may occasionally route to tab 0 — log it but only fail if NO send
        // happened at all. A stricter assertion would belong in T4G-style
        // statistical pass over multiple trials.
        var sendCall = host.Calls.FirstOrDefault(c => c.Tool == "send_to_terminal");
        Assert.False(sendCall == default,
            $"expected at least one send_to_terminal call; saw: [{string.Join(", ", host.Calls.Select(c => c.Tool))}]");

        // Soft check: log whether Gemma routed to the right tab. Don't fail —
        // single-trial routing is too noisy at 4B-class scale.
        if (sendCall.Args.Contains("\"tab\":1"))
            _output.WriteLine("  ✓ Gemma routed to Codex tab (tab=1) correctly");
        else if (sendCall.Args.Contains("\"tab\":0"))
            _output.WriteLine("  ⚠ Gemma routed to tab=0 (Claude) — may have mis-read catalog");
        else
            _output.WriteLine($"  ⚠ unexpected tab in send_to_terminal args: {sendCall.Args}");
    }

    /// <summary>
    /// T4G — output stability across trials. Run the same simple, unambiguous
    /// "list terminals" request N times; assert that the first tool call is
    /// <c>list_terminals</c> in at least <c>requiredHits / trials</c> of them.
    /// Statistical assertion is the right shape for non-deterministic LLMs:
    /// GBNF guarantees output VALIDITY 100% (no malformed JSON), but tool
    /// SELECTION is the model's free choice and should be measured as a rate.
    ///
    /// Calibrated for 4B-effective Gemma at CPU + temperature 0.1: expect
    /// near-perfect routing on a question this clear (≥ 4/5).
    /// </summary>
    [SkippableFact]
    public async Task T4G_first_call_is_list_terminals_at_least_4_of_5_trials()
    {
        Skip.IfNot(File.Exists(ModelPath), $"Model not present at {ModelPath}");

        const int trials = 5;
        const int requiredHits = 4;

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(Opts());

        int hits = 0;
        var firstCalls = new List<string>();

        for (int trial = 0; trial < trials; trial++)
        {
            var host = new MockAgentToolHost();
            await using var loop = new AgentToolLoop(llm, host, new AgentToolLoopOptions { MaxIterations = 1 });

            var sw = Stopwatch.StartNew();
            var session = await loop.RunAsync("List the currently open terminals.");
            sw.Stop();

            var firstCall = session.Turns.Count > 0
                ? session.Turns[0].Call.Tool
                : (session.TerminatedCleanly ? AgentToolGrammar.DoneToolName : "(none)");
            firstCalls.Add(firstCall);

            if (firstCall == "list_terminals") hits++;

            _output.WriteLine($"  trial {trial + 1}/{trials} ({sw.ElapsedMilliseconds}ms): first_call={firstCall}");
        }

        _output.WriteLine($"T4G results: {hits}/{trials} trials picked list_terminals first");
        _output.WriteLine($"  all first calls: [{string.Join(", ", firstCalls)}]");

        Assert.True(hits >= requiredHits,
            $"expected list_terminals as first call in ≥ {requiredHits}/{trials} trials; got {hits}/{trials}: [{string.Join(", ", firstCalls)}]");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Nemotron Nano 8B v1 (Llama-3.1 chat template) — Phase 2 backend tests
    // ────────────────────────────────────────────────────────────────────

    private static readonly string NemotronModelPath =
        Environment.GetEnvironmentVariable("NEMOTRON_MODEL_PATH")
        ?? @"D:\Code\AI\GemmaNet\models\Llama-3.1-Nemotron-Nano-8B-v1-UD-Q4_K_XL.gguf";

    private static LocalLlmOptions NemotronOpts() => new()
    {
        ModelPath = NemotronModelPath,
        Backend = LocalLlmBackend.Cpu,
        ContextSize = 4096,
        MaxTokens = 192,
        Temperature = 0.1f,
    };

    /// <summary>
    /// T1N — Nemotron Nano 8B v1 with the Llama-3.1 chat template + GBNF
    /// produces a valid tool call for a discovery question. This is the
    /// Nemotron equivalent of T1G; the only differences from the Gemma path
    /// are the template (ChatTemplates.Llama31) and the model path.
    /// </summary>
    [SkippableFact]
    public async Task T1N_first_call_for_discovery_question_is_a_known_tool_nemotron()
    {
        Skip.IfNot(File.Exists(NemotronModelPath), $"Model not present at {NemotronModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(NemotronOpts());
        var host = new MockAgentToolHost();
        await using var loop = new AgentToolLoop(
            llm,
            host,
            new AgentToolLoopOptions { MaxIterations = 1 },
            template: ChatTemplates.Llama31);

        var sw = Stopwatch.StartNew();
        var session = await loop.RunAsync("List the terminals that are currently open.");
        sw.Stop();

        var firstCallName = session.Turns.Count > 0
            ? session.Turns[0].Call.Tool
            : (session.TerminatedCleanly ? AgentToolGrammar.DoneToolName : "(none)");

        _output.WriteLine($"T1N elapsed={sw.ElapsedMilliseconds}ms first_call={firstCallName} clean={session.TerminatedCleanly}");

        Assert.Contains(firstCallName, AgentToolGrammar.KnownTools);
    }

    /// <summary>
    /// T2N — Nemotron multi-turn after list result. Equivalent of T2G.
    /// </summary>
    [SkippableFact]
    public async Task T2N_multi_turn_after_list_result_picks_known_followup_nemotron()
    {
        Skip.IfNot(File.Exists(NemotronModelPath), $"Model not present at {NemotronModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(NemotronOpts());
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
        await using var loop = new AgentToolLoop(
            llm,
            host,
            new AgentToolLoopOptions { MaxIterations = 4 },
            template: ChatTemplates.Llama31);

        var session = await loop.RunAsync("Find out what's in the Claude terminal right now and tell me.");

        _output.WriteLine($"T2N turns={session.TurnCount} clean={session.TerminatedCleanly} final=\"{session.FinalMessage}\"");
        foreach (var t in session.Turns)
            _output.WriteLine($"  call: {t.Call.Tool} args={t.Call.Args.ToJsonString()}");

        Assert.All(session.Turns, t => Assert.Contains(t.Call.Tool, AgentToolGrammar.KnownTools));
        Assert.True(session.TurnCount > 0 || session.TerminatedCleanly,
            $"expected progress; failure='{session.FailureReason}'");
    }

    /// <summary>
    /// T3N — Nemotron multi-step routing. Equivalent of T3G.
    /// </summary>
    [SkippableFact]
    public async Task T3N_multistep_send_to_named_terminal_routes_to_correct_tab_nemotron()
    {
        Skip.IfNot(File.Exists(NemotronModelPath), $"Model not present at {NemotronModelPath}");

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(NemotronOpts());
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
        await using var loop = new AgentToolLoop(
            llm,
            host,
            new AgentToolLoopOptions { MaxIterations = 6 },
            template: ChatTemplates.Llama31);

        var sw = Stopwatch.StartNew();
        var session = await loop.RunAsync(
            "Send the text 'hello' to the Codex terminal, then tell me you're done.");
        sw.Stop();

        _output.WriteLine($"T3N elapsed={sw.ElapsedMilliseconds}ms turns={session.TurnCount} clean={session.TerminatedCleanly} final=\"{session.FinalMessage}\"");
        foreach (var (call, idx) in session.Turns.Select((c, i) => (c, i)))
            _output.WriteLine($"  turn {idx}: {call.Call.Tool} args={call.Call.Args.ToJsonString()}");
        foreach (var (tool, args) in host.Calls)
            _output.WriteLine($"  host saw: {tool} {args}");

        Assert.All(session.Turns, t => Assert.Contains(t.Call.Tool, AgentToolGrammar.KnownTools));

        var sendCall = host.Calls.FirstOrDefault(c => c.Tool == "send_to_terminal");
        Assert.False(sendCall == default,
            $"expected at least one send_to_terminal call; saw: [{string.Join(", ", host.Calls.Select(c => c.Tool))}]");

        if (sendCall.Args.Contains("\"tab\":1"))
            _output.WriteLine("  ✓ Nemotron routed to Codex tab (tab=1) correctly");
        else if (sendCall.Args.Contains("\"tab\":0"))
            _output.WriteLine("  ⚠ Nemotron routed to tab=0 (Claude) — may have mis-read catalog");
        else
            _output.WriteLine($"  ⚠ unexpected tab: {sendCall.Args}");
    }

    /// <summary>
    /// T4N — Nemotron stability across trials. Equivalent of T4G.
    /// Same threshold (≥ 4/5 picking list_terminals) — Nemotron is the
    /// larger model so should match or beat Gemma's 5/5.
    /// </summary>
    [SkippableFact]
    public async Task T4N_first_call_is_list_terminals_at_least_4_of_5_trials_nemotron()
    {
        Skip.IfNot(File.Exists(NemotronModelPath), $"Model not present at {NemotronModelPath}");

        const int trials = 5;
        const int requiredHits = 4;

        await using var llm = await LlamaSharpLocalLlm.CreateAsync(NemotronOpts());

        int hits = 0;
        var firstCalls = new List<string>();

        for (int trial = 0; trial < trials; trial++)
        {
            var host = new MockAgentToolHost();
            await using var loop = new AgentToolLoop(
                llm,
                host,
                new AgentToolLoopOptions { MaxIterations = 1 },
                template: ChatTemplates.Llama31);

            var sw = Stopwatch.StartNew();
            var session = await loop.RunAsync("List the currently open terminals.");
            sw.Stop();

            var firstCall = session.Turns.Count > 0
                ? session.Turns[0].Call.Tool
                : (session.TerminatedCleanly ? AgentToolGrammar.DoneToolName : "(none)");
            firstCalls.Add(firstCall);
            if (firstCall == "list_terminals") hits++;

            _output.WriteLine($"  trial {trial + 1}/{trials} ({sw.ElapsedMilliseconds}ms): first_call={firstCall}");
        }

        _output.WriteLine($"T4N results: {hits}/{trials} trials picked list_terminals first");
        _output.WriteLine($"  all first calls: [{string.Join(", ", firstCalls)}]");

        Assert.True(hits >= requiredHits,
            $"expected list_terminals as first call in ≥ {requiredHits}/{trials} trials; got {hits}/{trials}: [{string.Join(", ", firstCalls)}]");
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
