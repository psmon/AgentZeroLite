using System.Linq;
using Agent.Common.Module;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for the `git worktree list --porcelain` parser (missions W4/W7).
/// </summary>
[Trait("Category", "GitWorktree")]
public sealed class GitWorktreeBuilderTests
{
    private const string Porcelain =
        "worktree /home/u/repo\n" +
        "HEAD abc123\n" +
        "branch refs/heads/main\n" +
        "\n" +
        "worktree /home/u/repo-feature\n" +
        "HEAD def456\n" +
        "branch refs/heads/feature/x\n" +
        "\n" +
        "worktree /home/u/repo-detached\n" +
        "HEAD 999aaa\n" +
        "detached\n";

    [Fact]
    public void Parse_ExtractsAllWorktrees()
    {
        var list = GitWorktreeBuilder.ParseWorktreeList(Porcelain);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void Parse_StripsRefsHeadsFromBranch()
    {
        var list = GitWorktreeBuilder.ParseWorktreeList(Porcelain);
        Assert.Equal("main", list[0].Branch);
        Assert.Equal("feature/x", list[1].Branch);
    }

    [Fact]
    public void Parse_MarksDetached()
    {
        var list = GitWorktreeBuilder.ParseWorktreeList(Porcelain);
        Assert.True(list[2].Detached);
        Assert.Equal("", list[2].Branch);
        Assert.Equal("999aaa", list[2].Head);
    }

    [Fact]
    public void Parse_HandlesBare()
    {
        var list = GitWorktreeBuilder.ParseWorktreeList("worktree /repo\nbare\n");
        Assert.Single(list);
        Assert.True(list[0].Bare);
    }

    [Fact]
    public void Parse_Empty_ReturnsNone()
    {
        Assert.Empty(GitWorktreeBuilder.ParseWorktreeList(""));
    }

    [Fact]
    public void Parse_TrailingRecordWithoutBlankLine_StillCaptured()
    {
        // git omits the trailing blank line on the last record.
        var list = GitWorktreeBuilder.ParseWorktreeList("worktree /a\nHEAD x\nbranch refs/heads/b");
        Assert.Single(list);
        Assert.Equal("/a", list[0].Path);
        Assert.Equal("b", list[0].Branch);
    }
}
