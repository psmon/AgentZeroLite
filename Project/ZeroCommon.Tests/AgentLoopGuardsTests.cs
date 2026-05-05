using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using Agent.Common.Llm.Tools;

namespace ZeroCommon.Tests;

/// <summary>
/// CPU-only unit tests for the shared <see cref="AgentLoopGuards"/> helper.
/// These don't need a model on disk — the guard logic is pure state machine
/// over <see cref="ToolCall"/> values, so we drive it with synthetic calls.
/// The two integration tests at the bottom verify the LLM-side semantics
/// (block message format, hard-stop reason) without binding to a real model.
/// </summary>
public sealed class AgentLoopGuardsTests
{
    // ─────────────────────────────────────────────────────────────────────
    // Repeat detection
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CheckRepeat_allows_calls_under_the_cap()
    {
        var guards = new AgentLoopGuards();
        var call = MakeCall("read_terminal", "{\"group\":0,\"tab\":0}");

        Assert.Null(guards.CheckRepeat(call, maxSameCallRepeats: 3));
        Assert.Null(guards.CheckRepeat(call, maxSameCallRepeats: 3));
        Assert.Null(guards.CheckRepeat(call, maxSameCallRepeats: 3));
        Assert.Equal(0, guards.BlockedRepeats);
    }

    [Fact]
    public void CheckRepeat_blocks_on_overflow_and_message_includes_call_signature()
    {
        var guards = new AgentLoopGuards();
        var call = MakeCall("read_terminal", "{\"group\":0,\"tab\":0}");

        for (var i = 0; i < 3; i++)
            Assert.Null(guards.CheckRepeat(call, 3));

        var blocked = guards.CheckRepeat(call, 3);
        Assert.NotNull(blocked);
        Assert.Contains("read_terminal", blocked);
        Assert.Contains("4 times", blocked);
        Assert.Contains("DO NOT repeat", blocked);
        Assert.Equal(1, guards.BlockedRepeats);
    }

    [Fact]
    public void CheckRepeat_with_different_args_does_not_count_against_each_other()
    {
        var guards = new AgentLoopGuards();
        var a = MakeCall("read_terminal", "{\"group\":0,\"tab\":0}");
        var b = MakeCall("read_terminal", "{\"group\":1,\"tab\":2}");

        for (var i = 0; i < 3; i++) Assert.Null(guards.CheckRepeat(a, 3));
        for (var i = 0; i < 3; i++) Assert.Null(guards.CheckRepeat(b, 3));

        Assert.Equal(0, guards.BlockedRepeats);
    }

    [Fact]
    public void CheckRepeat_normalizes_arg_key_order()
    {
        // Origin's known weakness: {"a":1,"b":2} ≠ {"b":2,"a":1} as raw strings.
        // Lite normalization (sorted keys) closes this loophole — the model
        // can't bypass the guard by reordering args.
        var guards = new AgentLoopGuards();
        var first = MakeCall("send_to_terminal",
            "{\"group\":0,\"tab\":0,\"text\":\"hi\"}");
        var reordered = MakeCall("send_to_terminal",
            "{\"text\":\"hi\",\"tab\":0,\"group\":0}");

        // Mixed-order calls share the canonical key, so they cumulatively
        // count toward the cap. Cap=3 means calls 1-3 pass, call 4 blocks.
        Assert.Null(guards.CheckRepeat(first, 3));      // count=1
        Assert.Null(guards.CheckRepeat(reordered, 3));  // count=2 (same key)
        Assert.Null(guards.CheckRepeat(first, 3));      // count=3
        var blocked = guards.CheckRepeat(reordered, 3); // count=4 → blocked

        Assert.NotNull(blocked);
        Assert.Equal(1, guards.BlockedRepeats);
    }

    [Fact]
    public void NormalizeArgsJson_produces_stable_canonical_form()
    {
        var a = JsonNode.Parse("{\"b\":2,\"a\":1}")!.AsObject();
        var b = JsonNode.Parse("{\"a\":1,\"b\":2}")!.AsObject();

        Assert.Equal(AgentLoopGuards.NormalizeArgsJson(a),
                     AgentLoopGuards.NormalizeArgsJson(b));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Consecutive-block hard stop
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShouldHardStop_fires_only_after_uninterrupted_block_streak()
    {
        var guards = new AgentLoopGuards();
        var spam = MakeCall("read_terminal", "{\"group\":0,\"tab\":0}");

        // Prime: 3 allowed + 1 blocked = streak 1
        for (var i = 0; i < 3; i++) guards.CheckRepeat(spam, 3);
        Assert.NotNull(guards.CheckRepeat(spam, 3));
        Assert.False(guards.ShouldHardStop(maxConsecutiveBlocks: 3));

        // Two more blocks → streak 3 → hard stop fires
        Assert.NotNull(guards.CheckRepeat(spam, 3));
        Assert.NotNull(guards.CheckRepeat(spam, 3));
        Assert.True(guards.ShouldHardStop(3));
        Assert.Equal(3, guards.ConsecutiveBlocks);
    }

    [Fact]
    public void Different_call_in_between_resets_consecutive_streak()
    {
        var guards = new AgentLoopGuards();
        var spam = MakeCall("read_terminal", "{\"group\":0,\"tab\":0}");
        var other = MakeCall("list_terminals", "{}");

        // Push spam to blocked state
        for (var i = 0; i < 3; i++) guards.CheckRepeat(spam, 3);
        Assert.NotNull(guards.CheckRepeat(spam, 3));   // block #1
        Assert.NotNull(guards.CheckRepeat(spam, 3));   // block #2

        // A diversifying call — non-blocked — clears the streak
        Assert.Null(guards.CheckRepeat(other, 3));
        Assert.Equal(0, guards.ConsecutiveBlocks);
        Assert.False(guards.ShouldHardStop(3));
    }

    // ─────────────────────────────────────────────────────────────────────
    // LLM retry budget + backoff
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryConsumeLlmRetry_caps_at_budget()
    {
        var guards = new AgentLoopGuards();
        Assert.True(guards.TryConsumeLlmRetry(2));
        Assert.True(guards.TryConsumeLlmRetry(2));
        Assert.False(guards.TryConsumeLlmRetry(2));
        Assert.Equal(2, guards.LlmRetries);
    }

    [Fact]
    public void CurrentBackoff_grows_linearly_per_consumed_retry()
    {
        var guards = new AgentLoopGuards();
        Assert.Equal(TimeSpan.Zero, guards.CurrentBackoff());

        guards.TryConsumeLlmRetry(5);
        Assert.Equal(TimeSpan.FromSeconds(1), guards.CurrentBackoff());

        guards.TryConsumeLlmRetry(5);
        Assert.Equal(TimeSpan.FromSeconds(3), guards.CurrentBackoff());

        guards.TryConsumeLlmRetry(5);
        Assert.Equal(TimeSpan.FromSeconds(5), guards.CurrentBackoff());
    }

    // ─────────────────────────────────────────────────────────────────────
    // Transient HTTP classification
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void IsTransientHttpError_typed_status_codes(HttpStatusCode code)
    {
        var ex = new HttpRequestException("upstream blip", inner: null, statusCode: code);
        Assert.True(AgentLoopGuards.IsTransientHttpError(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void IsTransientHttpError_rejects_permanent_status_codes(HttpStatusCode code)
    {
        var ex = new HttpRequestException("nope", inner: null, statusCode: code);
        Assert.False(AgentLoopGuards.IsTransientHttpError(ex));
    }

    [Theory]
    [InlineData("request timed out")]
    [InlineData("Connection reset by peer")]
    [InlineData("actively refused")]
    [InlineData("Gateway Time-out")]
    public void IsTransientHttpError_message_keyword_fallback(string message)
    {
        Assert.True(AgentLoopGuards.IsTransientHttpError(new InvalidOperationException(message)));
    }

    [Fact]
    public void IsTransientHttpError_rejects_unrelated_messages()
    {
        Assert.False(AgentLoopGuards.IsTransientHttpError(
            new InvalidOperationException("malformed prompt")));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Recent-attempts feedback (Lite refinement over Origin)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Block_message_includes_recent_attempt_summary_for_self_correction()
    {
        var guards = new AgentLoopGuards();
        var read = MakeCall("read_terminal", "{\"group\":0,\"tab\":0}");
        var list = MakeCall("list_terminals", "{}");

        // Build up a recent-attempts log
        guards.CheckRepeat(list, 3);
        guards.RecordResult(list, "{\"groups\":[]}");
        guards.CheckRepeat(read, 3);
        guards.RecordResult(read, "{\"ok\":true,\"text\":\"$ \"}");

        // Drive read past the cap
        for (var i = 0; i < 2; i++)
        {
            guards.CheckRepeat(read, 3);
            guards.RecordResult(read, "{\"ok\":true,\"text\":\"$ \"}");
        }
        var blocked = guards.CheckRepeat(read, 3);

        Assert.NotNull(blocked);
        Assert.Contains("Recent attempts", blocked);
        Assert.Contains("list_terminals", blocked);
        Assert.Contains("read_terminal", blocked);
    }

    [Fact]
    public void Snapshot_reflects_session_activity_for_telemetry()
    {
        var guards = new AgentLoopGuards();
        var spam = MakeCall("read_terminal", "{}");
        for (var i = 0; i < 5; i++) guards.CheckRepeat(spam, 3);

        guards.TryConsumeLlmRetry(5);
        guards.TryConsumeLlmRetry(5);

        var stats = guards.Snapshot();
        Assert.Equal(2, stats.BlockedRepeats);   // calls #4 and #5
        Assert.Equal(2, stats.LlmRetries);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static ToolCall MakeCall(string tool, string argsJson)
        => new(tool, JsonNode.Parse(argsJson)!.AsObject());
}
