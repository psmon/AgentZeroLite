using Agent.Common.Llm.Tools;

namespace ZeroCommon.Tests;

/// <summary>
/// Pure-CPU unit tests for <see cref="ExternalAgentLoop"/>. The class
/// has one piece of non-trivial standalone logic — pulling a balanced JSON
/// object out of noisy model output — and that's what we test here. The
/// loop itself needs a network endpoint and lives behind the online smoke
/// suite (<see cref="WebnoriExternalSmokeTests"/>).
/// </summary>
public sealed class ExternalAgentLoopTests
{
    [Fact]
    public void ExtractFirstJsonObject_returns_clean_object_when_input_is_pure_json()
    {
        var raw = """{"tool":"done","args":{"message":"hi"}}""";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.Equal(raw, extracted);
    }

    [Fact]
    public void ExtractFirstJsonObject_skips_leading_prose()
    {
        var raw = "Sure, here's the call:\n```json\n{\"tool\":\"done\",\"args\":{\"message\":\"ok\"}}\n```";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.NotNull(extracted);
        Assert.StartsWith("{", extracted);
        Assert.EndsWith("}", extracted);
        Assert.Contains("\"tool\":\"done\"", extracted);
    }

    [Fact]
    public void ExtractFirstJsonObject_handles_nested_objects()
    {
        var raw = "padding {\"tool\":\"x\",\"args\":{\"nested\":{\"deep\":1}}} more text";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.Equal("{\"tool\":\"x\",\"args\":{\"nested\":{\"deep\":1}}}", extracted);
    }

    [Fact]
    public void ExtractFirstJsonObject_ignores_braces_inside_strings()
    {
        var raw = "{\"tool\":\"send\",\"args\":{\"text\":\"hello { world } end\"}}";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.Equal(raw, extracted);
    }

    [Fact]
    public void ExtractFirstJsonObject_returns_null_when_unterminated()
    {
        var raw = "{\"tool\":\"x\",\"args\":{";
        Assert.Null(ExternalAgentLoop.ExtractFirstJsonObject(raw));
    }

    [Fact]
    public void ExtractFirstJsonObject_returns_null_when_no_brace()
    {
        Assert.Null(ExternalAgentLoop.ExtractFirstJsonObject("just prose, nothing structural"));
    }

    [Fact]
    public void ExtractFirstJsonObject_handles_escaped_quote_inside_string()
    {
        // The string contains an escaped quote followed by what looks like a closing
        // brace inside the string — the parser must NOT terminate early.
        var raw = "{\"tool\":\"x\",\"args\":{\"text\":\"escaped \\\"} fake close\"}}";
        var extracted = ExternalAgentLoop.ExtractFirstJsonObject(raw);
        Assert.Equal(raw, extracted);
    }
}
