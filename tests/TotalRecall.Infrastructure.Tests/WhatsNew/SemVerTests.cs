using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public class SemVerTests
{
    private static int Cmp(string a, string b)
    {
        Assert.True(SemVer.TryParse(a, out var pa), "parse " + a);
        Assert.True(SemVer.TryParse(b, out var pb), "parse " + b);
        return System.Math.Sign(SemVer.Compare(pa, pb));
    }

    [Theory]
    [InlineData("4.10.0", "4.9.0")]
    [InlineData("4.8.0", "4.8.0-beta.2")]
    [InlineData("4.8.0-beta.10", "4.8.0-beta.2")]
    [InlineData("4.8.0-rc.1", "4.8.0-beta.9")]
    [InlineData("4.8.0-beta.2", "4.8.0-beta")]
    [InlineData("4.8.0-beta", "4.8.0-1")]
    [InlineData("4.8.1", "4.8.0")]
    [InlineData("5.0.0", "4.99.99")]
    public void Compare_Greater_ReturnsPositiveAndReverseNegative(string higher, string lower)
    {
        Assert.Equal(1, Cmp(higher, lower));
        Assert.Equal(-1, Cmp(lower, higher));
    }

    [Theory]
    [InlineData("4.8.0", "4.8.0")]
    [InlineData("4.8.0+abc", "4.8.0")]
    [InlineData("4.8.0-beta.1+x", "4.8.0-beta.1+y")]
    public void Compare_Equal_ReturnsZero(string a, string b)
    {
        Assert.Equal(0, Cmp(a, b));
    }

    [Fact]
    public void Compare_DigitStringsAboveLongMax_AreComparedExactly()
    {
        Assert.Equal(1, Cmp("9223372036854775808.0.0", "9223372036854775807.0.0"));
        Assert.Equal(1, Cmp("1.0.99999999999999999999999999999999", "1.0.99999999999999999999999999999998"));
        Assert.Equal(1, Cmp("1.0.0-beta.18446744073709551617", "1.0.0-beta.18446744073709551616"));
        Assert.Equal(0, Cmp("18446744073709551616.0.0", "18446744073709551616.0.0"));
    }

    [Theory]
    [InlineData("v4.8.0")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-a_b")]
    [InlineData("1.2.3+")]
    [InlineData("a.b.c")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.\u0663")]
    public void TryParse_Invalid_ReturnsFalse(string? text)
    {
        Assert.False(SemVer.TryParse(text, out _));
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("4.8.0")]
    [InlineData("4.8.0-beta.2")]
    [InlineData("4.8.0-rc.1+build.5")]
    [InlineData("1.0.0-0")]
    [InlineData("1.0.0-0a")]
    public void TryParse_Valid_ReturnsTrue(string text)
    {
        Assert.True(SemVer.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("unknown", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("junk", true)]
    [InlineData("v4.7.0", true)]
    [InlineData("4.7.0", false)]
    [InlineData("4.8.0-beta.2", false)]
    [InlineData("1.0.0-beta.1", false)]
    [InlineData("1.0.1", false)]
    public void IsDevBuild_ClassifiesVersion(string? version, bool expected)
    {
        Assert.Equal(expected, SemVer.IsDevBuild(version));
    }
}
