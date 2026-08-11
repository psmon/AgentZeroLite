using System;
using Agent.Common.Automations;

namespace ZeroCommon.Tests;

/// <summary>
/// Headless tests for scheduled-automation schedule math.
/// </summary>
[Trait("Category", "Automation")]
public sealed class AutomationScheduleTests
{
    private static readonly DateTime Base =
        new(2026, 8, 11, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Interval_Minutes()
    {
        Assert.True(AutomationSchedule.TryComputeNext("every 30m", Base, out var next, out _));
        Assert.Equal(Base.AddMinutes(30), next);
    }

    [Fact]
    public void Interval_Hours()
    {
        Assert.True(AutomationSchedule.TryComputeNext("every 2h", Base, out var next, out _));
        Assert.Equal(Base.AddHours(2), next);
    }

    [Fact]
    public void Hourly_NextTopOfHour()
    {
        Assert.True(AutomationSchedule.TryComputeNext("hourly", Base, out var next, out _));
        Assert.Equal(new DateTime(2026, 8, 11, 11, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Daily_LaterToday()
    {
        Assert.True(AutomationSchedule.TryComputeNext("daily 14:00", Base, out var next, out _));
        Assert.Equal(new DateTime(2026, 8, 11, 14, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Daily_AlreadyPast_RollsToTomorrow()
    {
        Assert.True(AutomationSchedule.TryComputeNext("daily 09:00", Base, out var next, out _));
        Assert.Equal(new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData("")]
    [InlineData("every")]
    [InlineData("every 0m")]
    [InlineData("every 5x")]
    [InlineData("daily 25:00")]
    [InlineData("nonsense")]
    public void Invalid_ReturnsFalse(string spec)
    {
        Assert.False(AutomationSchedule.TryComputeNext(spec, Base, out _, out var err));
        Assert.NotEqual("", err);
    }

    [Fact]
    public void IsDue()
    {
        Assert.True(AutomationSchedule.IsDue(Base, Base.AddSeconds(1)));
        Assert.False(AutomationSchedule.IsDue(Base, Base.AddSeconds(-1)));
    }
}
