using System.Text.Json;
using Agent.Common.Remote;
using Xunit;

namespace ZeroCommon.Tests.Remote;

/// <summary>
/// Shape tests for <see cref="RemoteSettings"/> — defaults and JSON round-trip. Uses
/// in-memory serialization (not <see cref="RemoteSettingsStore"/>, which targets the real
/// user file) so the test never touches the operator's settings.
/// </summary>
public sealed class RemoteSettingsTests
{
    [Fact]
    public void Defaults_match_product_decisions()
    {
        var s = new RemoteSettings();
        Assert.False(s.Enabled);            // opt-in
        Assert.Equal(8787, s.Port);
        Assert.Equal("0.0.0.0", s.BindAddress); // LAN by default
        Assert.Equal(3, s.MaxConnections);      // default cap 3
        Assert.Empty(s.PairedTokenHashes);
    }

    [Fact]
    public void Json_round_trip_preserves_all_fields()
    {
        var s = new RemoteSettings
        {
            Enabled = true,
            Port = 9001,
            BindAddress = "127.0.0.1",
            MaxConnections = 5,
            DefaultTarget = "0:1",
        };
        s.PairedTokenHashes.Add("abc123");
        s.PairedTokenHashes.Add("def456");

        var json = JsonSerializer.Serialize(s);
        var loaded = JsonSerializer.Deserialize<RemoteSettings>(json)!;

        Assert.True(loaded.Enabled);
        Assert.Equal(9001, loaded.Port);
        Assert.Equal("127.0.0.1", loaded.BindAddress);
        Assert.Equal(5, loaded.MaxConnections);
        Assert.Equal("0:1", loaded.DefaultTarget);
        Assert.Equal(new[] { "abc123", "def456" }, loaded.PairedTokenHashes);
    }
}
