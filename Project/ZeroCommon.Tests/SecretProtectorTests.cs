using Agent.Common.Platform;

namespace ZeroCommon.Tests;

public class SecretProtectorTests
{
    [Fact]
    public void Roundtrip_ReturnsOriginalPlaintext()
    {
        var sut = SecretProtector.Create();
        const string secret = "hunter2-비밀-🔐";

        var protectedValue = sut.Protect(secret);
        Assert.NotNull(protectedValue);
        Assert.NotEqual(secret, protectedValue);          // 평문이 그대로 노출되지 않음
        Assert.Contains(":", protectedValue);             // scheme 태그 포함

        var recovered = sut.Unprotect(protectedValue);
        Assert.Equal(secret, recovered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_NullOrEmpty_ReturnsNull(string? input)
    {
        var sut = SecretProtector.Create();
        Assert.Null(sut.Protect(input));
    }

    [Fact]
    public void Unprotect_Garbage_ReturnsNull()
    {
        var sut = SecretProtector.Create();
        Assert.Null(sut.Unprotect("not-a-valid-blob"));
    }
}
