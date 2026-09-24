using Agent.Common.Llm;
using Agent.Common.Llm.Tools;

namespace ZeroCommon.Tests.Llm;

/// <summary>
/// The AI-mode tool chain's turn budget, which is a setting rather than a constant since
/// the hosts started passing it to <see cref="AgentLoopOptions.MaxIterations"/>. The
/// clamp lives on the settings object because two callers read it — the settings screen's
/// spinner and a JSON file a person may have edited — and a budget of 0 would end every
/// run before the first model call.
/// </summary>
[Trait("Category", "Llm")]
public class AgentLoopTurnBudgetTests
{
    [Fact]
    public void The_stored_default_is_the_budget_the_loop_already_used()
    {
        // Exposing a knob must not change what existing installations do: a settings file
        // written before this field existed deserializes to the default, and that default
        // has to be the number the loop ran with when nobody could set it.
        Assert.Equal(new AgentLoopOptions().MaxIterations, LlmRuntimeSettings.DefaultAgentLoopMaxTurns);
        Assert.Equal(LlmRuntimeSettings.DefaultAgentLoopMaxTurns, new LlmRuntimeSettings().ResolveAgentLoopMaxTurns());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_missing_or_nonsense_budget_falls_back_to_the_default(int stored)
    {
        // 0 is what an absent JSON number deserializes to, so it must read as "unset"
        // rather than as "never take a turn".
        var s = new LlmRuntimeSettings { AgentLoopMaxTurns = stored };
        Assert.Equal(LlmRuntimeSettings.DefaultAgentLoopMaxTurns, s.ResolveAgentLoopMaxTurns());
    }

    [Fact]
    public void A_budget_past_the_ceiling_is_capped()
    {
        var s = new LlmRuntimeSettings { AgentLoopMaxTurns = 100_000 };
        Assert.Equal(LlmRuntimeSettings.MaxAgentLoopMaxTurns, s.ResolveAgentLoopMaxTurns());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(50)]
    [InlineData(LlmRuntimeSettings.MaxAgentLoopMaxTurns)]
    public void A_budget_inside_the_bounds_is_passed_through(int stored)
    {
        var s = new LlmRuntimeSettings { AgentLoopMaxTurns = stored };
        Assert.Equal(stored, s.ResolveAgentLoopMaxTurns());
    }

    [Fact]
    public void The_bounds_leave_room_for_a_real_tool_chain()
    {
        // A relay through another terminal costs three turns on its own (send, wait,
        // read), so a ceiling anywhere near that would make the setting useless.
        Assert.True(LlmRuntimeSettings.MinAgentLoopMaxTurns >= 1);
        Assert.True(LlmRuntimeSettings.MaxAgentLoopMaxTurns >= 4 * LlmRuntimeSettings.DefaultAgentLoopMaxTurns);
    }
}
