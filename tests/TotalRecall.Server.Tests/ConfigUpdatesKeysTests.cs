using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public sealed class ConfigUpdatesKeysTests : IDisposable
{
    private readonly string _dir;
    private readonly string _userPath;
    private readonly string _absentPath;

    public ConfigUpdatesKeysTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tr-cuk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _userPath = Path.Combine(_dir, "config.toml");
        _absentPath = Path.Combine(_dir, "absent.toml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private ConfigGetHandler NewGet() =>
        new(() => new ConfigLoader().LoadEffectiveTable(_absentPath));

    private ConfigSetHandler NewSet() =>
        new(() => new ConfigLoader().LoadEffectiveTable(_absentPath), _userPath);

    [Fact]
    public async Task ConfigGet_UpdatesCheck_DefaultTrue()
    {
        var result = await NewGet().ExecuteAsync(Args("""{"key":"updates.check"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.True(doc.RootElement.GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task ConfigGet_UpdatesCheckIntervalHours_Default24()
    {
        var result = await NewGet().ExecuteAsync(Args("""{"key":"updates.check_interval_hours"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(24, doc.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task ConfigSet_UpdatesCheckFalse_IsReadBack()
    {
        await NewSet().ExecuteAsync(Args("""{"key":"updates.check","value":false}"""), CancellationToken.None);
        Assert.False(ConfigLoader.LoadUpdatesCheckEnabled(_userPath));
    }

    [Fact]
    public async Task ConfigSet_IntervalHours_IsReadBack()
    {
        await NewSet().ExecuteAsync(Args("""{"key":"updates.check_interval_hours","value":6}"""), CancellationToken.None);
        Assert.Equal(6, ConfigLoader.LoadUpdatesCheckIntervalHours(_userPath));
    }
}
