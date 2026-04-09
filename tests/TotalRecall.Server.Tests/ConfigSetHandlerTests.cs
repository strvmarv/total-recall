// Plan 6 Task 6.0d — ConfigSetHandler contract tests.
//
// Writes to a temp dir via the test-only constructor seam so the user's
// real ~/.total-recall/config.toml is never touched.

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tomlyn;
using Tomlyn.Model;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public sealed class ConfigSetHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigSetHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-cs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.toml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private ConfigSetHandler NewHandler(TomlTable? initial = null)
    {
        return new ConfigSetHandler(
            tableProvider: () => initial ?? new TomlTable(),
            userConfigPath: _configPath);
    }

    [Fact]
    public async Task HappyPath_WritesStringValue()
    {
        var handler = NewHandler();
        var result = await handler.ExecuteAsync(
            Args("""{"key":"embedding.model","value":"all-MiniLM-L6-v2"}"""),
            CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("embedding.model", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal("all-MiniLM-L6-v2", doc.RootElement.GetProperty("new_value").GetString());
        Assert.True(doc.RootElement.GetProperty("written").GetBoolean());

        Assert.True(File.Exists(_configPath));
        var parsed = Toml.Parse(File.ReadAllText(_configPath)).ToModel();
        var embedding = (TomlTable)parsed["embedding"];
        Assert.Equal("all-MiniLM-L6-v2", (string)embedding["model"]);
    }

    [Fact]
    public async Task HappyPath_WritesNumberAndBool()
    {
        var handler = NewHandler();
        await handler.ExecuteAsync(
            Args("""{"key":"tiers.hot.max_entries","value":25}"""),
            CancellationToken.None);
        await handler.ExecuteAsync(
            Args("""{"key":"debug","value":true}"""),
            CancellationToken.None);

        var parsed = Toml.Parse(File.ReadAllText(_configPath)).ToModel();
        var hot = (TomlTable)((TomlTable)parsed["tiers"])["hot"];
        Assert.Equal(25L, (long)hot["max_entries"]);
        Assert.True((bool)parsed["debug"]);
    }

    [Fact]
    public async Task OldValue_ReportedFromProviderTable()
    {
        var initial = new TomlTable();
        var tiers = new TomlTable();
        var hot = new TomlTable { ["max_entries"] = 10L };
        tiers["hot"] = hot;
        initial["tiers"] = tiers;

        var handler = NewHandler(initial);
        var result = await handler.ExecuteAsync(
            Args("""{"key":"tiers.hot.max_entries","value":42}"""),
            CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("10", doc.RootElement.GetProperty("old_value").GetString());
        Assert.Equal("42", doc.RootElement.GetProperty("new_value").GetString());
    }

    [Fact]
    public async Task MissingKey_Throws()
    {
        var handler = NewHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"value":42}"""), CancellationToken.None));
    }

    [Fact]
    public async Task MissingValue_Throws()
    {
        var handler = NewHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"key":"a.b"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task NullValue_Throws()
    {
        var handler = NewHandler();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => handler.ExecuteAsync(Args("""{"key":"a.b","value":null}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_DtoShape()
    {
        var handler = NewHandler();
        var result = await handler.ExecuteAsync(
            Args("""{"key":"foo","value":"bar"}"""),
            CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.ConfigSetResultDto);
        Assert.NotNull(dto);
        Assert.Equal("foo", dto!.Key);
        Assert.Equal("bar", dto.NewValue);
        Assert.True(dto.Written);
    }

    [Fact]
    public void Name_Is_config_set()
    {
        var handler = NewHandler();
        Assert.Equal("config_set", handler.Name);
    }
}
