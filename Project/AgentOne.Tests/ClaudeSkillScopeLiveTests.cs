using AgentOne.Agent;
using AgentOne.Tools;
using Xunit.Abstractions;

namespace AgentOne.Tests;

/// <summary>
/// Prints what the file tools see in a real workspace with the real Claude
/// homes — set <c>AGENT_ONE_SKILLS_LIVE_ROOT</c> to the workspace (and
/// optionally <c>AGENT_ONE_SKILLS_LIVE_GREP</c>). Does nothing when unset.
/// </summary>
public class ClaudeSkillScopeLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ShowsTheSkillsOfARealWorkspace()
    {
        var root = Environment.GetEnvironmentVariable("AGENT_ONE_SKILLS_LIVE_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var belt = new LocalFileToolbelt(root);
        output.WriteLine($"detected in {started.ElapsedMilliseconds} ms: {belt.Skills.Summary()}");

        var list = await belt.InvokeAsync(new ToolCall { Tool = "list_files", Args = { ["path"] = "." } }, CancellationToken.None);
        output.WriteLine(list.Text);

        var needle = Environment.GetEnvironmentVariable("AGENT_ONE_SKILLS_LIVE_GREP") ?? "playwright";
        started.Restart();
        var grep = await belt.InvokeAsync(new ToolCall { Tool = "grep", Args = { ["text"] = needle, ["glob"] = "*.md" } }, CancellationToken.None);
        output.WriteLine($"--- grep '{needle}' *.md in {started.ElapsedMilliseconds} ms");
        output.WriteLine(string.Join('\n', grep.Text.Split('\n').Take(25)));
        Assert.True(list.Ok && grep.Ok);
    }
}
