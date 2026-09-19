using Agent.Common.Actors;
using Agent.Common.Agents;
using Agent.Common.Services;
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
        // The timing line is chatter, hidden by default as in the WPF host; show it here.
        var vm = new AgentBotViewModel { HideSystemMessages = false };
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

    // ── M0041 ────────────────────────────────────────────────────────────────

    /// <summary>A view model wired to a fake terminal, with time collapsed to nothing.</summary>
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
    public void Chat_mode_also_tells_the_bot_actor()
    {
        var (vm, session) = Wired();
        var told = new List<object>();
        vm.BotTellOverride = told.Add;

        vm.Input = "dir";
        vm.Send();

        Assert.Equal("dir", Assert.Single(session.Writes));
        var input = Assert.IsType<UserInput>(Assert.Single(told));
        Assert.Equal("dir", input.Text);
    }

    [Fact]
    public void Clear_wipes_the_screen_without_echoing_a_bubble()
    {
        var (vm, session) = Wired();
        vm.Input = "clear";
        vm.Send();

        Assert.Equal(TerminalControl.ClearScreen, Assert.Single(session.Controls));
        Assert.Empty(session.Writes);
        Assert.DoesNotContain(vm.Items, i => i.Kind == ChatItemKind.User);
    }

    [Fact]
    public async Task Large_multi_line_text_goes_through_the_async_writer_with_an_extra_enter()
    {
        var (vm, session) = Wired();
        vm.Input = new string('x', 300) + "\nsecond";
        vm.Send();

        await session.Drain();
        Assert.Single(session.Writes);
        Assert.Equal(TerminalControl.Enter, Assert.Single(session.Controls));
    }

    [Fact]
    public void Hidden_system_messages_still_let_the_bot_speak_and_still_explain_failures()
    {
        var vm = new AgentBotViewModel();               // HideSystemMessages defaults to true
        vm.AddSystem("chatter");
        Assert.Empty(vm.Items);

        vm.AddNotice("you need to do something");
        Assert.Single(vm.Items);
    }

    [Fact]
    public void Leaving_ai_mode_resets_the_agent_session()
    {
        var vm = new AgentBotViewModel { Mode = ChatMode.Ai, AiBusy = true };
        var told = new List<object>();
        vm.BotTellOverride = told.Add;

        vm.CycleMode();                                  // Ai -> Chat

        Assert.Equal(ChatMode.Chat, vm.Mode);
        Assert.Contains(told, m => m is ResetAgentLoopMemory);
        Assert.False(vm.AiBusy);
    }

    [Fact]
    public void Mode_toast_appears_and_then_clears()
    {
        var gate = new TaskCompletionSource();
        var vm = new AgentBotViewModel { Delay = (_, _) => gate.Task };

        vm.CycleMode();
        Assert.NotNull(vm.ModeToast);

        gate.SetResult();
        Assert.True(SpinUntil(() => vm.ModeToast is null));
    }

    [Fact]
    public void Clipboard_attachment_is_spliced_on_send_and_then_cleared()
    {
        var (vm, session) = Wired();
        vm.Input = "review  please";
        vm.AttachClipboard("PAYLOAD", caretIndex: 7);

        Assert.True(vm.HasAttachment);
        Assert.Equal("[clipboard 7 chars]", vm.AttachmentTag);

        vm.Send();

        Assert.Equal("review PAYLOAD please", Assert.Single(session.Writes));
        Assert.False(vm.HasAttachment);
        Assert.Null(vm.AttachmentTag);
        // The transcript shows the chip, not the payload.
        var bubble = Assert.Single(vm.Items, i => i.Kind == ChatItemKind.User);
        Assert.Contains("[clipboard 7 chars]", bubble.Text);
        Assert.DoesNotContain("PAYLOAD", bubble.Text);
    }

    [Fact]
    public void Terminal_tool_turns_render_as_exchange_bubbles()
    {
        var vm = new AgentBotViewModel();

        vm.ApplyProgress(new AgentLoopProgress(AgentLoopPhase.Acting, "", 1)
        {
            ToolCall = new AgentLoopToolCallInfo(
                "send_to_terminal", "{\"group\":0,\"tab\":1,\"text\":\"dir\"}", "{\"ok\":true}"),
        });
        vm.ApplyProgress(new AgentLoopProgress(AgentLoopPhase.Acting, "", 2)
        {
            ToolCall = new AgentLoopToolCallInfo(
                "read_terminal", "{\"group\":0,\"tab\":1}", "{\"ok\":true,\"text\":\"C:\\\\>\"}"),
        });

        Assert.Contains(vm.Items, i => i.Kind == ChatItemKind.TerminalOut && i.Text == "dir");
        Assert.Contains(vm.Items, i => i.Kind == ChatItemKind.TerminalIn && i.Text == "C:\\>");
    }

    [Fact]
    public void Auto_approve_delay_is_clamped_and_bad_text_keeps_the_last_value()
    {
        var vm = new AgentBotViewModel { AutoApproveDelayText = "5" };
        Assert.Equal(5, vm.AutoApproveDelaySeconds);

        vm.AutoApproveDelayText = "99";
        Assert.Equal(5, vm.AutoApproveDelaySeconds);

        vm.AutoApproveDelayText = "abc";
        Assert.Equal(5, vm.AutoApproveDelaySeconds);

        vm.AutoApproveDelayText = "0";
        Assert.Equal(0, vm.AutoApproveDelaySeconds);
    }

    [Fact]
    public void Ask_ai_switches_mode_and_queues_the_request_until_the_actor_attaches()
    {
        // No actor system in a headless test, so the request parks and the user is told.
        var vm = new AgentBotViewModel { HideSystemMessages = false };
        vm.AskAi("list my terminals");

        Assert.Equal(ChatMode.Ai, vm.Mode);
        Assert.Contains(vm.Items, i => i.Kind == ChatItemKind.User && i.Text == "list my terminals");
    }

    private static bool SpinUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) Thread.Sleep(5);
        return condition();
    }
}
