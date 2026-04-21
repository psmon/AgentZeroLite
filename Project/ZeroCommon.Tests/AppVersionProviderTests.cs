namespace ZeroCommon.Tests;

public class AppVersionProviderTests
{
    [Fact]
    public void GetDisplayVersion_ReturnsNonEmpty()
    {
        var version = AppVersionProvider.GetDisplayVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
    }
}
