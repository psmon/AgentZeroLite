using System.Collections;
using Agent.Common.Services;

namespace ZeroCommon.Tests;

/// <summary>
/// What a terminal tab's child process inherits. Written after every terminal
/// rendered monochrome: the app had been launched from a session with NO_COLOR=1
/// and passed it to every tab, so each CLI correctly turned its own colour off.
/// </summary>
public class TerminalEnvironmentTests
{
    private static Hashtable Source(params (string Key, string Value)[] pairs)
    {
        var t = new Hashtable();
        foreach (var (k, v) in pairs) t[k] = v;
        return t;
    }

    /// <summary>The bug, as a test.</summary>
    [Fact]
    public void Build_DropsNoColor()
    {
        var env = TerminalEnvironment.Build(Source(("NO_COLOR", "1"), ("PATH", "C:/bin")));

        Assert.False(env.ContainsKey("NO_COLOR"));
        Assert.Equal("C:/bin", env["PATH"]);
    }

    [Fact]
    public void Build_AdvertisesTrueColour()
    {
        var env = TerminalEnvironment.Build(Source());

        Assert.Equal("truecolor", env["COLORTERM"]);
        Assert.Equal(TerminalEnvironment.TermProgram, env["TERM_PROGRAM"]);
    }

    [Fact]
    public void Build_OverridesAnotherEmulatorsIdentity()
    {
        var env = TerminalEnvironment.Build(Source(
            ("TERM_PROGRAM", "vscode"),
            ("TERM_PROGRAM_VERSION", "1.0"),
            ("TERMINAL_EMULATOR", "JetBrains-JediTerm"),
            ("TERM_SESSION_ID", "abc"),
            ("FIG_TERM", "1")));

        Assert.Equal(TerminalEnvironment.TermProgram, env["TERM_PROGRAM"]);
        Assert.False(env.ContainsKey("TERM_PROGRAM_VERSION"));
        Assert.False(env.ContainsKey("TERMINAL_EMULATOR"));
        Assert.False(env.ContainsKey("TERM_SESSION_ID"));
        Assert.False(env.ContainsKey("FIG_TERM"));
    }

    /// <summary>
    /// A tab the user opened is a fresh session, not a child of whatever launched
    /// the GUI — the visible tell was tabs reporting transcript saving off.
    /// </summary>
    [Fact]
    public void Build_DropsNestedAgentMarkers()
    {
        var env = TerminalEnvironment.Build(Source(
            ("CLAUDE_CODE_CHILD_SESSION", "1"),
            ("CLAUDE_CODE_SESSION_ID", "abc"),
            ("CLAUDE_CODE_MESSAGING_TOKEN", "secret"),
            ("CLAUDECODE", "1"),
            ("CLAUDE_PID", "123"),
            ("CLAUDE_CONFIG_DIR", "keep-me")));

        Assert.DoesNotContain(env.Keys, k => k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase));
        Assert.False(env.ContainsKey("CLAUDECODE"));
        Assert.False(env.ContainsKey("CLAUDE_PID"));
        Assert.True(env.ContainsKey("CLAUDE_CONFIG_DIR"));   // not a session marker
    }

    [Fact]
    public void Build_DropsStaleIpcHandles()
    {
        var env = TerminalEnvironment.Build(Source(("NODE_CHANNEL_FD", "3"), ("BUN_WATCH_PID", "9")));

        Assert.False(env.ContainsKey("NODE_CHANNEL_FD"));
        Assert.False(env.ContainsKey("BUN_WATCH_PID"));
    }

    [Fact]
    public void ShouldDrop_IsCaseInsensitive()
    {
        Assert.True(TerminalEnvironment.ShouldDrop("no_color"));
        Assert.True(TerminalEnvironment.ShouldDrop("claude_code_session_id"));
        Assert.False(TerminalEnvironment.ShouldDrop("PATH"));
    }

    [Fact]
    public void Build_FromTheRealEnvironment_IsUsable()
    {
        var env = TerminalEnvironment.Build();

        Assert.False(env.ContainsKey("NO_COLOR"));
        Assert.Equal("truecolor", env["COLORTERM"]);
        Assert.NotEmpty(env);
    }

    // ── the Windows block format ──

    [Fact]
    public void ToBlock_IsNulSeparatedAndNulTerminated()
    {
        var block = TerminalEnvironment.ToBlock(new Dictionary<string, string>
        {
            ["A"] = "1",
            ["B"] = "2",
        }.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase));

        Assert.Equal("A=1\0B=2\0\0", block);
    }

    /// <summary>
    /// cmd.exe keeps per-drive current directories as "=C:" pseudo-variables. An
    /// entry whose name starts with '=' terminates the block early, silently
    /// truncating everything after it.
    /// </summary>
    [Fact]
    public void ToBlock_SkipsDriveCurrentDirectoryPseudoVariables()
    {
        var block = TerminalEnvironment.ToBlock(new[]
        {
            new KeyValuePair<string, string>("=C:", "C:/tmp"),
            new KeyValuePair<string, string>("PATH", "C:/bin"),
        });

        Assert.Equal("PATH=C:/bin\0\0", block);
    }

    [Fact]
    public void ToBlock_Empty_IsJustTheTerminator()
    {
        Assert.Equal("\0", TerminalEnvironment.ToBlock(Array.Empty<KeyValuePair<string, string>>()));
    }

    /// <summary>Mac App Store build: the sandbox points HOME at the container; a tab gets the real one.</summary>
    [Fact]
    public void BuildPosix_GivesATabTheRealHomeInsideTheAppSandbox()
    {
        var env = TerminalEnvironment.BuildPosix(Source(
            ("HOME", "/Users/me/Library/Containers/com.psmon.agentzerolite/Data"),
            ("APP_SANDBOX_CONTAINER_ID", "com.psmon.agentzerolite"),
            ("PATH", "/usr/bin:/bin")), canList: _ => true);

        Assert.Equal("/Users/me", env["HOME"]);
        Assert.Equal("/opt/homebrew/bin:/opt/homebrew/sbin:/usr/bin:/bin", env["PATH"]);
    }

    /// <summary>Without a home-wide exception the real home is unreadable: the tab keeps the container.</summary>
    [Fact]
    public void BuildPosix_KeepsTheContainerHomeWhenTheRealOneIsUnreadable()
    {
        const string container = "/Users/me/Library/Containers/com.psmon.agentzerolite/Data";
        var env = TerminalEnvironment.BuildPosix(Source(("HOME", container), ("PATH", "/usr/bin")), canList: _ => false);

        Assert.Equal(container, env["HOME"]);
        Assert.StartsWith("/opt/homebrew/bin:", env["PATH"]);
    }

    [Fact]
    public void BuildPosix_LeavesAnOrdinaryHomeAndPathAlone()
    {
        var env = TerminalEnvironment.BuildPosix(Source(("HOME", "/Users/me"), ("PATH", "/usr/bin")), canList: _ => true);

        Assert.Equal("/Users/me", env["HOME"]);
        Assert.Equal("/usr/bin", env["PATH"]);
    }

    [Theory]
    [InlineData("/Users/me", null)]
    [InlineData("/Users/me/Library/Containers/com.x.app/Data", "/Users/me")]
    [InlineData("/Users/me/Library/Containers/com.x.app/Data/", "/Users/me")]
    [InlineData("/Users/me/Library/Containers/com.x.app", null)]
    [InlineData("/Users/me/Library/Containers/com.x.app/Data/sub", null)]
    public void RealHomeFromSandbox_RecognisesOnlyAContainerHome(string home, string? expected)
        => Assert.Equal(expected, TerminalEnvironment.RealHomeFromSandbox(home));
}
