using Agent.Common.Module;

namespace ZeroCommon.Tests;

/// <summary>
/// The "disable TUI animations" checkbox on a CLI definition. The rule that matters
/// is that ticking it on a CLI with no such switch must add nothing: an argument a
/// program does not recognise is usually fatal at launch, so a wrong guess here
/// produces a tab that will not start.
/// </summary>
public class ReducedMotionArgumentsTests
{
    [Theory]
    [InlineData("codex.cmd", null)]
    [InlineData("C:/tools/codex.exe", null)]
    [InlineData("powershell.exe", "-NoExit -Command codex")]
    [InlineData("cmd.exe", "/k codex --search")]
    public void Resolve_FindsCodex_InTheExeOrTheArguments(string exe, string? args)
    {
        // The shipped definitions launch the agent through a shell, so the tool name
        // is usually in the arguments rather than the executable.
        Assert.Equal(ReducedMotionArguments.CodexFlag, ReducedMotionArguments.Resolve(exe, args));
    }

    [Theory]
    [InlineData("pwsh.exe", "-NoExit -Command claude")]
    [InlineData("cmd.exe", null)]
    [InlineData("powershell.exe", "-NoExit")]
    public void Resolve_UnknownCli_AddsNothing(string exe, string? args)
    {
        Assert.Equal("", ReducedMotionArguments.Resolve(exe, args));
    }

    /// <summary>A directory or file that merely contains the letters is not the tool.</summary>
    [Theory]
    [InlineData("cmd.exe", "/k cd C:/codex-notes")]
    [InlineData("cmd.exe", "/k run-codex-tests.bat")]
    [InlineData("codexify.exe", null)]
    public void Resolve_DoesNotMatchSubstrings(string exe, string? args)
    {
        Assert.Equal("", ReducedMotionArguments.Resolve(exe, args));
    }

    // ── appending ──

    [Fact]
    public void Append_Disabled_LeavesArgumentsAlone()
    {
        Assert.Equal("-NoExit -Command codex",
            ReducedMotionArguments.Append("-NoExit -Command codex", "powershell.exe", enabled: false));
    }

    [Fact]
    public void Append_Enabled_AddsTheFlag()
    {
        Assert.Equal("-NoExit -Command codex " + ReducedMotionArguments.CodexFlag,
            ReducedMotionArguments.Append("-NoExit -Command codex", "powershell.exe", enabled: true));
    }

    [Fact]
    public void Append_Enabled_OnAnUnknownCli_ChangesNothing()
    {
        Assert.Equal("-NoExit -Command claude",
            ReducedMotionArguments.Append("-NoExit -Command claude", "pwsh.exe", enabled: true));
    }

    [Fact]
    public void Append_ToEmptyArguments_IsJustTheFlag()
    {
        Assert.Equal(ReducedMotionArguments.CodexFlag,
            ReducedMotionArguments.Append(null, "codex.cmd", enabled: true));
        Assert.Equal(ReducedMotionArguments.CodexFlag,
            ReducedMotionArguments.Append("   ", "codex.cmd", enabled: true));
    }

    /// <summary>Someone who already typed the flag by hand should not get it twice.</summary>
    [Fact]
    public void Append_DoesNotDuplicate()
    {
        var already = "-NoExit -Command codex " + ReducedMotionArguments.CodexFlag;
        Assert.Equal(already, ReducedMotionArguments.Append(already, "powershell.exe", enabled: true));
    }

    [Fact]
    public void Append_IsIdempotent()
    {
        var once = ReducedMotionArguments.Append("-Command codex", "powershell.exe", true);
        var twice = ReducedMotionArguments.Append(once, "powershell.exe", true);
        Assert.Equal(once, twice);
    }
}
