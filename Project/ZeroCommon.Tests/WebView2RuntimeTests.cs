using Agent.Common.Platform;

namespace ZeroCommon.Tests;

public class WebView2RuntimeTests
{
    private static WebView2Runtime.RegistryReader Registry(Dictionary<(bool, string), string?> values) =>
        (machine, key, name) => name == "pv" && values.TryGetValue((machine, key), out var v) ? v : null;

    private const string Machine64 = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + WebView2Runtime.ClientId;
    private const string User = @"Software\Microsoft\EdgeUpdate\Clients\" + WebView2Runtime.ClientId;

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0.0.0.0", false)]   // Microsoft: an uninstalled runtime can leave this behind
    [InlineData("garbage", false)]
    [InlineData("130.0.2849.80", true)]
    public void UsableVersion_FollowsMicrosoftsRule(string? pv, bool expected) =>
        Assert.Equal(expected, WebView2Runtime.IsUsableVersion(pv));

    [Fact]
    public void Probe_PerMachineInstall_IsFound()
    {
        var s = WebView2Runtime.Probe(Registry(new() { [(true, Machine64)] = "130.0.2849.80" }), _ => null);
        Assert.True(s.Installed);
        Assert.Equal("130.0.2849.80", s.Version);
        Assert.Equal("machine", s.Scope);
    }

    [Fact]
    public void Probe_PerUserInstall_IsFound_WhenMachineKeyHoldsZeros()
    {
        var s = WebView2Runtime.Probe(Registry(new()
        {
            [(true, Machine64)] = "0.0.0.0",
            [(false, User)] = "129.0.1.2",
        }), _ => null);
        Assert.True(s.Installed);
        Assert.Equal("user", s.Scope);
    }

    [Fact]
    public void Probe_NoKeys_IsMissing()
    {
        var s = WebView2Runtime.Probe(Registry(new()), _ => null);
        Assert.False(s.Installed);
        Assert.Equal("none", s.Scope);
    }

    [Fact]
    public void Probe_ReaderThrows_IsMissingNotCrash()
    {
        var s = WebView2Runtime.Probe((_, _, _) => throw new UnauthorizedAccessException(), _ => null);
        Assert.False(s.Installed);
    }

    [Fact]
    public void Probe_TestSwitch_ForcesMissing()
    {
        var s = WebView2Runtime.Probe(Registry(new() { [(true, Machine64)] = "130.0.2849.80" }),
            name => name == WebView2Runtime.ForceMissingVariable ? "1" : null);
        Assert.False(s.Installed);
        Assert.Equal("forced-missing", s.Scope);
    }
}
