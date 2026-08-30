using Agent.Common.Music;
using Xunit;

namespace ZeroCommon.Tests;

[Trait("Category", "YouTubePlaylist")]
public sealed class YouTubePlaylistRulesTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("_-Ab12CD34e", true)]
    [InlineData("short", false)]
    [InlineData("toolongvideoid00", false)]
    [InlineData("has space11", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidVideoId(string? id, bool expected)
        => Assert.Equal(expected, YouTubePlaylistRules.IsValidVideoId(id));

    [Theory]
    [InlineData("K-Pop", "K-Pop")]
    [InlineData("재즈", "재즈")]
    [InlineData("록", "록")]
    [InlineData("nonsense", "기타")]
    [InlineData("", "기타")]
    [InlineData(null, "기타")]
    public void NormalizeCategory_clamps_to_allowed_set(string? input, string expected)
        => Assert.Equal(expected, YouTubePlaylistRules.NormalizeCategory(input));

    [Theory]
    [InlineData("llm", "llm")]
    [InlineData("keyword", "keyword")]
    [InlineData("something-else", "keyword")]
    [InlineData("", "keyword")]
    [InlineData(null, "keyword")]
    public void NormalizeCategoryBy_only_llm_or_keyword(string? input, string expected)
        => Assert.Equal(expected, YouTubePlaylistRules.NormalizeCategoryBy(input));

    [Fact]
    public void Categories_match_the_plugin_contract()
    {
        // Must stay in lock-step with agent-band.js YT_CATEGORIES.
        Assert.Equal(
            new[] { "재즈", "K-Pop", "클래식", "힙합", "EDM", "발라드", "록", "OST", "기타" },
            YouTubePlaylistRules.Categories);
        Assert.Equal(60, YouTubePlaylistRules.MaxItems);
    }
}
