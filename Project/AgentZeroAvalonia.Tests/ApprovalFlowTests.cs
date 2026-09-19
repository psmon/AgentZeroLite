using Agent.Common;
using Agent.Common.Services;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>
/// M0041 — approvals and links. The bot watches the active terminal through
/// <see cref="AgentEventStream"/>; these drive the events directly so no PTY is needed.
/// </summary>
public class ApprovalFlowTests
{
    private static ApprovalRequested Approval(string command = "rm -rf build", int options = 3)
        => new(command,
            Enumerable.Range(1, options)
                .Select(n => new ApprovalParser.ApprovalOption(n, $"Option {n}"))
                .ToList(),
            IsFallback: false,
            DateTimeOffset.UtcNow);

    private static (AgentBotViewModel Vm, FakeBotSession Session) Wired(bool showSystem = true)
    {
        var session = new FakeBotSession();
        var vm = new AgentBotViewModel
        {
            ActiveSession = () => session,
            ActiveSessionLabel = () => "work / cmd",
            HideSystemMessages = !showSystem,
            Delay = (_, _) => Task.CompletedTask,
        };
        return (vm, session);
    }

    [Fact]
    public void An_approval_raises_the_toast_with_the_offered_options()
    {
        var (vm, _) = Wired();
        vm.HandleAgentEvent(Approval());

        Assert.True(vm.Approval.IsOpen);
        Assert.Equal("rm -rf build", vm.Approval.Command);
        Assert.Equal(3, vm.Approval.Options.Count);
        Assert.Equal(0, vm.Approval.Options[0].Index);
        Assert.Contains("1. Option 1", vm.Approval.Options[0].Label);
    }

    [Fact]
    public void Choosing_an_option_sends_the_arrows_and_enter_to_the_terminal()
    {
        var (vm, session) = Wired();
        vm.HandleAgentEvent(Approval());

        vm.Approval.Choose(2);

        Assert.False(vm.Approval.IsOpen);
        Assert.True(SpinUntil(() => session.Controls.Count == 3));
        Assert.Equal(
            new[] { TerminalControl.DownArrow, TerminalControl.DownArrow, TerminalControl.Enter },
            session.Controls);
    }

    [Fact]
    public void Auto_approve_answers_without_showing_the_toast()
    {
        var (vm, session) = Wired();
        vm.AutoApprove = true;

        vm.HandleAgentEvent(Approval());

        Assert.False(vm.Approval.IsOpen);
        Assert.True(SpinUntil(() => session.Controls.Count == 1));
        Assert.Equal(TerminalControl.Enter, Assert.Single(session.Controls));
    }

    [Fact]
    public void Switching_auto_approve_off_during_the_delay_abandons_the_answer()
    {
        var session = new FakeBotSession();
        var gate = new TaskCompletionSource();
        var vm = new AgentBotViewModel
        {
            ActiveSession = () => session,
            HideSystemMessages = false,
            Delay = (_, _) => gate.Task,
        };
        vm.AutoApprove = true;
        vm.AutoApproveDelayText = "5";

        vm.HandleAgentEvent(Approval());
        vm.AutoApprove = false;      // the user changed their mind while we waited
        gate.SetResult();

        Thread.Sleep(50);
        Assert.Empty(session.Controls);
    }

    [Fact]
    public void Muting_suppresses_the_next_approval()
    {
        var (vm, _) = Wired();
        vm.Approval.Mute();

        vm.HandleAgentEvent(Approval());

        Assert.True(vm.Approval.IsMuted);
        Assert.False(vm.Approval.IsOpen);
    }

    [Fact]
    public async Task The_toast_counts_down_and_hides_itself()
    {
        var vm = new AgentBotViewModel { ActiveSession = () => new FakeBotSession() };
        vm.Approval.Delay = (_, _) => Task.CompletedTask;

        vm.HandleAgentEvent(Approval());

        await Task.Yield();
        Assert.True(SpinUntil(() => !vm.Approval.IsOpen));
        Assert.Equal(0, vm.Approval.RemainingSeconds);
    }

    [Fact]
    public void A_detected_url_becomes_one_bubble_and_repeats_are_suppressed()
    {
        var (vm, _) = Wired();
        var now = DateTimeOffset.UtcNow;

        vm.HandleAgentEvent(new UrlDetected("https://example.com", now));
        vm.HandleAgentEvent(new UrlDetected("https://example.com", now));

        var bubble = Assert.Single(vm.Items, i => i.Kind == ChatItemKind.Url);
        Assert.Equal("https://example.com", bubble.Url);
    }

    [Fact]
    public void Clicking_a_url_bubble_hands_the_link_to_the_shell()
    {
        var (vm, _) = Wired();
        string? opened = null;
        vm.OpenUrl = u => opened = u;

        vm.HandleAgentEvent(new UrlDetected("https://example.com/docs", DateTimeOffset.UtcNow));
        vm.OpenLink(Assert.Single(vm.Items, i => i.Kind == ChatItemKind.Url));

        Assert.Equal("https://example.com/docs", opened);
    }

    [Fact]
    public void The_session_header_tracks_the_active_terminal_and_announces_it_once()
    {
        var (vm, _) = Wired();

        vm.OnActiveSessionChanged("work", "cmd");
        Assert.True(vm.HasSession);
        Assert.Equal("work", vm.SessionGroup);
        Assert.Equal("cmd", vm.SessionTab);
        Assert.Single(vm.Items, i => i.Text.Contains("[Session] work / cmd"));

        vm.OnActiveSessionChanged("work", "cmd");
        Assert.Single(vm.Items, i => i.Text.Contains("[Session] work / cmd"));
    }

    private static bool SpinUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) Thread.Sleep(5);
        return condition();
    }
}
