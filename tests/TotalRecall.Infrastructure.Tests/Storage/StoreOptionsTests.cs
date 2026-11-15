using System;
using Microsoft.FSharp.Core;
using Npgsql;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Storage;

/// <summary>
/// Git sync milestone 0a: the small store options (caller-chosen creation
/// time, scope / entry-type updates, Postgres guards) and the tolerant
/// source-tool reader.
/// </summary>
public class StoreOptionsTests
{
    private static (MsSqliteConnection conn, SqliteStore store) NewFixture()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new SqliteStore(conn));
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void Insert_WithCreatedAt_KeepsItAndStampsUpdatedAtNow()
    {
        var (conn, store) = NewFixture();
        using (conn)
        {
            var startMs = NowMs();
            var id = store.Insert(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("c", CreatedAt: 1_000L));

            var entry = store.Get(Tier.Warm, ContentType.Memory, id)!;
            Assert.Equal(1_000L, entry.CreatedAt);
            Assert.True(entry.UpdatedAt >= startMs);
            Assert.True(entry.LastAccessedAt >= startMs);
        }
    }

    [Fact]
    public void InsertWithEmbedding_WithCreatedAt_KeepsItAndStampsUpdatedAtNow()
    {
        var (conn, store) = NewFixture();
        using (conn)
        {
            var startMs = NowMs();
            var embedding = new FakeEmbedder().Embed("c");
            var id = store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("c", CreatedAt: 2_000L), embedding);

            var entry = store.Get(Tier.Warm, ContentType.Memory, id)!;
            Assert.Equal(2_000L, entry.CreatedAt);
            Assert.True(entry.UpdatedAt >= startMs);
        }
    }

    [Fact]
    public void Insert_WithoutCreatedAt_StampsNow()
    {
        var (conn, store) = NewFixture();
        using (conn)
        {
            var startMs = NowMs();
            var id = store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts("c"));

            var entry = store.Get(Tier.Warm, ContentType.Memory, id)!;
            Assert.True(entry.CreatedAt >= startMs);
        }
    }

    [Fact]
    public void Update_ScopeAndEntryType_ChangesOnlyThose()
    {
        var (conn, store) = NewFixture();
        using (conn)
        {
            var id = store.Insert(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("keep me", Tags: new[] { "t" },
                    Scope: "a", EntryType: EntryType.Correction));

            store.Update(Tier.Warm, ContentType.Memory, id,
                new UpdateEntryOpts { Scope = "b", EntryType = EntryType.Decision });

            var entry = store.Get(Tier.Warm, ContentType.Memory, id)!;
            Assert.Equal("b", entry.Scope);
            Assert.True(entry.EntryType.IsDecision);
            Assert.Equal("keep me", entry.Content);
            Assert.Equal(new[] { "t" }, entry.Tags);
        }
    }

    // ---- Postgres guards (the guard fires before anything connects) ----

    private static PostgresStore NewUnconnectedPostgresStore() =>
        new(NpgsqlDataSource.Create("Host=localhost;Database=x"), "owner");

    [Fact]
    public void PostgresStore_Insert_WithCreatedAt_Throws()
    {
        var store = NewUnconnectedPostgresStore();
        Assert.Throws<NotSupportedException>(() =>
            store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts("c", CreatedAt: 1L)));
    }

    [Fact]
    public void PostgresStore_InsertWithEmbedding_WithCreatedAt_Throws()
    {
        var store = NewUnconnectedPostgresStore();
        Assert.Throws<NotSupportedException>(() =>
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("c", CreatedAt: 1L), new float[] { 0f }));
    }

    [Fact]
    public void PostgresStore_Update_WithScope_Throws()
    {
        var store = NewUnconnectedPostgresStore();
        Assert.Throws<NotSupportedException>(() =>
            store.Update(Tier.Warm, ContentType.Memory, "id", new UpdateEntryOpts { Scope = "s" }));
    }

    [Fact]
    public void PostgresStore_Update_WithEntryType_Throws()
    {
        var store = NewUnconnectedPostgresStore();
        Assert.Throws<NotSupportedException>(() =>
            store.Update(Tier.Warm, ContentType.Memory, "id",
                new UpdateEntryOpts { EntryType = EntryType.Decision }));
    }

    // ---- SourceToolMapping.TryParse ----

    [Fact]
    public void SourceToolMapping_TryParse_NullAndUnknown_ReturnNone()
    {
        Assert.True(FSharpOption<SourceTool>.get_IsNone(SourceToolMapping.TryParse(null)));
        Assert.True(FSharpOption<SourceTool>.get_IsNone(SourceToolMapping.TryParse("nope")));
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("copilot-cli")]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("cline")]
    [InlineData("hermes")]
    [InlineData("manual")]
    public void SourceToolMapping_TryParse_Known_MatchesParse(string value)
    {
        var tolerant = SourceToolMapping.TryParse(value);
        var strict = SourceToolMapping.Parse(value);
        Assert.True(FSharpOption<SourceTool>.get_IsSome(tolerant));
        Assert.Equal(strict.Value, tolerant.Value);
    }

    [Fact]
    public void SourceToolMapping_Parse_Unknown_StillThrows()
    {
        Assert.Throws<ArgumentException>(() => SourceToolMapping.Parse("nope"));
    }
}
