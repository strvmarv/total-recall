namespace TotalRecall.Web.Tests;

using TotalRecall.Web.Api;
using Xunit;

public sealed class ToolAllowlistTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("usage_status")]
    [InlineData("eval_report")]
    [InlineData("memory_search")]
    public void IsAllowed_True_ForCuratedTools(string name) =>
        Assert.True(ToolAllowlist.IsAllowed(name));

    [Theory]
    [InlineData("migrate_to_remote")]
    [InlineData("session_end")]
    [InlineData("")]
    [InlineData("does_not_exist")]
    public void IsAllowed_False_ForEverythingElse(string name) =>
        Assert.False(ToolAllowlist.IsAllowed(name));
}
