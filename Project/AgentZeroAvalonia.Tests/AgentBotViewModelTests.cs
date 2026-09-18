using Agent.Common.Actors;
using Agent.Common.Agents;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

public class AgentBotViewModelTests
{
    [Fact]
    public void Mode_cycle_skips_ai_when_no_llm_is_available()
    {
        // ChatModeCycle is the shared rule; the view model asks it with the gateway's answer.
        Assert.Equal(ChatMode.Key, ChatModeCycle.Next(ChatMode.Chat, aiAvailable: false));
        Assert.Equal(ChatMode.Chat, ChatModeCycle.Next(ChatMode.Key, aiAvailable: false));
        Assert.Equal(ChatMode.Ai, ChatModeCycle.Next(ChatMode.Key, aiAvailable: true));
        Assert.Equal(ChatMode.Chat, ChatModeCycle.Next(ChatMode.Ai, aiAvailable: true));
    }

    [Fact]
    public void Progress_becomes_one_live_row_and_tool_cards()
    {
        var vm = new AgentBotViewModel();
        vm.ApplyProgress(new AgentLoopProgress(AgentLoopPhase.Thinking, "", 1));
        var progress = Assert.Single(vm.Items);
        Assert.Equal(ChatItemKind.Progress, progress.Kind);
        Assert.Contains("thinking", progress.Text);

        vm.ApplyProgress(new AgentLoopProgress(AgentLoopPhase.Generating, "", 1) { Tokens = 42 });
        Assert.Single(vm.Items);                       // same row, updated
        Assert.Contains("42 tok", vm.Items[0].Text);

        vm.ApplyProgress(new AgentLoopProgress(AgentLoopPhase.Acting, "", 1)
        {
            ToolCall = new AgentLoopToolCallInfo("list_terminals", "{}", "{\"groups\":[]}"),
        });
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(ChatItemKind.Tool, vm.Items[0].Kind);
        Assert.Contains("list_terminals", vm.Items[0].Label);
        Assert.Equal("{\"groups\":[]}", vm.Items[0].Detail);
        Assert.Equal(ChatItemKind.Progress, vm.Items[1].Kind);   // a fresh spinner for round 1
    }

    [Fact]
    public void Result_replaces_the_progress_row_with_the_answer_and_a_timing_line()
    {
        var vm = new AgentBotViewModel();
        vm.ApplyProgress(new AgentLoopProgress(AgentLoopPhase.Thinking, "", 1));
        vm.ApplyResult(new AgentLoopResult(true, "All three terminals are idle.", 2, 1234));

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(ChatItemKind.Bot, vm.Items[0].Kind);
        Assert.Equal("All three terminals are idle.", vm.Items[0].Text);
        Assert.Equal(ChatItemKind.System, vm.Items[1].Kind);
        Assert.Contains("2 turn(s)", vm.Items[1].Text);
        Assert.False(vm.AiBusy);
        Assert.DoesNotContain(vm.Items, i => i.Kind == ChatItemKind.Progress);
    }

    [Fact]
    public void Failure_shows_the_reason()
    {
        var vm = new AgentBotViewModel();
        vm.ApplyResult(new AgentLoopResult(false, "", 0, 50, FailureReason: "provider returned 401"));
        Assert.Contains("provider returned 401", vm.Items[0].Text);
        Assert.Contains("failed after", vm.Items[1].Text);
    }

    [Fact]
    public void External_chat_unwraps_DONE_envelopes()
    {
        var vm = new AgentBotViewModel();
        vm.ReceiveExternalChat("Claude", "DONE(handshake-ok)");
        vm.ReceiveExternalChat("CLI", "DONE(Claude-2, finished the build)");
        vm.ReceiveExternalChat("tester", "plain text");

        Assert.Equal("DONE(Claude)", vm.Items[0].Label);
        Assert.Equal("handshake-ok", vm.Items[0].Text);
        Assert.Equal("DONE(Claude-2)", vm.Items[1].Label);
        Assert.Equal("finished the build", vm.Items[1].Text);
        Assert.Equal("tester", vm.Items[2].Label);
        Assert.Equal("plain text", vm.Items[2].Text);
    }

    [Fact]
    public void Chat_mode_without_a_terminal_explains_instead_of_throwing()
    {
        var vm = new AgentBotViewModel { Input = "dir" };
        vm.Send();
        var only = Assert.Single(vm.Items);
        Assert.Equal(ChatItemKind.System, only.Kind);
        Assert.Contains("No active terminal", only.Text);
        Assert.Equal("", vm.Input);
    }
}
