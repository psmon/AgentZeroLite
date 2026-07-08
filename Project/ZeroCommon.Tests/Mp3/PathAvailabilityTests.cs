using Agent.Common.Mp3;
using Xunit;

namespace ZeroCommon.Tests.Mp3;

// Headless coverage for the timeout-bounded scan-root probe that fixes the
// disconnected-network-drive launch hang. The dead-drive SMB timeout itself is
// not reproducible headlessly, so these cover the fast, deterministic paths:
// null/empty, a real local dir (present), and a missing path (absent) — plus
// the fact that a healthy local dir answers well inside the timeout budget.
public sealed class PathAvailabilityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DirectoryExistsFast_NullOrBlank_IsFalse(string? path)
        => Assert.False(PathAvailability.DirectoryExistsFast(path));

    [Fact]
    public void DirectoryExistsFast_ExistingLocalDir_IsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "azl-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PathAvailability.Invalidate(dir);            // avoid a stale cache hit
            Assert.True(PathAvailability.DirectoryExistsFast(dir));
        }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public void DirectoryExistsFast_MissingLocalPath_IsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "azl-missing-" + Guid.NewGuid().ToString("N"));
        PathAvailability.Invalidate(dir);
        Assert.False(PathAvailability.DirectoryExistsFast(dir));
    }

    [Fact]
    public void DirectoryExistsFast_HealthyLocalDir_ReturnsWithinBudget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "azl-fast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PathAvailability.Invalidate(dir);
            var start = Environment.TickCount64;
            _ = PathAvailability.DirectoryExistsFast(dir, timeoutMs: 1200);
            var elapsed = Environment.TickCount64 - start;
            // A live local dir must answer far inside the budget — the whole
            // point is that only a dead path is allowed to consume the timeout.
            Assert.True(elapsed < 1000, $"probe took {elapsed}ms");
        }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public void Invalidate_ForcesReprobe_AfterStateChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "azl-inval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        PathAvailability.Invalidate(dir);
        Assert.True(PathAvailability.DirectoryExistsFast(dir));   // caches true

        Directory.Delete(dir);
        PathAvailability.Invalidate(dir);                         // drop cached true
        Assert.False(PathAvailability.DirectoryExistsFast(dir));  // re-probed → gone
    }
}
