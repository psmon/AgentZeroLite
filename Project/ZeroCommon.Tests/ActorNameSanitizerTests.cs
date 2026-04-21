namespace ZeroCommon.Tests;

public class ActorNameSanitizerTests
{
    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("my-actor_1", "my-actor_1")]
    [InlineData("", "_")]
    public void Safe_AsciiOnly_PassesThrough(string input, string expected)
    {
        Assert.Equal(expected, ActorNameSanitizer.Safe(input));
    }

    [Fact]
    public void Safe_Korean_ReplacesAndAppendsHash()
    {
        var result = ActorNameSanitizer.Safe("한글테스트");
        Assert.DoesNotContain("한", result);
        Assert.Contains("-", result);
    }

    [Fact]
    public void Safe_SpecialChars_ReplacesAndAppendsHash()
    {
        var result = ActorNameSanitizer.Safe("a/b@c");
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("@", result);
    }
}
