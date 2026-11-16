using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public sealed class UpdateChannelTests
{
    [Theory]
    [InlineData("4.8.0", "stable")]
    [InlineData("4.8.0+abc", "stable")]
    [InlineData("garbage", "stable")]
    [InlineData(null, "stable")]
    [InlineData("", "stable")]
    [InlineData("4.8.0-beta.1", "pre")]
    [InlineData("4.8.0-beta.1+x", "pre")]
    public void Of(string? running, string expected)
    {
        Assert.Equal(expected, UpdateChannel.Of(running));
    }

    public static TheoryData<string?, bool> ValidCases => new()
    {
        { "4.9.0", true },
        { "4.10.0-beta.1", true },
        { "v4.9.0", false },
        { "4.9.0+b", false },
        { "4.9", false },
        { "", false },
        { null, false },
        { "4.9.0-" + new string('a', 59), false },
        { "4.9.0-" + new string('a', 58), true },
    };

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void IsValidReleaseVersion(string? version, bool expected)
    {
        Assert.Equal(expected, UpdateChannel.IsValidReleaseVersion(version));
    }

    [Theory]
    [InlineData("stable", true)]
    [InlineData("pre", true)]
    [InlineData("Stable", false)]
    [InlineData("beta", false)]
    [InlineData("../x", false)]
    [InlineData(null, false)]
    public void IsKnown(string? channel, bool expected)
    {
        Assert.Equal(expected, UpdateChannel.IsKnown(channel));
    }
}
