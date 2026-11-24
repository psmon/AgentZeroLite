// ───────────────────────────────────────────────────────────
// Two terminal agents talking through AgentBot — the pieces that failed on
// 2026-11-24 (Codex × Hermes in one workspace, conversation never started):
//
//   1. the Enter that followed a long message was read as a line break
//      (TerminalSubmit),
//   2. a peer's bot-chat reply arriving mid-run was ignored (PeerSignalInbox +
//      AgentBotActor),
//   3. "DONE(Codex, …) --from Hermes" was read as a message FROM Codex
//      (PeerEnvelope),
//   4. the 4th identical wait of a relay was blocked as a repeat
//      (AgentLoopGuards).
// ───────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.Json.Nodes;
using Akka.Actor;
using Akka.TestKit.Xunit2;
using Agent.Common.Actors;
using Agent.Common.Llm.Tools;
using Agent.Common.Services;

namespace ZeroCommon.Tests;

public sealed class PeerEnvelopeTests
{
    [Fact]
    public void Explicit_from_is_the_sender_even_when_the_text_opens_with_a_name()
    {
        // Hermes answering Codex — the measured message, shortened.
        var p = PeerEnvelope.Parse("Hermes", "DONE(Codex, 가장 현실적인 첫걸음은 신뢰 회복입니다.)");

        Assert.Equal("Hermes", p.Peer);
        Assert.Equal("Codex, 가장 현실적인 첫걸음은 신뢰 회복입니다.", p.Text);
        Assert.True(p.IsDone);
    }

    [Fact]
    public void A_comma_in_the_reply_does_not_invent_a_sender()
    {
        var p = PeerEnvelope.Parse("Codex", "DONE(네, 맞아요)");

        Assert.Equal("Codex", p.Peer);
        Assert.Equal("네, 맞아요", p.Text);
    }

    [Fact]
    public void Without_from_the_legacy_envelope_still_names_the_sender()
    {
        var p = PeerEnvelope.Parse("CLI", "DONE(Claude, build passed)");

        Assert.Equal("Claude", p.Peer);
        Assert.Equal("build passed", p.Text);
        Assert.True(p.IsDone);
    }

    [Fact]
    public void Without_from_a_sentence_before_the_comma_is_not_a_name()
    {
        var p = PeerEnvelope.Parse(null, "DONE(I agree with that, and here is why)");

        Assert.Equal(PeerEnvelope.DefaultFrom, p.Peer);
        Assert.Equal("I agree with that, and here is why", p.Text);
    }

    [Fact]
    public void Handshake_ack_and_plain_text_keep_their_sender()
    {
        var ack = PeerEnvelope.Parse("Hermes", "DONE(handshake-ok)");
        Assert.Equal(("Hermes", "handshake-ok", true), (ack.Peer, ack.Text, ack.IsDone));

        var plain = PeerEnvelope.Parse("Hermes", "still working on it");
        Assert.Equal(("Hermes", "still working on it", false), (plain.Peer, plain.Text, plain.IsDone));
    }
}

public sealed class PeerHandshakeTests
{
    [Fact]
    public void The_handshake_names_this_windows_own_exe_in_every_command()
    {
        const string exe = @"C:\code\app\bin\Debug\net10.0\AgentZeroLite.exe";

        var text = PeerHandshake.BuildFirstContactHeader("Codex", exe, windows: true);

        // A bare name is resolved by whoever runs it — Codex's daemon, not the tab.
        Assert.DoesNotContain("      AgentZeroLite.exe -cli", text);
        Assert.Contains($"PowerShell:  & \"{exe}\" -cli bot-chat \"DONE(handshake-ok)\" --from Codex", text);
        Assert.Contains($"cmd / bash:  \"{exe}\" -cli bot-chat \"DONE(handshake-ok)\" --from Codex", text);
        Assert.Contains("ONLY your reply text inside DONE(...)", text);
    }

    [Fact]
    public void A_posix_host_gets_one_form_and_an_unknown_path_the_bare_name()
    {
        var posix = PeerHandshake.BuildFirstContactHeader("Claude", "/Applications/AgentZeroLite.app/Contents/MacOS/AgentZeroLite", windows: false);
        Assert.Contains("      \"/Applications/AgentZeroLite.app/Contents/MacOS/AgentZeroLite\" -cli help", posix);
        Assert.DoesNotContain("PowerShell:", posix);

        var unknown = PeerHandshake.BuildFirstContactHeader("Claude", null);
        Assert.Contains("      AgentZeroLite.exe -cli bot-chat \"DONE(handshake-ok)\" --from Claude", unknown);
    }
}

public sealed class PeerCliFaultTests
{
    [Fact]
    public void A_screen_showing_the_sandbox_failure_gets_a_note_with_the_recorded_cause()
    {
        var home = Directory.CreateTempSubdirectory("codex-home-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".sandbox"));
            File.WriteAllText(Path.Combine(home, ".sandbox", "setup_error.json"),
                """{ "code": "helper_sandbox_lock_failed", "message": "lock sandbox bin dir failed: 5" }""");

            // As Codex draws it: the line is cut at the pane's edge.
            var note = Agent.Common.Agents.PeerCliFault.NoteFor(
                "• Exited (status -1) AgentZeroLite.exe -cli bot-ch…\n  └ Failed to create unified exec process: helper_…", home);

            Assert.NotNull(note);
            Assert.Contains("helper_sandbox_lock_failed: lock sandbox bin dir failed: 5", note);
            Assert.Contains("CANNOT call bot-chat", note);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void An_ordinary_screen_gets_no_note_and_an_old_error_file_is_not_quoted()
    {
        var home = Directory.CreateTempSubdirectory("codex-home-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".sandbox"));
            var file = Path.Combine(home, ".sandbox", "setup_error.json");
            File.WriteAllText(file, """{ "code": "old", "message": "fixed long ago" }""");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));

            Assert.Null(Agent.Common.Agents.PeerCliFault.NoteFor("• 안녕하세요! 세계 평화는 …", home));
            Assert.Null(Agent.Common.Agents.PeerCliFault.RecentCodexSetupError(home));
            var note = Agent.Common.Agents.PeerCliFault.NoteFor("Failed to create unified exec process", home);
            Assert.NotNull(note);
            Assert.DoesNotContain("fixed long ago", note);
        }
        finally { Directory.Delete(home, true); }
    }
}

public sealed class TerminalSubmitTests
{
    /// <summary>A terminal whose output grows at scripted moments; time moves only when the helper sleeps.</summary>
    private sealed class FakeTerminal
    {
        private readonly List<(long At, string Text)> _script = new();
        private readonly System.Text.StringBuilder _out = new();
        private long _now;
        public long EnterAt = -1;

        public void Emit(long at, string text) => _script.Add((at, text));

        public Task Delay(int ms)
        {
            _now += ms;
            foreach (var (at, text) in _script.Where(s => s.At <= _now).ToList())
            {
                _out.Append(text);
                _script.Remove((at, text));
            }
            return Task.CompletedTask;
        }

        public Task<TerminalSubmit.Outcome> Submit(int textLength)
            => TerminalSubmit.EnterWhenSettledAsync(
                () => _out.Length, () => EnterAt = _now, textLength,
                (start, length) => _out.ToString(start, length), Delay, () => _now);
    }

    private const string EmptyFrame = "\x1b[?2026h\x1b[?2026l";

    [Fact]
    public async Task A_long_paste_gets_its_enter_only_after_the_program_drew_it()
    {
        // Codex, 1,356 characters: empty frames every 100 ms, the composer drawn at ~1 s.
        var term = new FakeTerminal();
        for (var t = 100; t <= 6000; t += 100) term.Emit(t, EmptyFrame);
        term.Emit(2400, "\x1b[2K› [Pasted Content 1356 chars]");

        var o = await term.Submit(1356);

        Assert.True(o.Settled);
        Assert.True(o.SawEcho);
        Assert.True(term.EnterAt >= 2400 + TerminalSubmit.QuietMs, $"Enter at {term.EnterAt} ms");
    }

    [Fact]
    public async Task Empty_frames_alone_do_not_hold_a_short_command_back()
    {
        var term = new FakeTerminal();
        term.Emit(40, "codex");
        for (var t = 100; t <= 6000; t += 100) term.Emit(t, EmptyFrame);

        var o = await term.Submit(5);

        Assert.True(o.Settled);
        Assert.InRange(term.EnterAt, TerminalSubmit.QuietMs, 500);
    }

    [Fact]
    public async Task A_prompt_that_echoes_nothing_is_not_kept_waiting()
    {
        var term = new FakeTerminal();   // a password prompt: no output at all

        var o = await term.Submit(20);

        Assert.True(o.Settled);
        Assert.False(o.SawEcho);
        Assert.InRange(term.EnterAt, TerminalSubmit.QuietMs, 400);
    }

    [Fact]
    public async Task A_screen_that_never_goes_quiet_gets_the_enter_at_the_cap()
    {
        var term = new FakeTerminal();
        for (var t = 25; t <= 20000; t += 50) term.Emit(t, "⠋ working");

        var o = await term.Submit(300);

        Assert.False(o.Settled);
        Assert.InRange(term.EnterAt, TerminalSubmit.MaxWaitMs(300), TerminalSubmit.MaxWaitMs(300) + 100);
    }

    [Theory]
    [InlineData("\x1b[?2026h\x1b[?2026l", false)]
    [InlineData("\x1b[2J\x1b[H  \r\n", false)]
    [InlineData("\x1b]0;⠋ zero-test\a", false)]          // a title change draws nothing in the grid
    [InlineData("\x1b]0;title\x1b\\", false)]
    [InlineData("\x1b[32mok\x1b[0m", true)]
    [InlineData("안", true)]
    public void DrawsSomething_tells_text_from_control_sequences(string output, bool expected)
        => Assert.Equal(expected, TerminalSubmit.DrawsSomething(output));
}

public sealed class PeerSignalInboxTests
{
    [Fact]
    public async Task Wait_returns_as_soon_as_a_peer_speaks()
    {
        var inbox = new PeerSignalInbox();
        var waiting = inbox.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        inbox.Post(new PeerSignal("Hermes", "handshake-ok"));

        var got = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new PeerSignal("Hermes", "handshake-ok"), Assert.Single(got));
        Assert.Equal(0, inbox.Count);
    }

    [Fact]
    public async Task What_arrived_before_the_wait_is_returned_at_once_and_in_order()
    {
        var inbox = new PeerSignalInbox();
        inbox.Post(new PeerSignal("Hermes", "one"));
        inbox.Post(new PeerSignal("Codex", "two"));

        var got = await inbox.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(["one", "two"], got.Select(s => s.Text));
    }

    [Fact]
    public async Task Wait_tool_reports_the_reply_and_stays_a_plain_sleep_without_a_peer_channel()
    {
        var inbox = new PeerSignalInbox();
        inbox.Post(new PeerSignal("Hermes", "평화는 신뢰에서 시작됩니다"));
        var withChannel = new AgentLoopOptions { WaitForPeerSignals = inbox.WaitAsync };

        var json = await PeerSignalInbox.WaitToolAsync(withChannel, 20, CancellationToken.None);

        Assert.Contains("평화는 신뢰에서 시작됩니다", json);   // readable, not \uXXXX
        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("Hermes", root.GetProperty("peer_signals")[0].GetProperty("from").GetString());

        var quiet = await PeerSignalInbox.WaitToolAsync(new AgentLoopOptions(), 1, CancellationToken.None);
        Assert.False(JsonDocument.Parse(quiet).RootElement.TryGetProperty("peer_signals", out _));
    }

    [Fact]
    public async Task A_cancelled_wait_throws_instead_of_reporting_silence()
    {
        var inbox = new PeerSignalInbox();
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inbox.WaitAsync(TimeSpan.FromSeconds(30), cts.Token));
    }
}

public sealed class AgentLoopGuardsRelayTests
{
    private static ToolCall Call(string tool, string args) => new(tool, (JsonObject)JsonNode.Parse(args)!);

    [Fact]
    public void Waiting_again_after_a_new_message_was_sent_is_not_a_repeat()
    {
        var guards = new AgentLoopGuards();
        var wait = Call("wait", """{"seconds":20}""");
        var send = Call("send_to_terminal", """{"group":1,"tab":0,"text":"next"}""");

        // Six relays, one wait each — every wait is for a different reply.
        for (var round = 0; round < 6; round++)
        {
            Assert.Null(guards.CheckRepeat(send with { Args = (JsonObject)JsonNode.Parse($$"""{"group":1,"tab":0,"text":"m{{round}}"}""")! }, 3));
            guards.RecordResult(send, """{"ok":true}""");
            Assert.Null(guards.CheckRepeat(wait, 3));
            guards.RecordResult(wait, """{"ok":true,"waited_seconds":20}""");
        }
    }

    [Fact]
    public void Waiting_over_and_over_with_nothing_sent_is_still_caught()
    {
        var guards = new AgentLoopGuards();
        var wait = Call("wait", """{"seconds":20}""");

        for (var i = 0; i < 3; i++) Assert.Null(guards.CheckRepeat(wait, 3));
        Assert.NotNull(guards.CheckRepeat(wait, 3));
    }

    [Fact]
    public void A_send_that_failed_does_not_reset_the_count()
    {
        var guards = new AgentLoopGuards();
        var wait = Call("wait", """{"seconds":20}""");
        for (var i = 0; i < 3; i++) Assert.Null(guards.CheckRepeat(wait, 3));

        guards.RecordResult(Call("send_to_terminal", """{"group":9,"tab":9,"text":"x"}"""), """{"ok":false}""");

        Assert.NotNull(guards.CheckRepeat(wait, 3));
    }
}

/// <summary>What AgentBotActor does with a peer reply that arrives while a run is in flight.</summary>
public sealed class AgentBotPeerSignalDuringRunTests : TestKit
{
    private IActorRef NewBot() => Sys.ActorOf(Props.Create(() => new AgentBotActor(CreateTestProbe("stage").Ref)), "bot");

    /// <summary>A loop whose every run is scripted by the test; it records the request it was started with.</summary>
    private sealed class ScriptedLoop(AgentLoopOptions options, Func<AgentLoopOptions, string, CancellationToken, Task<string>> run) : IAgentLoop
    {
        public int UserSendCount { get; private set; }

        public async Task<AgentLoopRun> RunAsync(string userRequest, CancellationToken ct = default)
        {
            UserSendCount++;
            return new([], await run(options, userRequest, ct), TerminatedCleanly: true, FailureReason: null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static AgentLoopBindings Bindings(Func<AgentLoopOptions, string, CancellationToken, Task<string>> run) => new(
        ToolbeltFactory: () => new NoTerminals(),
        OptionsFactory: () => new AgentLoopOptions(),
        AgentLoopFactory: (options, _) => new ScriptedLoop(options, run));

    private sealed class NoTerminals : IAgentToolbelt
    {
        public Task<string> ListTerminalsAsync(CancellationToken ct = default) => Task.FromResult("[]");
        public Task<string> ReadTerminalAsync(int g, int t, int n, CancellationToken ct = default) => Task.FromResult("");
        public Task<bool> SendToTerminalAsync(int g, int t, string text, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendKeyAsync(int g, int t, string key, CancellationToken ct = default) => Task.FromResult(true);
    }

    [Fact]
    public void A_reply_that_arrives_mid_run_ends_the_runs_wait()
    {
        var bot = NewBot();
        var results = CreateTestProbe("results");
        var waiting = new TaskCompletionSource();
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => results.Ref.Tell(r)));
        bot.Tell(Bindings(async (options, _, ct) =>
        {
            // The run is at its `wait` tool, as it was when Hermes answered.
            waiting.TrySetResult();
            var heard = await options.WaitForPeerSignals!(TimeSpan.FromSeconds(30), ct);
            return string.Join("|", heard.Select(s => $"{s.PeerName}:{s.Text}"));
        }));
        bot.Tell(new MarkConversationActive("Hermes"));

        bot.Tell(new StartAgentLoop("greet Hermes"));
        Assert.True(waiting.Task.Wait(TimeSpan.FromSeconds(5)));
        bot.Tell(new TerminalSentToBot("Hermes", "handshake-ok"));

        var result = results.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(5));
        Assert.Equal("Hermes:handshake-ok", result.FinalMessage);
        // The run took it — it must not also start a cycle of its own.
        results.ExpectNoMsg(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public void A_reply_the_run_never_looked_at_starts_the_next_cycle()
    {
        var bot = NewBot();
        var results = CreateTestProbe("results");
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var runs = 0;
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => results.Ref.Tell(r)));
        bot.Tell(Bindings(async (_, request, _) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                started.TrySetResult();
                await release.Task;      // busy with something else — never waits for peers
                return "first run";
            }
            return request;              // the continuation echoes what it was started with
        }));
        bot.Tell(new MarkConversationActive("Hermes"));
        bot.Tell(new MarkConversationActive("Codex"));

        bot.Tell(new StartAgentLoop("relay"));
        Assert.True(started.Task.Wait(TimeSpan.FromSeconds(5)));
        bot.Tell(new TerminalSentToBot("Hermes", "첫 번째 답"));
        bot.Tell(new TerminalSentToBot("Codex", "second answer"));
        results.ExpectNoMsg(TimeSpan.FromMilliseconds(300));   // nothing is lost, nothing runs twice
        release.SetResult();

        Assert.Equal("first run", results.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(5)).FinalMessage);
        var next = results.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(5)).FinalMessage;
        Assert.Equal(
            "[peer signal from Hermes via bot-chat] 첫 번째 답\n[peer signal from Codex via bot-chat] second answer",
            next);
        results.ExpectNoMsg(TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public void A_reply_from_a_peer_nobody_is_talking_to_is_still_dropped()
    {
        var bot = NewBot();
        var results = CreateTestProbe("results");
        var release = new TaskCompletionSource();
        bot.Tell(new SetAgentLoopCallbacks(_ => { }, r => results.Ref.Tell(r)));
        bot.Tell(Bindings(async (_, _, _) => { await release.Task; return "done"; }));

        bot.Tell(new StartAgentLoop("something else"));
        bot.Tell(new TerminalSentToBot("Stranger", "hello?"));
        release.SetResult();

        Assert.Equal("done", results.ExpectMsg<AgentLoopResult>(TimeSpan.FromSeconds(5)).FinalMessage);
        results.ExpectNoMsg(TimeSpan.FromMilliseconds(400));
    }
}
