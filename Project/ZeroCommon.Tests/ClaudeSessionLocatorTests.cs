using System;
using System.IO;
using System.Threading;
using Agent.Common.Agents;

namespace ZeroCommon.Tests;

/// <summary>Headless tests for Claude session discovery (herdr-adoption H3).</summary>
[Trait("Category", "ClaudeSession")]
public sealed class ClaudeSessionLocatorTests : IDisposable
{
    private readonly string _home;

    public ClaudeSessionLocatorTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "aztest-claude-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
    }

    [Theory]
    [InlineData(@"C:\code\psmon\CodeScan", "C--code-psmon-CodeScan")]
    [InlineData("/home/u/proj", "-home-u-proj")]
    public void Slug_MatchesClaudeScheme(string cwd, string expected)
        => Assert.Equal(expected, ClaudeSessionLocator.Slug(cwd));

    [Fact]
    public void FindLatestSessionId_PicksNewest()
    {
        var cwd = @"C:\code\proj";
        var dir = Path.Combine(_home, ".claude", "projects", ClaudeSessionLocator.Slug(cwd));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "old-session.jsonl"), "{}");
        Thread.Sleep(20);
        File.WriteAllText(Path.Combine(dir, "new-session.jsonl"), "{}");

        Assert.Equal("new-session", ClaudeSessionLocator.FindLatestSessionId(cwd, _home));
    }

    [Fact]
    public void FindLatestSessionId_AcrossProfiles()
    {
        var cwd = @"C:\x";
        var slug = ClaudeSessionLocator.Slug(cwd);
        var d1 = Path.Combine(_home, ".claude", "projects", slug);
        var d2 = Path.Combine(_home, ".claude-work", "projects", slug);
        Directory.CreateDirectory(d1);
        Directory.CreateDirectory(d2);
        File.WriteAllText(Path.Combine(d1, "a.jsonl"), "{}");
        Thread.Sleep(20);
        File.WriteAllText(Path.Combine(d2, "b.jsonl"), "{}"); // newer, different profile
        Assert.Equal("b", ClaudeSessionLocator.FindLatestSessionId(cwd, _home));
    }

    [Fact]
    public void FindLatestSessionId_NoneReturnsNull()
        => Assert.Null(ClaudeSessionLocator.FindLatestSessionId(@"C:\nope", _home));

    [Fact]
    public void BuildResumeCommand_ProducesClaudeResume()
    {
        var cwd = @"C:\code\proj2";
        var dir = Path.Combine(_home, ".claude", "projects", ClaudeSessionLocator.Slug(cwd));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sess42.jsonl"), "{}");
        Assert.Equal("claude --resume sess42", ClaudeSessionLocator.BuildResumeCommand(cwd, _home));
    }
}
