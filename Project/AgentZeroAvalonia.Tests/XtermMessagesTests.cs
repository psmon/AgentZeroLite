using System.Text;
using System.Text.Json;
using AgentZeroAvalonia.Terminal;
using Xunit;

namespace AgentZeroAvalonia.Tests;

public class XtermMessagesTests
{
    [Fact]
    public void Chunks_never_split_a_code_point_and_reassemble_exactly()
    {
        var text = string.Concat(Enumerable.Repeat("한글 ABC 日本語 \x1b[31m✓\x1b[0m\r\n", 200));
        var chunks = XtermMessages.ChunkUtf8Base64(text, maxBytes: 100).ToList();

        Assert.True(chunks.Count > 10);
        var sb = new StringBuilder();
        foreach (var c in chunks)
        {
            var bytes = Convert.FromBase64String(c);
            Assert.True(bytes.Length <= 100);
            // Each chunk alone must decode without a replacement character.
            var piece = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            sb.Append(piece);
        }
        Assert.Equal(text, sb.ToString());
    }

    [Fact]
    public void Small_text_is_one_chunk_and_empty_is_none()
    {
        Assert.Single(XtermMessages.ChunkUtf8Base64("hi"));
        Assert.Empty(XtermMessages.ChunkUtf8Base64(""));
    }

    [Fact]
    public void Recv_script_is_a_single_line_safe_for_a_script_literal()
    {
        // U+2028 / U+2029 are line terminators to a JS parser; built at runtime so the
        // source file itself never carries them.
        var ls = new string((char)0x2028, 1);
        var ps = new string((char)0x2029, 1);
        var script = XtermMessages.BuildRecvScript(new { type = "out", data = "line" + ls + "para" + ps + " </script> \"q\" " + (char)0xD55C });
        Assert.StartsWith("window.zeroHost&&window.zeroHost.recv({", script);
        Assert.DoesNotContain(ls, script);
        Assert.DoesNotContain(ps, script);
        Assert.DoesNotContain("</script>", script);
        Assert.DoesNotContain(new string((char)10, 1), script);
        Assert.DoesNotContain(new string((char)13, 1), script);
    }

    [Fact]
    public void Inbound_accepts_object_and_double_encoded_string()
    {
        Assert.True(XtermMessages.TryParseInbound("{\"type\":\"in\",\"data\":\"x\"}", out var direct));
        Assert.Equal("in", XtermMessages.Type(direct));
        Assert.Equal("x", XtermMessages.Str(direct, "data"));

        var wrapped = JsonSerializer.Serialize("{\"type\":\"resize\",\"cols\":120,\"rows\":40}");
        Assert.True(XtermMessages.TryParseInbound(wrapped, out var inner));
        Assert.Equal("resize", XtermMessages.Type(inner));
        Assert.Equal(120, XtermMessages.Int(inner, "cols", 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    public void Inbound_rejects_garbage(string? body)
    {
        Assert.False(XtermMessages.TryParseInbound(body, out _));
    }

    [Fact]
    public void Out64_batch_carries_every_chunk_in_one_script()
    {
        var chunks = XtermMessages.ChunkUtf8Base64(new string('x', 200), maxBytes: 64).ToArray();
        Assert.Equal(4, chunks.Length);
        var script = XtermMessages.BuildRecvScript(new { type = "out64", data = chunks });
        Assert.StartsWith("window.zeroHost&&window.zeroHost.recv({", script);
        Assert.Contains("\"data\":[", script);
        foreach (var c in chunks) Assert.Contains(c, script);
    }
}
