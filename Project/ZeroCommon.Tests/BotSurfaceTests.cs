using Agent.Common.Agents;
using Agent.Common.Services;
using Xunit;
using ZeroCommon.Tests.Remote;

namespace ZeroCommon.Tests;

/// <summary>
/// M0041 — the AgentBot surface rules both GUI hosts share. Everything here is pure or
/// drives a <see cref="FakeTerminalSession"/>; time is injected so nothing waits.
/// </summary>
public sealed class BotOptionsTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData("30", 30)]
    public void Accepts_delays_inside_the_range(string text, int expected)
        => Assert.Equal(expected, BotOptions.TryParseDelay(text));

    [Theory]
    [InlineData("-1")]
    [InlineData("31")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_everything_else(string? text)
        => Assert.Null(BotOptions.TryParseDelay(text));

    [Fact]
    public void System_messages_are_hidden_by_default()
        => Assert.True(new BotOptions().HideSystemMessages);
}

public sealed class ApprovalAutoResponderTests
{
    private static Func<TimeSpan, CancellationToken, Task> NoDelay(List<int> recorded)
        => (t, _) => { recorded.Add((int)t.TotalMilliseconds); return Task.CompletedTask; };

    [Fact]
    public void Option_zero_is_a_bare_enter()
    {
        var strokes = ApprovalAutoResponder.Keystrokes(0);
        Assert.Equal(TerminalControl.Enter, Assert.Single(strokes).Control);
    }

    [Fact]
    public void Option_two_is_two_downs_then_enter()
    {
        var strokes = ApprovalAutoResponder.Keystrokes(2);
        Assert.Equal(
            new[] { TerminalControl.DownArrow, TerminalControl.DownArrow, TerminalControl.Enter },
            strokes.Select(s => s.Control));
        Assert.Equal(ApprovalAutoResponder.ArrowDelayMs, strokes[0].DelayMsAfter);
    }

    [Fact]
    public async Task Sends_arrows_then_enter_with_the_settle_beat()
    {
        var session = new FakeTerminalSession();
        var delays = new List<int>();

        await ApprovalAutoResponder.SendAsync(session, optionIndex: 2, NoDelay(delays));

        Assert.Equal(
            new[] { TerminalControl.DownArrow, TerminalControl.DownArrow, TerminalControl.Enter },
            session.Controls);
        // 150 after each arrow, then the 100ms settle before Enter.
        Assert.Equal(new[] { 150, 150, 100 }, delays);
    }

    [Fact]
    public async Task Switching_auto_approve_off_during_the_wait_cancels_the_answer()
    {
        var session = new FakeTerminalSession();

        await ApprovalAutoResponder.SendAsync(
            session, optionIndex: 0, (_, _) => Task.CompletedTask, stillWanted: () => false);

        Assert.Empty(session.Controls);
    }
}

public sealed class UrlNoticeThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_same_url_is_announced_once_inside_the_cooldown()
    {
        var throttle = new UrlNoticeThrottle();
        Assert.True(throttle.ShouldShow("https://example.com", T0));
        Assert.False(throttle.ShouldShow("https://example.com", T0.AddSeconds(29)));
    }

    [Fact]
    public void After_the_cooldown_it_is_announced_again()
    {
        var throttle = new UrlNoticeThrottle();
        Assert.True(throttle.ShouldShow("https://example.com", T0));
        Assert.True(throttle.ShouldShow("https://example.com", T0.AddSeconds(31)));
    }

    [Fact]
    public void Matching_ignores_case()
    {
        var throttle = new UrlNoticeThrottle();
        Assert.True(throttle.ShouldShow("https://Example.com/A", T0));
        Assert.False(throttle.ShouldShow("https://example.com/a", T0.AddSeconds(1)));
    }

    [Fact]
    public void Expired_entries_are_evicted_once_the_table_grows()
    {
        var throttle = new UrlNoticeThrottle();
        for (var i = 0; i <= UrlNoticeThrottle.EvictAbove; i++)
            throttle.ShouldShow($"https://example.com/{i}", T0);

        // Everything above is now expired; the next show sweeps them out.
        throttle.ShouldShow("https://example.com/fresh", T0.AddSeconds(31));

        Assert.Equal(1, throttle.TrackedCount);
    }
}

public sealed class TerminalTextSenderTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(201, true)]
    public void Chunking_kicks_in_just_past_the_threshold(int length, bool chunked)
        => Assert.Equal(chunked, TerminalTextSender.NeedsChunking(new string('x', length)));

    [Fact]
    public async Task Short_text_goes_through_the_synchronous_write()
    {
        var session = new FakeTerminalSession();
        await TerminalTextSender.SendAsync(session, "dir", (_, _) => Task.CompletedTask);

        Assert.Equal("dir", Assert.Single(session.Writes));
        Assert.Empty(session.Controls);
    }

    [Fact]
    public async Task Multi_line_text_gets_the_extra_enter()
    {
        var session = new FakeTerminalSession();
        await TerminalTextSender.SendAsync(session, "one\ntwo", (_, _) => Task.CompletedTask);

        Assert.Equal(TerminalControl.Enter, Assert.Single(session.Controls));
    }

    [Fact]
    public async Task Large_text_uses_the_async_queue()
    {
        var session = new FakeTerminalSession();
        var big = new string('x', TerminalTextSender.ChunkThreshold + 1);

        await TerminalTextSender.SendAsync(session, big, (_, _) => Task.CompletedTask);

        Assert.Equal(big, Assert.Single(session.Writes));
        Assert.Empty(session.Controls);   // WriteAsync appends its own \r
    }

    [Fact]
    public async Task Empty_text_sends_nothing()
    {
        var session = new FakeTerminalSession();
        await TerminalTextSender.SendAsync(session, "", (_, _) => Task.CompletedTask);

        Assert.Empty(session.Writes);
        Assert.Empty(session.Controls);
    }
}

public sealed class KeyChordTranslatorTests
{
    [Theory]
    [InlineData('a', '\u0001')]
    [InlineData('A', '\u0001')]
    [InlineData('c', '\u0003')]
    [InlineData('z', '\u001a')]
    public void Ctrl_letters_become_control_characters(char letter, char expected)
        => Assert.Equal(expected, KeyChordTranslator.ControlChar(letter));

    [Theory]
    [InlineData('1')]
    [InlineData('-')]
    [InlineData('가')]
    public void Non_letters_have_no_control_character(char letter)
        => Assert.Null(KeyChordTranslator.ControlChar(letter));

    [Fact]
    public async Task Escape_is_followed_by_an_interrupt_after_a_beat()
    {
        var session = new FakeTerminalSession();
        var delays = new List<int>();

        await KeyChordTranslator.SendEscapeSequenceAsync(
            session, (t, _) => { delays.Add((int)t.TotalMilliseconds); return Task.CompletedTask; });

        Assert.Equal(new[] { TerminalControl.Escape, TerminalControl.Interrupt }, session.Controls);
        Assert.Equal(KeyChordTranslator.EscapeFollowUpDelayMs, Assert.Single(delays));
    }
}

public sealed class ClipboardAttachmentTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(201, true)]
    public void Only_large_pastes_are_held_back(int length, bool attach)
        => Assert.Equal(attach, ClipboardAttachment.ShouldAttach(new string('x', length)));

    [Fact]
    public void Without_an_attachment_the_typed_text_is_trimmed()
    {
        var (send, display) = ClipboardAttachment.Compose("  hello  ", null);
        Assert.Equal("hello", send);
        Assert.Equal("hello", display);
    }

    [Fact]
    public void The_attachment_is_spliced_at_the_caret()
    {
        var attachment = new ClipboardAttachment("PAYLOAD", CaretIndex: 7);
        var (send, display) = ClipboardAttachment.Compose("review  please", attachment);

        Assert.Equal("review PAYLOAD please", send);
        Assert.Equal("review 📋[clipboard 7 chars] please", display);
    }

    [Fact]
    public void A_caret_past_the_end_is_clamped()
    {
        var attachment = new ClipboardAttachment("PAYLOAD", CaretIndex: 999);
        var (send, _) = ClipboardAttachment.Compose("hi", attachment);
        Assert.Equal("hiPAYLOAD", send);
    }

    [Fact]
    public void An_attachment_with_no_typed_text_is_sent_alone()
    {
        var attachment = new ClipboardAttachment("PAYLOAD", CaretIndex: 0);
        var (send, display) = ClipboardAttachment.Compose("", attachment);

        Assert.Equal("PAYLOAD", send);
        Assert.Equal("📋[clipboard 7 chars]", display);
    }

    [Fact]
    public void The_preview_is_ellipsised()
    {
        var attachment = new ClipboardAttachment(new string('x', 500), 0);
        Assert.Equal(ClipboardAttachment.PreviewLength + 3, attachment.Preview.Length);
        Assert.EndsWith("...", attachment.Preview);
    }
}

public sealed class BotSessionHeaderTests
{
    [Fact]
    public void The_notice_names_the_group_and_the_tab()
        => Assert.Equal("[Session] work / Claude", new BotSession("work", "Claude").Notice);

    [Fact]
    public void A_session_is_announced_once()
    {
        var announcer = new BotSessionAnnouncer();
        var session = new BotSession("work", "Claude");

        Assert.Equal("[Session] work / Claude", announcer.Announce(session));
        Assert.Null(announcer.Announce(session));
        Assert.Null(announcer.Announce(new BotSession("work", "Claude")));
    }

    [Fact]
    public void Switching_away_and_back_announces_again()
    {
        var announcer = new BotSessionAnnouncer();
        announcer.Announce(new BotSession("work", "Claude"));

        Assert.NotNull(announcer.Announce(new BotSession("work", "cmd")));
        Assert.NotNull(announcer.Announce(new BotSession("work", "Claude")));
    }

    [Fact]
    public void Losing_the_terminal_clears_the_memory()
    {
        var announcer = new BotSessionAnnouncer();
        var session = new BotSession("work", "Claude");

        announcer.Announce(session);
        Assert.Null(announcer.Announce(null));
        Assert.NotNull(announcer.Announce(session));
    }
}

public sealed class ToolTurnPresenterTests
{
    [Fact]
    public void Send_to_terminal_is_an_outgoing_exchange()
    {
        var view = ToolTurnPresenter.Present(
            "send_to_terminal", "{\"group\":0,\"tab\":2,\"text\":\"dir\"}", "{\"ok\":true}");

        var exchange = Assert.IsType<TerminalExchangeView>(view);
        Assert.True(exchange.Outgoing);
        Assert.Equal("→", exchange.Arrow);
        Assert.Equal(0, exchange.Group);
        Assert.Equal(2, exchange.Tab);
        Assert.Equal("dir", exchange.Text);
    }

    [Fact]
    public void Read_terminal_is_an_incoming_exchange()
    {
        var view = ToolTurnPresenter.Present(
            "read_terminal", "{\"group\":1,\"tab\":0}", "{\"ok\":true,\"text\":\"C:\\\\>\"}");

        var exchange = Assert.IsType<TerminalExchangeView>(view);
        Assert.False(exchange.Outgoing);
        Assert.Equal("←", exchange.Arrow);
        Assert.Equal("C:\\>", exchange.Text);
    }

    [Fact]
    public void A_failed_read_is_reported_rather_than_shown_as_empty_output()
    {
        var view = ToolTurnPresenter.Present("read_terminal", "{\"group\":0,\"tab\":0}", "{\"ok\":false}");
        Assert.IsType<FailedView>(view);
    }

    [Fact]
    public void Wait_prefers_the_clamped_result_over_the_request()
    {
        var view = ToolTurnPresenter.Present("wait", "{\"seconds\":120}", "{\"waited_seconds\":30}");
        Assert.Equal(30, Assert.IsType<WaitedView>(view).Seconds);
    }

    [Fact]
    public void Wait_falls_back_to_what_was_asked_for()
    {
        var view = ToolTurnPresenter.Present("wait", "{\"seconds\":5}", "{}");
        Assert.Equal(5, Assert.IsType<WaitedView>(view).Seconds);
    }

    [Fact]
    public void Other_tools_stay_compact()
    {
        var view = ToolTurnPresenter.Present("list_terminals", "{}", "{\"groups\":[]}");
        Assert.Equal("list_terminals", Assert.IsType<AdministrativeView>(view).Tool);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    public void Malformed_json_never_throws(string junk)
    {
        Assert.IsType<TerminalExchangeView>(ToolTurnPresenter.Present("send_to_terminal", junk, junk));
        Assert.IsType<FailedView>(ToolTurnPresenter.Present("read_terminal", junk, junk));
        Assert.IsType<WaitedView>(ToolTurnPresenter.Present("wait", junk, junk));
    }
}
