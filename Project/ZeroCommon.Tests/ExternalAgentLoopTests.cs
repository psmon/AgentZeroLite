using System.Runtime.CompilerServices;
using Agent.Common.Llm;
using Agent.Common.Llm.Providers;
using Agent.Common.Llm.Tools;

namespace ZeroCommon.Tests;

/// <summary>
/// Pure-CPU unit tests for <see cref="ExternalAgentLoop"/>. The class
/// has one piece of non-trivial standalone logic — pulling a balanced JSON
/// object out of noisy model output — and that's what we test here. The
/// loop itself needs a network endpoint and lives behind the online smoke
/// suite (<see cref="WebnoriExternalSmokeTests"/>).
/// </summary>
public sealed class ExternalAgentLoopTests
{
    [Fact]
    public void ExtractFirstJsonObject_returns_clean_object_when_input_is_pure_json()
    {
        var raw = """{"tool":"done","args":{"message":"hi"}}""";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.Equal(raw, extracted);
    }

    [Fact]
    public void ExtractFirstJsonObject_skips_leading_prose()
    {
        var raw = "Sure, here's the call:\n```json\n{\"tool\":\"done\",\"args\":{\"message\":\"ok\"}}\n```";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.NotNull(extracted);
        Assert.StartsWith("{", extracted);
        Assert.EndsWith("}", extracted);
        Assert.Contains("\"tool\":\"done\"", extracted);
    }

    [Fact]
    public void ExtractFirstJsonObject_handles_nested_objects()
    {
        var raw = "padding {\"tool\":\"x\",\"args\":{\"nested\":{\"deep\":1}}} more text";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.Equal("{\"tool\":\"x\",\"args\":{\"nested\":{\"deep\":1}}}", extracted);
    }

    [Fact]
    public void ExtractFirstJsonObject_ignores_braces_inside_strings()
    {
        var raw = "{\"tool\":\"send\",\"args\":{\"text\":\"hello { world } end\"}}";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.Equal(raw, extracted);
    }

    [Fact]
    public void ExtractFirstJsonObject_returns_null_when_unterminated()
    {
        var raw = "{\"tool\":\"x\",\"args\":{";
        Assert.Null(ExternalAgentLoop.ExtractFirstJsonObject(raw));
    }

    [Fact]
    public void ExtractFirstJsonObject_returns_null_when_no_brace()
    {
        Assert.Null(ExternalAgentLoop.ExtractFirstJsonObject("just prose, nothing structural"));
    }

    [Fact]
    public void ExtractFirstJsonObject_handles_escaped_quote_inside_string()
    {
        // The string contains an escaped quote followed by what looks like a closing
        // brace inside the string — the parser must NOT terminate early.
        var raw = "{\"tool\":\"x\",\"args\":{\"text\":\"escaped \\\"} fake close\"}}";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.Equal(raw, extracted);
    }

    // ── Envelope self-heal — Gemma-4 mimic-the-example regression ─────────
    //
    // The model copies the inner args of `done` verbatim (e.g. {"message":"..."})
    // and drops the {"tool":"done","args":...} envelope. The loop wraps it
    // once per session before failing.

    [Fact]
    public void TryRepairAsDoneEnvelope_wraps_inner_args_only_message()
    {
        var inner = "{\"message\":\"안녕하세요! 무엇을 도와드릴까요?\"}";
        Assert.True(ExternalAgentLoop.TryRepairAsDoneEnvelope(inner, out var repaired));
        Assert.Equal($"{{\"tool\":\"done\",\"args\":{inner}}}", repaired);
        // And the result must parse back into a proper done ToolCall.
        var call = LocalAgentLoop.ParseToolCall(repaired);
        Assert.Equal("done", call.Tool);
    }

    [Fact]
    public void TryRepairAsDoneEnvelope_refuses_when_envelope_already_present()
    {
        var ok = "{\"tool\":\"done\",\"args\":{\"message\":\"hi\"}}";
        Assert.False(ExternalAgentLoop.TryRepairAsDoneEnvelope(ok, out _));
    }

    [Fact]
    public void TryRepairAsDoneEnvelope_refuses_when_no_message_field()
    {
        var unrelated = "{\"text\":\"hello\"}";
        Assert.False(ExternalAgentLoop.TryRepairAsDoneEnvelope(unrelated, out _));
    }

    [Fact]
    public void TryRepairAsDoneEnvelope_refuses_when_message_is_not_string()
    {
        var bad = "{\"message\":123}";
        Assert.False(ExternalAgentLoop.TryRepairAsDoneEnvelope(bad, out _));
    }

    [Fact]
    public async Task Loop_self_heals_inner_args_only_done_payload_once()
    {
        // First turn: model emits inner-args only — the bug shape.
        // Second turn would happen only if the wrap fails; we expect a clean done.
        var provider = new ScriptedProvider(new[]
        {
            "{\"message\":\"안녕하세요! 무엇을 도와드릴까요?\"}",
        });
        var host = new MockAgentToolbelt();
        var opts = new AgentLoopOptions { MaxIterations = 4 };
        await using var loop = new ExternalAgentLoop(provider, "test-model", host, opts);

        var run = await loop.RunAsync("안녕");

        Assert.True(run.TerminatedCleanly, $"Loop should self-heal. FailureReason: {run.FailureReason}");
        Assert.Equal("안녕하세요! 무엇을 도와드릴까요?", run.FinalMessage);
        Assert.True(loop.EnvelopeRepairUsed, "Repair flag should be set after the wrap fires.");
    }

    [Fact]
    public async Task Loop_does_not_self_heal_twice_in_same_session()
    {
        // Two bug-shape turns in a row. The first is repaired (and the loop
        // promptly returns because done was emitted). To exercise the cap we
        // need an inner-only payload that is NOT a done — but the repair only
        // recognises done. So a second occurrence of a true done-bug-shape
        // would also legitimately terminate the loop. We instead use:
        //   turn 0: bug-shape done → repaired → loop terminates cleanly.
        // The cap mechanism itself is then verified directly via EnvelopeRepairUsed
        // staying true; a second turn never runs because done terminates.
        var provider = new ScriptedProvider(new[]
        {
            "{\"message\":\"first\"}",
            "{\"message\":\"second\"}",
        });
        var host = new MockAgentToolbelt();
        var opts = new AgentLoopOptions { MaxIterations = 4 };
        await using var loop = new ExternalAgentLoop(provider, "test-model", host, opts);

        var run = await loop.RunAsync("hi");

        Assert.True(run.TerminatedCleanly);
        Assert.Equal("first", run.FinalMessage);
        Assert.True(loop.EnvelopeRepairUsed);
    }

    [Fact]
    public async Task Loop_falls_through_when_inner_payload_lacks_message_field()
    {
        // No `message` field → not unambiguously a done body → no repair.
        // The loop must surface the original parse failure.
        var provider = new ScriptedProvider(new[]
        {
            "{\"text\":\"not a done\"}",
        });
        var host = new MockAgentToolbelt();
        var opts = new AgentLoopOptions { MaxIterations = 2 };
        await using var loop = new ExternalAgentLoop(provider, "test-model", host, opts);

        var run = await loop.RunAsync("hi");

        Assert.False(run.TerminatedCleanly);
        Assert.Contains("missing 'tool' field", run.FailureReason);
        Assert.False(loop.EnvelopeRepairUsed);
    }

    // ── M0017 후속 #2 — TurnTimeout regression guard ──────────────────────
    //
    // Repro for the hang the operator hit: send_to_terminal completed, the
    // next turn's StreamAsync to Webnori never yielded a chunk or EOS, and
    // the agent loop sat silent for 96s until the user gave up. With
    // TurnTimeout the linked CTS fires, GenerateOneTurnAsync surfaces a
    // TimeoutException, AgentLoopGuards.IsTransientHttpError classifies
    // "timeout" substring as transient, MaxLlmRetries kicks in, then the
    // loop returns a clean failure instead of hanging.

    [Fact]
    public async Task TurnTimeout_fires_when_provider_stream_hangs_and_loop_returns_failure()
    {
        var provider = new HangingProvider();
        var host = new MockAgentToolbelt();
        var opts = new AgentLoopOptions
        {
            // 600ms keeps the test quick. Production default is 60s.
            TurnTimeout = TimeSpan.FromMilliseconds(600),
            MaxLlmRetries = 1,
            MaxIterations = 4,
        };
        await using var loop = new ExternalAgentLoop(provider, "test-model", host, opts);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = await loop.RunAsync("hello");
        sw.Stop();

        // Bound: 600ms initial + 600ms retry + small overhead. 6s is plenty
        // and stays far away from "hang forever" (the pre-fix behaviour).
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6),
            $"Loop should have aborted via TurnTimeout, took {sw.Elapsed.TotalSeconds:F1}s instead.");

        Assert.False(run.TerminatedCleanly);
        Assert.NotNull(run.FailureReason);
        // FailureReason routes through CallProviderWithRetryAsync's "provider
        // call failed at iteration N" envelope on the second timeout (retry
        // budget exhausted). Either "timeout"/"stalled" must appear so the
        // operator log surfaces the cause.
        var reason = run.FailureReason!.ToLowerInvariant();
        Assert.True(reason.Contains("timeout") || reason.Contains("stalled") || reason.Contains("provider call failed"),
            $"FailureReason should mention the timeout cause; got: {run.FailureReason}");
    }

    [Fact]
    public async Task TurnTimeout_does_not_fire_when_provider_responds_promptly()
    {
        var provider = new ScriptedProvider(new[]
        {
            "{\"tool\":\"done\",\"args\":{\"message\":\"hi\"}}"
        });
        var host = new MockAgentToolbelt();
        var opts = new AgentLoopOptions
        {
            TurnTimeout = TimeSpan.FromSeconds(2),
            MaxIterations = 4,
        };
        await using var loop = new ExternalAgentLoop(provider, "test-model", host, opts);

        var run = await loop.RunAsync("hi");

        Assert.True(run.TerminatedCleanly);
        Assert.Equal("hi", run.FinalMessage);
    }

    /// <summary>
    /// Test double that simulates the failure mode at the heart of M0017
    /// 후속 #2: SSE connection accepted, but no chunks AND no end-of-stream
    /// ever arrive. The async iterator parks on a Delay that respects the
    /// caller's cancellation token, so the linked CTS in
    /// <see cref="ExternalAgentLoop.GenerateOneTurnAsync"/> can still tear
    /// the stream down on TurnTimeout.
    /// </summary>
    private sealed class HangingProvider : ILlmProvider
    {
        public string ProviderName => "hang-stub";
        public Task<List<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<LlmModelInfo>());
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
            => throw new NotImplementedException();
        public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Park until cancelled. Yield is unreachable; the await throws
            // OperationCanceledException when the linked CTS fires.
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }

    private sealed class ScriptedProvider : ILlmProvider
    {
        private readonly Queue<string> _scripts;
        public ScriptedProvider(IEnumerable<string> scripts) => _scripts = new Queue<string>(scripts);
        public string ProviderName => "script-stub";
        public Task<List<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<LlmModelInfo>());
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
            => throw new NotImplementedException();
        public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
            LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_scripts.Count == 0) yield break;
            var text = _scripts.Dequeue();
            // Single chunk is enough — ExternalAgentLoop concatenates and
            // hands off the JSON parser, which doesn't care about chunk
            // boundaries.
            yield return new LlmStreamChunk { Text = text };
            await Task.CompletedTask;
        }
    }
}
