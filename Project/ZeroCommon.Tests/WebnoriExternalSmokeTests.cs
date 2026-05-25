using Agent.Common.Llm.Providers;
using Agent.Common.Llm.Tools;
using Xunit.Abstractions;

namespace ZeroCommon.Tests;

/// <summary>
/// Online smoke tests for the Webnori bundled test-key provider. These are
/// the canonical "does the wire still work" tests — much cheaper than spinning
/// up a 5 GB local Gemma 4. Maintainer's dev box can't run the local model
/// reliably, so this is the default LLM-touching path for CI.
///
/// <para><b>Skipped by default</b> via <c>WEBNORI_SMOKE</c> env var so
/// builds without internet stay green. Run locally with:</para>
/// <code>
/// $env:WEBNORI_SMOKE="1"; dotnet test Project/ZeroCommon.Tests --filter "Category=Online"
/// </code>
/// </summary>
[Trait("Category", "Online")]
[Trait("Provider", "Webnori")]
public sealed class WebnoriExternalSmokeTests
{
    private readonly ITestOutputHelper _output;

    public WebnoriExternalSmokeTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("WEBNORI_SMOKE") == "1";

    [Fact]
    public async Task Hello_completion_round_trips_under_30s()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set WEBNORI_SMOKE=1 to enable.");
            return;
        }

        using var provider = (OpenAiCompatibleProvider)LlmProviderFactory.CreateWebnori(
            timeout: TimeSpan.FromSeconds(30));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var resp = await provider.CompleteAsync(new LlmRequest
        {
            Model = WebnoriDefaults.DefaultModel,
            Messages = [LlmMessage.User("Reply with exactly one word: hello")],
            Temperature = 0.0f,
            MaxTokens = 16,
        }, cts.Token);
        sw.Stop();

        _output.WriteLine($"Webnori responded in {sw.ElapsedMilliseconds}ms: \"{resp.Text}\"");
        Assert.False(string.IsNullOrWhiteSpace(resp.Text), $"Empty response (finish={resp.FinishReason})");
        Assert.True(sw.ElapsedMilliseconds < 30_000, "Webnori should respond within the 30s budget");
    }

    /// <summary>
    /// Regression: AgentBot UI test on Webnori + google/gemma-4-e4b returned a
    /// `{"message":"안녕하세요! ..."}` payload at iteration 0 — the inner args of
    /// `done` with the outer `{"tool":"done","args":...}` envelope dropped.
    /// Root cause hypothesis: the `done({"message": "..."})` pseudo-function-call
    /// examples in <see cref="AgentToolGrammar.SystemPrompt"/> are copied
    /// literally by Gemma when there is no GBNF to enforce the envelope
    /// (REST/External path).
    ///
    /// Today this test should FAIL with FailureReason containing
    /// "missing 'tool' field". After the prompt-example fix (or the
    /// self-healing fallback) it should pass with TerminatedCleanly=true and
    /// FinalMessage non-empty.
    /// </summary>
    [Fact]
    public async Task Greeting_drives_clean_done_envelope_against_real_Gemma4()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set WEBNORI_SMOKE=1 to enable.");
            return;
        }

        using var provider = (OpenAiCompatibleProvider)LlmProviderFactory.CreateWebnori(
            timeout: TimeSpan.FromSeconds(60));
        var host = new MockAgentToolbelt();
        var opts = new AgentLoopOptions
        {
            MaxIterations = 2,           // Mode 1 should call done on iter 0; cap low so a bug surfaces fast.
            MaxTokensPerTurn = 512,
            Temperature = 0.0f,
            TurnTimeout = TimeSpan.FromSeconds(45),
        };
        await using var loop = new ExternalAgentLoop(
            provider, WebnoriDefaults.DefaultModel, host, opts);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = await loop.RunAsync("안녕");
        sw.Stop();

        _output.WriteLine($"Webnori responded in {sw.ElapsedMilliseconds}ms");
        _output.WriteLine($"TerminatedCleanly = {run.TerminatedCleanly}");
        _output.WriteLine($"FailureReason   = {run.FailureReason ?? "(none)"}");
        _output.WriteLine($"FinalMessage    = {run.FinalMessage}");
        _output.WriteLine($"TurnCount       = {run.TurnCount}");
        foreach (var t in run.Turns)
            _output.WriteLine($"  turn: tool={t.Call.Tool} args={t.Call.Args.ToJsonString()} result={t.ToolResult}");

        // The bug. Once fixed this branch goes away and only the
        // TerminatedCleanly assertion remains. Two breadcrumbs are kept so the
        // test message itself tells the next maintainer what shape of failure
        // it is recording.
        if (!run.TerminatedCleanly && run.FailureReason is { } fr)
        {
            Assert.Contains("missing 'tool' field", fr);
            // Confirm the smoking-gun string from AgentToolGrammar.cs:198
            // actually surfaced in the raw payload. If it didn't, the failure
            // mode shifted and this regression test no longer represents the
            // original bug — investigate before adjusting.
            Assert.Contains("\"message\"", fr);
        }

        // Post-fix expectation. Today this fails (proving the bug); after the
        // prompt-example normalisation or the self-healing fallback lands, it
        // should pass and the if-block above becomes dead.
        Assert.True(run.TerminatedCleanly,
            $"Loop did not terminate cleanly. FailureReason: {run.FailureReason}");
        Assert.False(string.IsNullOrWhiteSpace(run.FinalMessage),
            "Mode-1 done must carry a non-empty greeting back to the user.");
    }

    [Fact]
    public async Task ListModels_returns_at_least_one_entry()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set WEBNORI_SMOKE=1 to enable.");
            return;
        }

        using var provider = (OpenAiCompatibleProvider)LlmProviderFactory.CreateWebnori(
            timeout: TimeSpan.FromSeconds(15));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var models = await provider.ListModelsAsync(cts.Token);
        _output.WriteLine($"Webnori listed {models.Count} model(s): {string.Join(", ", models.Select(m => m.Id))}");
        Assert.NotEmpty(models);
    }
}
