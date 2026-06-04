using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Server.Handlers;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Server.Tests.Handlers;

public sealed class CacheHandlerTests : IDisposable
{
    private readonly MsSqliteConnection _conn;
    private readonly ToolCacheStore _cache;

    public CacheHandlerTests()
    {
        _conn = new MsSqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE tool_cache (
                tool           TEXT NOT NULL,
                args_hash      TEXT NOT NULL,
                content        TEXT NOT NULL,
                content_hash   TEXT NOT NULL,
                stored_at_ms   INTEGER NOT NULL,
                ttl_seconds    INTEGER NOT NULL DEFAULT 600,
                hit_count      INTEGER NOT NULL DEFAULT 0,
                last_hit_at_ms INTEGER,
                token_estimate INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (tool, args_hash)
            )
            """;
        cmd.ExecuteNonQuery();
        _cache = new ToolCacheStore(_conn);
    }

    public void Dispose() => _conn.Dispose();

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task CacheStore_ThenCheck_RoundTrips()
    {
        var storeHandler = new CacheStoreHandler(_cache);
        var checkHandler = new CacheCheckHandler(_cache);

        var storeResult = await storeHandler.ExecuteAsync(
            Args("""{"tool":"Read","argsHash":"abc","content":"cached file content"}"""),
            CancellationToken.None);
        Assert.NotEqual(true, storeResult.IsError);
        var stored = JsonDocument.Parse(storeResult.Content[0].Text!).RootElement;
        Assert.True(stored.GetProperty("stored").GetBoolean());
        Assert.True(stored.GetProperty("tokenEstimate").GetInt32() > 0);

        var checkResult = await checkHandler.ExecuteAsync(
            Args("""{"tool":"Read","argsHash":"abc"}"""),
            CancellationToken.None);
        Assert.NotEqual(true, checkResult.IsError);
        var hit = JsonDocument.Parse(checkResult.Content[0].Text!).RootElement;
        Assert.True(hit.GetProperty("hit").GetBoolean());
        Assert.Equal("cached file content", hit.GetProperty("content").GetString());
        Assert.True(hit.GetProperty("tokenSavings").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(hit.GetProperty("cachedAt").GetString()));
    }

    [Fact]
    public async Task CacheCheck_Miss_ReturnsHitFalse()
    {
        var checkHandler = new CacheCheckHandler(_cache);
        var result = await checkHandler.ExecuteAsync(
            Args("""{"tool":"Read","argsHash":"missing"}"""),
            CancellationToken.None);
        Assert.NotEqual(true, result.IsError);
        var payload = JsonDocument.Parse(result.Content[0].Text!).RootElement;
        Assert.False(payload.GetProperty("hit").GetBoolean());
    }

    [Fact]
    public async Task CacheCheck_MissingRequiredArg_Throws()
    {
        var checkHandler = new CacheCheckHandler(_cache);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            checkHandler.ExecuteAsync(Args("""{"tool":"Read"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task CacheStore_MissingContent_Throws()
    {
        var storeHandler = new CacheStoreHandler(_cache);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storeHandler.ExecuteAsync(
                Args("""{"tool":"Read","argsHash":"abc"}"""), CancellationToken.None));
    }
}
