using Agent.Common.Mp3;
using Xunit;

namespace ZeroCommon.Tests.Mp3;

// The instrument CSV is written by the plugin over the bridge while a track
// plays (M0029 확장) — this clamp is the only thing between arbitrary JS
// input and the DB column, so junk rejection is the contract under test.
public sealed class Mp3InstrumentSetTests
{
    [Fact]
    public void Merge_UnionsDedupesAndSorts()
        => Assert.Equal("drum,piano,violin",
            Mp3InstrumentSet.Merge("piano,violin", new[] { "drum", "piano" }));

    [Fact]
    public void Merge_EmptyExisting_TakesNewKeys()
        => Assert.Equal("piano,vocal", Mp3InstrumentSet.Merge("", new[] { "vocal", "piano" }));

    [Fact]
    public void Merge_RejectsJunkKeys()
        // 대문자/공백/특수문자/과길이 — 정규화 통과분만 저장
        => Assert.Equal("piano",
            Mp3InstrumentSet.Merge(null, new[]
            {
                "PIANO",                      // normalized to lowercase → kept
                "pia no",                     // space → rejected
                "<script>alert(1)</script>",  // markup → rejected
                new string('x', 40),          // over-length → rejected
                "",                           // empty → rejected
            }));

    [Fact]
    public void Merge_CapsAt24Keys()
    {
        var many = Enumerable.Range(0, 40).Select(i => $"inst{i:D2}");
        var merged = Mp3InstrumentSet.Parse(Mp3InstrumentSet.Merge("", many));
        Assert.Equal(24, merged.Count);
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(Mp3InstrumentSet.Parse(null));
        Assert.Empty(Mp3InstrumentSet.Parse(""));
        Assert.Empty(Mp3InstrumentSet.Parse(" , ,, "));
    }
}
