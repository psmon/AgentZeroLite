using System.Text;
using Agent.Common.Agents;
using Xunit;

namespace ZeroCommon.Tests;

public class TerminalNotificationTests
{
    [Fact]
    public void Osc9_text_is_a_notification()
    {
        var n = TerminalNotificationParser.Parse(9, "Claude needs your permission to use Bash");
        Assert.NotNull(n);
        Assert.Equal("", n!.Title);
        Assert.Equal("Claude needs your permission to use Bash", n.Body);
        Assert.Equal("osc9", n.Source);
    }

    [Theory]
    [InlineData("4;1;50")]     // ConEmu / Windows Terminal progress bar (winget, PowerShell 7.5)
    [InlineData("4;0")]
    [InlineData("9;C:\\x")]    // ConEmu "current directory"
    [InlineData("12")]
    public void Osc9_conemu_commands_are_not_notifications(string data) =>
        Assert.Null(TerminalNotificationParser.Parse(9, data));

    [Fact]
    public void Osc9_text_that_merely_starts_with_a_digit_is_kept() =>
        Assert.NotNull(TerminalNotificationParser.Parse(9, "3 tests failed"));

    [Fact]
    public void Osc777_notify_title_and_body_keep_semicolons_in_the_body()
    {
        var n = TerminalNotificationParser.Parse(777, "notify;OpenCode;done; 2 files changed");
        Assert.Equal("OpenCode", n!.Title);
        Assert.Equal("done; 2 files changed", n.Body);
        Assert.Equal("OpenCode — done; 2 files changed", n.Text);
    }

    [Fact]
    public void Osc777_other_verbs_are_ignored() =>
        Assert.Null(TerminalNotificationParser.Parse(777, "precmd;whatever"));

    [Fact]
    public void Osc99_title_body_and_base64()
    {
        Assert.Equal("Build finished", TerminalNotificationParser.Parse(99, "i=1:d=0;Build finished")!.Title);
        Assert.Equal("all green", TerminalNotificationParser.Parse(99, "i=1:p=body;all green")!.Body);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("한글 알림"));
        Assert.Equal("한글 알림", TerminalNotificationParser.Parse(99, "e=1;" + b64)!.Title);
        Assert.Null(TerminalNotificationParser.Parse(99, "i=1:p=?;"));        // a capability query
        Assert.Null(TerminalNotificationParser.Parse(99, "e=1;!!not-base64"));
    }

    [Fact]
    public void Text_is_cleaned_and_capped()
    {
        var n = TerminalNotificationParser.Parse(9, "line1\nline2\u001b[31m\a  end");
        Assert.Equal("line1 line2[31m end", n!.Body);
        var long_ = TerminalNotificationParser.Parse(9, new string('x', 1000))!;
        Assert.True(long_.Body.Length <= TerminalNotificationParser.MaxLength + 1);
        Assert.Null(TerminalNotificationParser.Parse(9, " \t\n"));
        Assert.Null(TerminalNotificationParser.Parse(42, "hello"));
    }

    // ── AttentionNotifier.ObserveNotification ─────────────────────────────────

    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TerminalNotification Note = new("", "needs approval", "osc9");

    [Fact]
    public void Notification_alerts_at_once_with_its_text()
    {
        var a = new AttentionNotifier().ObserveNotification(1, 2, "Gemini", Note, new AttentionSettings(), appActive: false, T0);
        Assert.NotNull(a);
        Assert.Equal(AttentionKind.Notify, a!.Kind);
        Assert.Equal("needs approval", a.Message);
        Assert.Equal((1, 2, "Gemini"), (a.Group, a.Tab, a.Title));
    }

    [Fact]
    public void Same_text_from_same_terminal_is_deduplicated_within_the_window()
    {
        var n = new AttentionNotifier();
        var s = new AttentionSettings();
        Assert.NotNull(n.ObserveNotification(0, 0, "t", Note, s, false, T0));
        Assert.Null(n.ObserveNotification(0, 0, "t", Note, s, false, T0.AddSeconds(3)));
        Assert.NotNull(n.ObserveNotification(0, 0, "t", Note with { Body = "other" }, s, false, T0.AddSeconds(3)));
        Assert.NotNull(n.ObserveNotification(0, 1, "t2", Note, s, false, T0.AddSeconds(3)));
        Assert.NotNull(n.ObserveNotification(0, 0, "t", Note, s, false, T0 + AttentionNotifier.NotifyRepeatWindow));
    }

    [Fact]
    public void Notification_respects_the_switches()
    {
        var n = new AttentionNotifier();
        Assert.Null(n.ObserveNotification(0, 0, "t", Note, new AttentionSettings { OnNotify = false }, false, T0));
        Assert.Null(n.ObserveNotification(0, 0, "t", Note, new AttentionSettings { Enabled = false }, false, T0));
        Assert.Null(n.ObserveNotification(0, 0, "t", Note, new AttentionSettings(), appActive: true, T0));
        Assert.NotNull(n.ObserveNotification(0, 0, "t", Note, new AttentionSettings { OnlyWhenInactive = false }, appActive: true, T0));
    }

    [Fact]
    public void Settings_file_written_before_the_field_defaults_to_on()
    {
        var path = Path.Combine(Path.GetTempPath(), $"attention-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"Enabled\":true,\"OnBlocked\":true,\"OnDone\":false}");
            Assert.True(AttentionSettingsStore.Load(path).OnNotify);
        }
        finally { File.Delete(path); }
    }
}
