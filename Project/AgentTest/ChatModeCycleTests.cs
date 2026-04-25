using AgentZeroWpf.UI.APP;

namespace AgentTest;

/// <summary>
/// E2E-flavour tests for the AgentBot input-mode cycle (Shift+Tab). Pure
/// transition logic was extracted into <see cref="ChatModeCycle"/> precisely
/// so this can run without WPF — the cycle has had repeat regressions where
/// AI-mode entry was silently swallowed by stale "must be loaded" gates.
///
/// Scenarios cover: full cycle, Ai availability gating, badge stabilisation,
/// and the "lazy-load is allowed to surface AI mode in the cycle" rule
/// added when on-device LLM moved to lazy load on first AIMODE send.
/// </summary>
public sealed class ChatModeCycleTests
{
    [Fact]
    public void Cycle_visits_all_three_modes_when_ai_is_available()
    {
        var path = new[]
        {
            ChatModeCycle.Next(ChatMode.Chat, aiAvailable: true),
            ChatModeCycle.Next(ChatMode.Key,  aiAvailable: true),
            ChatModeCycle.Next(ChatMode.Ai,   aiAvailable: true),
        };

        Assert.Equal(ChatMode.Key, path[0]);
        Assert.Equal(ChatMode.Ai,  path[1]);
        Assert.Equal(ChatMode.Chat, path[2]);
    }

    [Fact]
    public void Cycle_skips_ai_when_unavailable_returning_to_chat_after_key()
    {
        // No AI: Chat → Key → Chat (skip Ai). Ai → Chat shouldn't even be
        // reachable from Cycle in this state, but keep the rule symmetric.
        Assert.Equal(ChatMode.Key,  ChatModeCycle.Next(ChatMode.Chat, aiAvailable: false));
        Assert.Equal(ChatMode.Chat, ChatModeCycle.Next(ChatMode.Key,  aiAvailable: false));
        Assert.Equal(ChatMode.Chat, ChatModeCycle.Next(ChatMode.Ai,   aiAvailable: false));
    }

    [Theory]
    [InlineData(ChatMode.Chat, true,  ChatMode.Chat)]
    [InlineData(ChatMode.Chat, false, ChatMode.Chat)]
    [InlineData(ChatMode.Key,  true,  ChatMode.Key)]
    [InlineData(ChatMode.Key,  false, ChatMode.Key)]
    [InlineData(ChatMode.Ai,   true,  ChatMode.Ai)]   // KEY assertion: Ai stays Ai when available
    [InlineData(ChatMode.Ai,   false, ChatMode.Chat)] // and downgrades only when truly unreachable
    public void Stabilize_for_badge_only_downgrades_unreachable_ai(
        ChatMode wanted, bool aiAvailable, ChatMode expected)
    {
        Assert.Equal(expected, ChatModeCycle.StabilizeForBadge(wanted, aiAvailable));
    }

    [Fact]
    public void Lazy_load_scenario_can_enter_ai_without_loaded_llm()
    {
        // Regression guard for the bug we just fixed: with on-device lazy
        // load, "saved settings + model file on disk" counts as Ai-available
        // even when LlmService.Llm is currently null. The cycle must reach
        // Ai (otherwise AIMODE never gets a chance to lazy-load on first
        // send). The window's IsAiModeAvailable() encodes the disk check;
        // here we simulate its `true` return to verify the cycle responds.
        var afterKey = ChatModeCycle.Next(ChatMode.Key, aiAvailable: true);
        Assert.Equal(ChatMode.Ai, afterKey);

        // And the badge keeps it.
        Assert.Equal(ChatMode.Ai, ChatModeCycle.StabilizeForBadge(ChatMode.Ai, aiAvailable: true));
    }

    [Fact]
    public void Eight_consecutive_cycles_with_ai_available_returns_to_chat_three_times()
    {
        // Long sequence sanity — repeat the cycle and verify period 3.
        var mode = ChatMode.Chat;
        var visits = new List<ChatMode> { mode };
        for (var i = 0; i < 9; i++)
        {
            mode = ChatModeCycle.Next(mode, aiAvailable: true);
            visits.Add(mode);
        }

        // Expected: Chat, Key, Ai, Chat, Key, Ai, Chat, Key, Ai, Chat
        Assert.Equal(
            new[]
            {
                ChatMode.Chat, ChatMode.Key, ChatMode.Ai,
                ChatMode.Chat, ChatMode.Key, ChatMode.Ai,
                ChatMode.Chat, ChatMode.Key, ChatMode.Ai,
                ChatMode.Chat,
            },
            visits);
    }
}
