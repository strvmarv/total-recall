// tests/TotalRecall.Server.Tests/DumpCatalogTests.cs
//
// Contract test for CatalogDumper.DumpLocalCatalogJson():
//   - output is valid JSON with shape { "tools": [...] }
//   - at least 40 tools (local/sqlite mode registers 48: 41 core + 7 sqlite-only)
//   - contains memory_store and usage_status (always in local mode)
//   - does NOT contain skill_delete (cortex-only)

using System.Text.Json;
using TotalRecall.Server;
using Xunit;

public class DumpCatalogTests
{
    // The catalog is exactly the tools/list result shape: { "tools": [ {name, description, inputSchema}, ... ] }
    // Local/sqlite mode must expose memory_store and must NOT expose skill_delete (cortex-only).
    [Fact]
    public void LocalCatalog_HasExpectedShape()
    {
        var json = CatalogDumper.DumpLocalCatalogJson();
        using var doc = JsonDocument.Parse(json);
        var tools = doc.RootElement.GetProperty("tools");
        Assert.True(tools.GetArrayLength() >= 40);

        var names = new System.Collections.Generic.HashSet<string>();
        foreach (var t in tools.EnumerateArray())
            names.Add(t.GetProperty("name").GetString()!);

        Assert.Contains("memory_store", names);
        Assert.Contains("usage_status", names);   // local registers usage/cache/skill
        Assert.DoesNotContain("skill_delete", names); // cortex-only
    }
}
