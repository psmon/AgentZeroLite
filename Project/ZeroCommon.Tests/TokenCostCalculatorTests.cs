using System.Linq;
using Agent.Common.Data.Entities;
using Agent.Common.Telemetry;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for the token→USD cost layer (mission W9, orca-adoption).
/// </summary>
[Trait("Category", "TokenCost")]
public sealed class TokenCostCalculatorTests
{
    private static TokenUsageRecord Rec(string model, long input, long output, long cacheCreate = 0, long cacheRead = 0)
        => new() { Model = model, InputTokens = input, OutputTokens = output, CacheCreateTokens = cacheCreate, CacheReadTokens = cacheRead };

    [Theory]
    [InlineData("claude-opus-4-8", "opus")]
    [InlineData("claude-sonnet-5", "sonnet")]
    [InlineData("claude-haiku-4-5", "haiku")]
    [InlineData("gpt-4o-mini", "gpt-4o")]
    public void Lookup_MatchesModelFamilyBySubstring(string model, string expectedKey)
    {
        var p = TokenCostCalculator.Lookup(model);
        var expected = TokenCostCalculator.DefaultTable.First(t => t.Key == expectedKey).Pricing;
        Assert.Equal(expected, p);
    }

    [Fact]
    public void Lookup_UnknownModel_ReturnsNull()
    {
        Assert.Null(TokenCostCalculator.Lookup("some-random-model"));
    }

    [Fact]
    public void CostUsd_PricesEachTokenClassSeparately()
    {
        // 1M input + 1M output on sonnet = 3.00 + 15.00 = 18.00
        var r = Rec("claude-sonnet-5", 1_000_000, 1_000_000);
        Assert.Equal(18.00m, TokenCostCalculator.CostUsd(r));
    }

    [Fact]
    public void CostUsd_CacheReadIsCheaperThanInput()
    {
        var fresh = Rec("claude-sonnet-5", 1_000_000, 0);
        var cached = Rec("claude-sonnet-5", 0, 0, cacheRead: 1_000_000);
        Assert.True(TokenCostCalculator.CostUsd(cached) < TokenCostCalculator.CostUsd(fresh));
    }

    [Fact]
    public void CostUsd_UnknownModel_IsZero()
    {
        Assert.Equal(0m, TokenCostCalculator.CostUsd(Rec("mystery", 1_000_000, 1_000_000)));
    }

    [Fact]
    public void TotalUsd_SumsAcrossRecords()
    {
        var records = new[]
        {
            Rec("claude-sonnet-5", 1_000_000, 0),   // 3.00
            Rec("claude-haiku-4-5", 1_000_000, 0),  // 0.80
            Rec("unknown", 5_000_000, 0),           // 0
        };
        Assert.Equal(3.80m, TokenCostCalculator.TotalUsd(records));
    }

    [Fact]
    public void ByModel_GroupsAndSortsByCostDescending()
    {
        var records = new[]
        {
            Rec("claude-haiku-4-5", 1_000_000, 0),  // 0.80
            Rec("claude-opus-4-8", 1_000_000, 0),   // 15.00
            Rec("claude-opus-4-8", 1_000_000, 0),   // 15.00
        };
        var by = TokenCostCalculator.ByModel(records);
        Assert.Equal("claude-opus-4-8", by[0].Model);
        Assert.Equal(30.00m, by[0].Usd);
        Assert.Equal(2, by[0].Records);
    }
}
