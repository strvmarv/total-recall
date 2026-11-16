using TotalRecall.Infrastructure.WhatsNew;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.WhatsNew;

public sealed class UpdateSettingsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\t", false)]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData(" False ", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("no", true)]
    [InlineData("off", true)]
    [InlineData("00", true)]
    [InlineData("false1", true)]
    [InlineData(" 1 ", true)]
    public void EnvDisablesCheck(string? value, bool expected)
    {
        Assert.Equal(expected, UpdateSettings.EnvDisablesCheck(value));
    }

    [Theory]
    [InlineData(false, true, null, false)]
    [InlineData(true, false, null, false)]
    [InlineData(true, false, "0", false)]
    [InlineData(true, true, "1", false)]
    [InlineData(true, true, null, true)]
    [InlineData(true, true, "", true)]
    [InlineData(true, true, "0", true)]
    [InlineData(true, true, "false", true)]
    public void CheckOn(bool notice, bool check, string? env, bool expected)
    {
        Assert.Equal(expected, UpdateSettings.CheckOn(notice, check, env));
    }

    public static TheoryData<object?, int> IntervalCases => new()
    {
        { 1L, 1 },
        { 12L, 12 },
        { 8760L, 8760 },
        { 0L, 24 },
        { -5L, 24 },
        { 8761L, 24 },
        { 1.5d, 24 },
        { 24.0d, 24 },
        { "12", 24 },
        { true, 24 },
        { null, 24 },
    };

    [Theory]
    [MemberData(nameof(IntervalCases))]
    public void NormalizeIntervalHours(object? raw, int expected)
    {
        Assert.Equal(expected, UpdateSettings.NormalizeIntervalHours(raw));
    }

    [Fact]
    public void NormalizeIntervalHours_IntInRange_IsAccepted()
    {
        Assert.Equal(6, UpdateSettings.NormalizeIntervalHours(6));
        Assert.Equal(24, UpdateSettings.NormalizeIntervalHours(0));
    }
}
