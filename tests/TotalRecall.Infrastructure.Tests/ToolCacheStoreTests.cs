using System;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

public sealed class ToolCacheStoreTests : IDisposable
{
    private readonly MsSqliteConnection _conn;
    private long _nowMs = 1_000_000_000_000; // fixed fake clock

    public ToolCacheStoreTests()
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
    }

    public void Dispose() => _conn.Dispose();

    private ToolCacheStore NewStore(int maxEntries = 200, int defaultTtl = 600)
        => new(_conn, maxEntries, defaultTtl, () => _nowMs);

    [Fact]
    public void StoreThenCheck_FreshEntry_IsHit()
    {
        var store = NewStore();
        store.StoreResult("Read", "hash1", "file contents here");

        var hit = store.Check("Read", "hash1");

        Assert.NotNull(hit);
        Assert.Equal("file contents here", hit!.Content);
        Assert.Equal(_nowMs, hit.StoredAtMs);
        Assert.True(hit.TokenEstimate > 0);
    }

    [Fact]
    public void Check_UnknownKey_IsMiss()
    {
        var store = NewStore();
        Assert.Null(store.Check("Read", "nope"));
    }

    [Fact]
    public void Check_ExpiredByTtl_IsMissAndRowDeleted()
    {
        var store = NewStore(defaultTtl: 600);
        store.StoreResult("Read", "hash1", "content");

        _nowMs += 601_000; // 601s > 600s ttl
        Assert.Null(store.Check("Read", "hash1"));

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tool_cache";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Check_MaxAgeNarrowerThanTtl_IsMiss()
    {
        var store = NewStore(defaultTtl: 600);
        store.StoreResult("Read", "hash1", "content");

        _nowMs += 400_000; // 400s: within ttl(600) but over maxAge(300)
        Assert.Null(store.Check("Read", "hash1", maxAgeSeconds: 300));
        // Within ttl AND within a wide maxAge — still a hit (row not deleted).
        Assert.NotNull(store.Check("Read", "hash1", maxAgeSeconds: 500));
    }

    [Fact]
    public void Check_Hit_IncrementsHitCount()
    {
        var store = NewStore();
        store.StoreResult("Read", "hash1", "content");
        store.Check("Read", "hash1");
        store.Check("Read", "hash1");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT hit_count, last_hit_at_ms FROM tool_cache WHERE tool='Read' AND args_hash='hash1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(_nowMs, reader.GetInt64(1));
    }

    [Fact]
    public void StoreResult_OverCapacity_EvictsLeastRecentlyUsed()
    {
        var store = NewStore(maxEntries: 2);
        store.StoreResult("t", "h1", "one");
        _nowMs += 1000;
        store.StoreResult("t", "h2", "two");
        _nowMs += 1000;
        store.Check("t", "h1"); // h1 now more recently used than h2
        _nowMs += 1000;
        store.StoreResult("t", "h3", "three"); // evicts h2 (LRU)

        Assert.NotNull(store.Check("t", "h1"));
        Assert.Null(store.Check("t", "h2"));
        Assert.NotNull(store.Check("t", "h3"));
    }

    [Fact]
    public void StoreResult_SameKey_Upserts()
    {
        var store = NewStore();
        store.StoreResult("t", "h1", "old");
        _nowMs += 1000;
        store.StoreResult("t", "h1", "new content here");

        var hit = store.Check("t", "h1");
        Assert.Equal("new content here", hit!.Content);
        Assert.Equal(_nowMs, hit.StoredAtMs);
    }

    [Fact]
    public void SessionStats_TrackHitsMissesAndSavings()
    {
        var store = NewStore();
        store.StoreResult("t", "h1", "some cached content with several words");
        store.Check("t", "h1");      // hit
        store.Check("t", "absent");  // miss

        var (hits, misses, saved) = store.GetSessionStats();
        Assert.Equal(1L, hits);
        Assert.Equal(1L, misses);
        Assert.True(saved > 0);
    }

    [Fact]
    public void EstimateTokens_UsesWordHeuristic()
    {
        // 4 words * 0.75 = 3
        Assert.Equal(3, ToolCacheStore.EstimateTokens("one two three four"));
        Assert.Equal(0, ToolCacheStore.EstimateTokens(""));
    }
}
