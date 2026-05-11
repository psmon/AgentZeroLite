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
