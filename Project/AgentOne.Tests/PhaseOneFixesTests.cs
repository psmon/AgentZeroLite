using AgentOne.Agent;
using AgentOne.Tools;

namespace AgentOne.Tests;

/// <summary>
/// Gemma 4's native tool-call syntax. Measured (2026-09-30): two write_file
/// calls came back in it, were taken for prose and shown as the answer, and
/// nothing was written. These are those replies, shortened.
/// </summary>
public class GemmaNativeCallTests
{
    private const string Measured =
        "<|tool_call>call:write_file{args:{content:<|\"|># Phase 1 Knowledge\n\n## Assumptions\n- \"quoted\" and {braces}, commas.\n<|\"|>,path:\"notes/phase-1.md\"}}<tool_call|>";

    [Fact]
    public void TheMeasuredReplyIsAWriteFileCall()
    {
        Assert.True(ToolCall.TryParse(Measured, out var call, out var error), error);

        Assert.Equal("write_file", call.Tool);
        Assert.Equal("notes/phase-1.md", call.Arg("path"));
        Assert.Equal("# Phase 1 Knowledge\n\n## Assumptions\n- \"quoted\" and {braces}, commas.\n", call.Arg("content"));
    }

    [Theory]
    [InlineData("<|tool_call>call: list_files{}<tool_call|>", "list_files", "", "")]
    [InlineData("<|tool_call>call:read_file{path:\"src/a.cs\"}", "read_file", "path", "src/a.cs")]
    [InlineData("Let me look.\n<|tool_call>call:grep{args:{text:<|\"|>Foo.Bar<|\"|>}}<tool_call|>", "grep", "text", "Foo.Bar")]
    [InlineData("<|tool_call>call:final{args:{text:<|\"|>다 됐습니다<|\"|>}}<tool_call|>", "final", "text", "다 됐습니다")]
    public void TheShapesItComesInAllParse(string raw, string tool, string arg, string value)
    {
        Assert.True(ToolCall.TryParse(raw, out var call, out var error), error);
        Assert.Equal(tool, call.Tool);
        if (arg.Length > 0) Assert.Equal(value, call.Arg(arg));
    }

    [Fact]
    public void AnUnreadableNativeCallIsAFailureNeverAnAnswer()
    {
        const string broken = "<|tool_call>call:write_file{args:{content:<|\"|># never closed";

        Assert.False(ToolCall.TryParse(broken, out _, out var error));
        Assert.Contains("<|tool_call>", error);
        Assert.True(ToolCall.LooksLikeEnvelope(broken));
    }

    [Fact]
    public async Task TheLoopWritesTheFileANativeCallAsksFor()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-one-gemma-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var provider = new ScriptedChatProvider(Measured, """{"tool":"final","args":{"text":"notes/phase-1.md 를 썼습니다."}}""");
            var loop = new AgentLoop(provider, new LocalFileToolbelt(root), maxSteps: 4) { Root = root };

            var run = await loop.RunAsync("write the phase 1 notes");

            Assert.True(run.Succeeded);
            Assert.Contains(run.Steps, s => s.Tool == "write_file" && s.Ok);
            Assert.StartsWith("# Phase 1 Knowledge", File.ReadAllText(Path.Combine(root, "notes", "phase-1.md")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>Writing git configuration is always put to a person; reading it is not.</summary>
public class GitConfigRiskTests
{
    [Theory]
    [InlineData("git config user.email \"phase1@local\" && git config user.name \"Phase 1\"")]   // measured
    [InlineData("git -C ../phase-1 config user.email \"phase1@local\"")]                         // measured
    [InlineData("git config --global user.name me")]
    [InlineData("git config --local --unset user.name")]
    [InlineData("git config core.hooksPath .hooks")]
    public void WritesAreDangerous(string command) =>
        Assert.True(CommandRisk.Inspect(command).Dangerous, command);

    [Theory]
    [InlineData("git config user.name")]
    [InlineData("git config --get user.email")]
    [InlineData("git config --list")]
    [InlineData("git config --global --list")]
    [InlineData("git config user.name && git status")]
    [InlineData("git status")]
    public void ReadsAreNot(string command) =>
        Assert.False(CommandRisk.Inspect(command).Dangerous, command);
}

/// <summary>The shell is found, not assumed, and every prompt names the same one.</summary>
public class ShellInfoTests
{
    [Fact]
    public void TheShellIsDetectedWithItsVersionAndIsTheOneThatRuns()
    {
        var shell = ShellInfo.Current;

        Assert.Equal(shell.Describe, ShellToolbelt.ShellName);
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(shell.Dialect, new[] { ShellDialect.Pwsh, ShellDialect.WindowsPowerShell });
            Assert.Matches(@"^\d+\.\d+", shell.Version);   // asked, not assumed
        }
    }

    [Fact]
    public void WindowsPowerShellIsToldThatAndAndDoesNotExist()
    {
        var ps5 = new ShellInfo("powershell", ShellDialect.WindowsPowerShell, "5.1.26100.1");

        Assert.Equal("Windows PowerShell 5.1.26100.1 (powershell)", ps5.Describe);
        Assert.Contains("&& and || do NOT exist", ps5.Hints);
        Assert.Contains("no -p", new ShellInfo("pwsh", ShellDialect.Pwsh, "7.5.3").Hints);
    }

    [Fact]
    public void TheSystemPromptNamesTheShellAndTheRootLimit()
    {
        var prompt = SystemPrompt.Build("C:/ws");

        Assert.Contains($"run_command runs in {ShellInfo.Current.Describe}", prompt);
        Assert.Contains(ShellInfo.Current.Hints, prompt);
        Assert.Contains("write_file can only write inside the workspace root", prompt);
    }

    [Fact]
    public void TheDesignPromptKeepsFilesUnderTheRootAndCommandsInTheShell()
    {
        var prompt = ReasoningSubtask.DesignSystemPrompt("small", "PowerShell 7.5.3 (pwsh)", "mkdir takes one path");

        Assert.Contains("write_file can only write inside the project root", prompt);
        Assert.Contains("never ../", prompt);
        Assert.Contains("Write every command for that shell. mkdir takes one path", prompt);
    }
}
