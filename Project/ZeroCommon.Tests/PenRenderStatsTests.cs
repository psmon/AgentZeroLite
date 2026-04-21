namespace ZeroCommon.Tests;

public class PenRenderStatsTests
{
    [Fact]
    public void CountType_IncrementsCounters()
    {
        var stats = new PenRenderStats();
        stats.CountType("Rectangle");
        stats.CountType("Rectangle");
        stats.CountType("Text");

        Assert.Equal(3, stats.TotalElements);
        Assert.Equal(2, stats.TypeCounts["Rectangle"]);
        Assert.Equal(1, stats.TypeCounts["Text"]);
    }

    [Fact]
    public void Error_AddsToErrorList()
    {
        var stats = new PenRenderStats();
        stats.Error("Circle", "circle1", "radius invalid");

        Assert.Equal(1, stats.ErrorElements);
        Assert.Single(stats.Errors);
        Assert.Contains("radius invalid", stats.Errors[0]);
    }

    [Fact]
    public void Skip_AddsToSkippedList()
    {
        var stats = new PenRenderStats();
        stats.Skip("Image", "img1", "unsupported format");

        Assert.Equal(1, stats.SkippedElements);
        Assert.Contains("unsupported", stats.Skipped[0]);
    }
}
