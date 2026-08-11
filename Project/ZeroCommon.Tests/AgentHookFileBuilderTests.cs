using System.Linq;
using System.Text.Json;
using Agent.Common.Agents;

namespace ZeroCommon.Tests;

/// <summary>Headless tests for the multi-CLI hook-file builder (herdr-adoption H4).</summary>
[Trait("Category", "HookFileBuilder")]
public sealed class AgentHookFileBuilderTests
{
    private const string Exe = @"C:\app\AgentZeroLite.exe";

    [Fact]
    public void AddHooks_Codex_CreatesEventsWithCommands()
    {
        var json = AgentHookFileBuilder.AddHooks("", Exe, AgentHookFileBuilder.CodexEvents);
        using var doc = JsonDocument.Parse(json);
        var hooks = doc.RootElement.GetProperty("hooks");
        Assert.True(hooks.TryGetProperty("PermissionRequest", out var pr));
        var cmd = pr[0].GetProperty("command").GetString()!;
        Assert.Contains("-cli agent-hook", cmd);
        Assert.Contains("--event PermissionRequest", cmd);
        Assert.Contains("--state blocked", cmd);
    }

    [Fact]
    public void AddHooks_Cursor_UsesCamelEvents()
    {
        var json = AgentHookFileBuilder.AddHooks("", Exe, AgentHookFileBuilder.CursorEvents);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("hooks").TryGetProperty("beforeShellExecution", out _));
    }

    [Fact]
    public void AddHooks_Idempotent_NoDuplicates()
    {
        var once = AgentHookFileBuilder.AddHooks("", Exe, AgentHookFileBuilder.CodexEvents);
        var twice = AgentHookFileBuilder.AddHooks(once, Exe, AgentHookFileBuilder.CodexEvents);
        using var doc = JsonDocument.Parse(twice);
        Assert.Single(doc.RootElement.GetProperty("hooks").GetProperty("Stop").EnumerateArray());
    }

    [Fact]
    public void AddHooks_PreservesForeignHooks()
    {
        var existing = """{ "hooks": { "PreToolUse": [ { "command": "echo foreign" } ] } }""";
        var json = AgentHookFileBuilder.AddHooks(existing, Exe, AgentHookFileBuilder.CodexEvents);
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray().ToList();
        Assert.Equal(2, arr.Count); // foreign + ours
    }

    [Fact]
    public void RemoveHooks_RemovesOnlyOurs()
    {
        var existing = """{ "hooks": { "PreToolUse": [ { "command": "echo foreign" } ] } }""";
        var added = AgentHookFileBuilder.AddHooks(existing, Exe, AgentHookFileBuilder.CodexEvents);
        var (json, changed) = AgentHookFileBuilder.RemoveHooks(added);
        Assert.True(changed);
        Assert.False(AgentHookFileBuilder.HasOurHooks(json));
        using var doc = JsonDocument.Parse(json);
        Assert.Single(doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray()); // foreign kept
    }

    [Fact]
    public void HasOurHooks_DetectsPresence()
    {
        Assert.False(AgentHookFileBuilder.HasOurHooks(""));
        var json = AgentHookFileBuilder.AddHooks("", Exe, AgentHookFileBuilder.CursorEvents);
        Assert.True(AgentHookFileBuilder.HasOurHooks(json));
    }
}
