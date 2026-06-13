namespace TotalRecall.Web.Tests;

using TotalRecall.Web.Security;
using Xunit;

public sealed class LocalAuthTests
{
    [Fact]
    public void GenerateToken_IsUrlSafe_AndLongEnough()
    {
        var t = LocalAuth.GenerateToken();
        Assert.True(t.Length >= 32);
        Assert.DoesNotContain('+', t);
        Assert.DoesNotContain('/', t);
        Assert.DoesNotContain('=', t);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:5577")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:5577")]
    [InlineData("[::1]:5577")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    public void IsAllowedHost_True_ForLoopback(string host) =>
        Assert.True(LocalAuth.IsAllowedHost(host, allowedHost: "127.0.0.1"));

    [Theory]
    [InlineData("evil.com")]
    [InlineData("169.254.169.254")]
    [InlineData("")]
    public void IsAllowedHost_False_ForNonLoopback(string host) =>
        Assert.False(LocalAuth.IsAllowedHost(host, allowedHost: "127.0.0.1"));

    [Fact]
    public void IsAllowedHost_True_WhenExplicitHostMatches()
    {
        Assert.True(LocalAuth.IsAllowedHost("0.0.0.0:5577", allowedHost: "0.0.0.0"));
    }

    [Theory]
    [InlineData("192.168.1.50:5577")]
    [InlineData("10.0.0.5")]
    [InlineData("example.local")]
    public void IsAllowedHost_True_ForAnyHost_WhenBoundToWildcard(string host)
    {
        Assert.True(LocalAuth.IsAllowedHost(host, allowedHost: "0.0.0.0"));
    }

    [Fact]
    public void TokenMatches_IsConstantTimeEqual()
    {
        Assert.True(LocalAuth.TokenMatches("abc123", "abc123"));
        Assert.False(LocalAuth.TokenMatches("abc123", "abc124"));
        Assert.False(LocalAuth.TokenMatches("abc123", null));
    }

    [Fact]
    public void GenerateToken_ReturnsDistinctValues()
    {
        Assert.NotEqual(LocalAuth.GenerateToken(), LocalAuth.GenerateToken());
    }
}
