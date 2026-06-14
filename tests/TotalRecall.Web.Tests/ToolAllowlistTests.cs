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
    [InlineData("memory_list")]
    [InlineData("memory_recent")]
    [InlineData("memory_history")]
    [InlineData("memory_get")]
    [InlineData("memory_inspect")]
    [InlineData("memory_lineage")]
    [InlineData("memory_update")]
    [InlineData("memory_pin")]
    [InlineData("memory_unpin")]
    [InlineData("memory_promote")]
    [InlineData("memory_demote")]
    [InlineData("memory_delete")]
    [InlineData("kb_list_collections")]
    [InlineData("kb_ingest_file")]
    [InlineData("kb_ingest_dir")]
    [InlineData("kb_refresh")]
    [InlineData("kb_remove")]
    [InlineData("kb_search")]
    [InlineData("config_get")]
    [InlineData("config_set")]
    public void IsAllowed_True_ForCuratedTools(string name) =>
        Assert.True(ToolAllowlist.IsAllowed(name));

    [Theory]
    [InlineData("migrate_to_remote")]
    [InlineData("session_end")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("does_not_exist")]
    public void IsAllowed_False_ForEverythingElse(string? name) =>
        Assert.False(ToolAllowlist.IsAllowed(name!));
}
