// Plan 6 Task 6.0d — ConfigGetHandler contract tests.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tomlyn.Model;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class ConfigGetHandlerTests
{
    private static TomlTable BuildTable()
    {
        var tiers = new TomlTable();
        var hot = new TomlTable
        {
            ["max_entries"] = 10L,
            ["token_budget"] = 2000L,
            ["carry_forward_threshold"] = 0.8,
        };
        tiers["hot"] = hot;
        var root = new TomlTable
        {
            ["tiers"] = tiers,
            ["debug"] = true,
            ["name"] = "test",
        };
        return root;
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task NoKey_ReturnsFullConfig()
    {
        var handler = new ConfigGetHandler(() => BuildTable());
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var cfg = doc.RootElement.GetProperty("config");
        Assert.Equal(JsonValueKind.Object, cfg.ValueKind);
        Assert.Equal(10, cfg.GetProperty("tiers").GetProperty("hot").GetProperty("max_entries").GetInt32());
        Assert.True(cfg.GetProperty("debug").GetBoolean());
        Assert.Equal("test", cfg.GetProperty("name").GetString());
    }

    [Fact]
    public async Task WithKey_ReturnsScalarValue()
    {
        var handler = new ConfigGetHandler(() => BuildTable());
        var result = await handler.ExecuteAsync(
            Args("""{"key":"tiers.hot.max_entries"}"""),
            CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("tiers.hot.max_entries", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal(10, doc.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task WithKey_ReturnsNestedTable()
    {
        var handler = new ConfigGetHandler(() => BuildTable());
        var result = await handler.ExecuteAsync(
            Args("""{"key":"tiers.hot"}"""),
            CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var value = doc.RootElement.GetProperty("value");
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal(0.8, value.GetProperty("carry_forward_threshold").GetDouble());
    }

    [Fact]
    public async Task UnknownKey_Throws()
    {
        var handler = new ConfigGetHandler(() => BuildTable());
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"key":"missing.path"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_ParsesAsJson()
    {
        var handler = new ConfigGetHandler(() => BuildTable());
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        // Just verify it round-trips through JsonDocument.
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Name_Is_config_get()
    {
        var handler = new ConfigGetHandler(() => BuildTable());
        Assert.Equal("config_get", handler.Name);
    }
}
