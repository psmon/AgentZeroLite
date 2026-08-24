using System.Text.Json;
using Agent.Common.Remote;
using Xunit;

namespace ZeroCommon.Tests.Remote;

/// <summary>Wire-format tests for <see cref="RemoteProtocol"/> — server frame building and
/// client frame parsing.</summary>
public sealed class RemoteProtocolTests
{
    [Fact]
    public void Output_frame_is_well_formed_and_escapes_data()
    {
        var frame = RemoteProtocol.Output("line\n\"q\"");
        using var doc = JsonDocument.Parse(frame); // must be valid JSON
        Assert.Equal("output", doc.RootElement.GetProperty("t").GetString());
        Assert.Equal("line\n\"q\"", doc.RootElement.GetProperty("d").GetString());
    }

    [Fact]
    public void Snapshot_and_info_and_error_carry_their_type()
    {
        Assert.Equal("snapshot", TypeOf(RemoteProtocol.Snapshot("x")));
        Assert.Equal("info", TypeOf(RemoteProtocol.Info("x")));
        Assert.Equal("error", TypeOf(RemoteProtocol.Error("x")));

        static string TypeOf(string frame)
        {
            using var doc = JsonDocument.Parse(frame);
            return doc.RootElement.GetProperty("t").GetString()!;
        }
    }

    [Fact]
    public void Terminals_embeds_raw_list_json()
    {
        var frame = RemoteProtocol.Terminals("{\"groups\":[]}");
        using var doc = JsonDocument.Parse(frame);
        Assert.Equal("terminals", doc.RootElement.GetProperty("t").GetString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("list").ValueKind);
    }

    [Fact]
    public void Parse_input_frame()
    {
        Assert.True(RemoteProtocol.TryParseClient("{\"t\":\"input\",\"d\":\"ls\\r\"}", out var msg));
        Assert.Equal("input", msg.Type);
        Assert.Equal("ls\r", msg.Data);
    }

    [Fact]
    public void Parse_attach_frame_with_group_and_tab()
    {
        Assert.True(RemoteProtocol.TryParseClient("{\"t\":\"attach\",\"group\":1,\"tab\":2}", out var msg));
        Assert.Equal("attach", msg.Type);
        Assert.Equal(1, msg.Group);
        Assert.Equal(2, msg.Tab);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]              // missing type
    [InlineData("[1,2,3]")]         // not an object
    public void Malformed_frames_are_rejected(string json)
    {
        Assert.False(RemoteProtocol.TryParseClient(json, out _));
    }
}
