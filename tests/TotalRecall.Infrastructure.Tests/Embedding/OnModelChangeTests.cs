using TotalRecall.Infrastructure.Embedding;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class OnModelChangeTests
{
    [Theory]
    [InlineData("auto", OnModelChange.Auto)]
    [InlineData("AUTO", OnModelChange.Auto)]
    [InlineData("Auto", OnModelChange.Auto)]
    [InlineData(null, OnModelChange.Auto)]
    [InlineData("", OnModelChange.Auto)]
    [InlineData("   ", OnModelChange.Auto)]
    [InlineData("garbage", OnModelChange.Auto)]
    [InlineData("warn", OnModelChange.Warn)]
    [InlineData("Warn", OnModelChange.Warn)]
    [InlineData("WARN", OnModelChange.Warn)]
    [InlineData("  warn  ", OnModelChange.Warn)]
    [InlineData("block", OnModelChange.Block)]
    [InlineData("BLOCK", OnModelChange.Block)]
    [InlineData("Block", OnModelChange.Block)]
    public void Parse_MapsValueToPolicy(string? value, OnModelChange expected)
    {
        Assert.Equal(expected, OnModelChangePolicy.Parse(value));
    }
}
