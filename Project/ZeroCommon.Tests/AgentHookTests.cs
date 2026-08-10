using System.Text.Json.Nodes;
using Agent.Common.Actors;
using Agent.Common.Agents;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for the agent-hook state pipeline (mission W1, orca-adoption):
/// the event→phase mapper and the settings.json subtree merger.
/// </summary>
[Trait("Category", "AgentHook")]
public sealed class AgentHookTests
{
    // ------------------------------------------------------------ mapper

    [Theory]
    [InlineData("UserPromptSubmit", AgentLoopPhase.Thinking)]
    [InlineData("PreToolUse", AgentLoopPhase.Acting)]
    [InlineData("PostToolUse", AgentLoopPhase.Acting)]
    [InlineData("Stop", AgentLoopPhase.Done)]
    [InlineData("SessionStart", AgentLoopPhase.Idle)]
    [InlineData("SessionEnd", AgentLoopPhase.Done)]
    [InlineData("Notification", AgentLoopPhase.Thinking)]
    [InlineData("pretooluse", AgentLoopPhase.Acting)]      // case-insensitive
    [InlineData("SomethingUnknown", AgentLoopPhase.Thinking)] // safe default
    public void MapEventToPhase_MapsKnownEvents(string hookEvent, AgentLoopPhase expected)
    {
        Assert.Equal(expected, AgentHookMapper.MapEventToPhase(hookEvent));
    }

    [Fact]
    public void Resolve_StateOverride_WinsOverEvent()
    {
        var evt = new AgentHookEvent("PreToolUse", StateOverride: "Done", Session: "s1", Detail: "");
        var (phase, _) = AgentHookMapper.Resolve(evt);
        Assert.Equal(AgentLoopPhase.Done, phase);
    }

    [Fact]
    public void Resolve_InvalidStateOverride_FallsBackToEvent()
    {
        var evt = new AgentHookEvent("Stop", StateOverride: "garbage", Session: "s1", Detail: "");
        var (phase, _) = AgentHookMapper.Resolve(evt);
        Assert.Equal(AgentLoopPhase.Done, phase);
    }

    [Fact]
    public void Resolve_IncludesDetailInText()
    {
        var evt = new AgentHookEvent("PreToolUse", null, "s1", "Bash");
        var (_, text) = AgentHookMapper.Resolve(evt);
        Assert.Contains("Bash", text);
    }

    // ------------------------------------------------------------ merger

    private static string Cmd(string ev) => $"\"C:/app/AgentZeroLite.exe\" -cli agent-hook --event {ev} --no-wait";

    [Fact]
    public void AddHooks_CreatesEntriesForEveryEvent()
    {
        var root = new JsonObject();
        AgentHookSettingsMerger.AddHooks(root, Cmd);

        Assert.True(AgentHookSettingsMerger.HasOurHooks(root));
        var hooks = root["hooks"]!.AsObject();
        foreach (var ev in AgentHookSettingsMerger.DefaultEvents)
            Assert.True(hooks.ContainsKey(ev), $"missing event {ev}");
    }

    [Fact]
    public void AddHooks_IsIdempotent_NoDuplicateEntries()
    {
        var root = new JsonObject();
        AgentHookSettingsMerger.AddHooks(root, Cmd);
        AgentHookSettingsMerger.AddHooks(root, Cmd);

        var preToolUse = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Single(preToolUse); // reinstall replaced, did not duplicate
    }

    [Fact]
    public void AddHooks_PreservesForeignHooks()
    {
        var root = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(new JsonObject
                {
                    ["matcher"] = "Bash",
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "echo foreign" }),
                }),
            },
        };

        AgentHookSettingsMerger.AddHooks(root, Cmd);
        var preToolUse = root["hooks"]!["PreToolUse"]!.AsArray();
        // one foreign + one ours
        Assert.Equal(2, preToolUse.Count);
    }

    [Fact]
    public void RemoveHooks_RemovesOnlyOurs_AndPrunes()
    {
        var root = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(new JsonObject
                {
                    ["matcher"] = "Bash",
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "echo foreign" }),
                }),
            },
        };
        AgentHookSettingsMerger.AddHooks(root, Cmd);

        Assert.True(AgentHookSettingsMerger.RemoveHooks(root));
        Assert.False(AgentHookSettingsMerger.HasOurHooks(root));

        // Foreign entry survived; Stop (only-ours) event was pruned entirely.
        Assert.Equal(1, root["hooks"]!["PreToolUse"]!.AsArray().Count);
        Assert.False(root["hooks"]!.AsObject().ContainsKey("Stop"));
    }

    [Fact]
    public void RemoveHooks_OnCleanGraph_NoChange()
    {
        var root = new JsonObject();
        Assert.False(AgentHookSettingsMerger.RemoveHooks(root));
    }
}
