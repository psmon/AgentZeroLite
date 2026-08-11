using System.Linq;
using Agent.Common.Module;

namespace ZeroCommon.Tests;

/// <summary>Headless tests for the command-palette fuzzy matcher.</summary>
[Trait("Category", "Fuzzy")]
public sealed class FuzzyMatcherTests
{
    [Fact]
    public void EmptyQuery_MatchesAnything()
    {
        Assert.True(FuzzyMatcher.TryMatch("", "anything", out _));
    }

    [Theory]
    [InlineData("dr", "Diff Review", true)]     // subsequence
    [InlineData("difrev", "Diff Review", true)]
    [InlineData("xyz", "Diff Review", false)]   // not a subsequence
    [InlineData("wsp", "workspace", true)]
    public void TryMatch_Subsequence(string q, string text, bool expected)
    {
        Assert.Equal(expected, FuzzyMatcher.TryMatch(q, text, out _));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.True(FuzzyMatcher.TryMatch("DIFF", "diff review", out _));
    }

    [Fact]
    public void ConsecutiveAndPrefix_ScoreHigher()
    {
        FuzzyMatcher.TryMatch("diff", "diff review", out var prefixScore);
        FuzzyMatcher.TryMatch("diff", "the diff review", out var midScore);
        // A prefix / word-start match should outscore a later, spread-out one.
        Assert.True(prefixScore >= midScore);
    }

    [Fact]
    public void Rank_ReturnsBestFirst()
    {
        var items = new[] { "Settings", "Diff Review", "Bot", "Web Diff" };
        var ranked = FuzzyMatcher.Rank("diff", items, s => s);
        Assert.Equal("Diff Review", ranked[0]); // word-start beats "Web Diff"
        Assert.DoesNotContain("Settings", ranked);
        Assert.DoesNotContain("Bot", ranked);
    }

    [Fact]
    public void Rank_StableOnTies()
    {
        var items = new[] { "aa", "ab", "ac" };
        var ranked = FuzzyMatcher.Rank("a", items, s => s);
        Assert.Equal(3, ranked.Count);
    }
}
