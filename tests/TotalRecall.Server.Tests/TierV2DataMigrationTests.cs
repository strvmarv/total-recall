// tests/TotalRecall.Server.Tests/TierV2DataMigrationTests.cs
//
// Task 6 — one-time data move into the merged tier model. Exercises the REAL
// vec path on an in-memory SQLite store (the sqlite-vec native lib is staged
// in the test output), so the transactional vec dance (RC1/RC2/RI1) is
// actually covered, not mocked.
//
// Asserts: pinned row -> sticky-hot with entry_type/scope preserved (C2),
// old non-sticky hot row -> warm, a hot_*_vec row exists at the migrated
// row's rowid (RC2 — no orphan collision), the pinned_* tables are dropped
// (NC1), and a second run is an idempotent no-op (NC2, _meta flag).

using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Server.Tests;

public sealed class TierV2DataMigrationTests
{
    private static void InsertViaStore(
        SqliteStore store,
        IEmbedder embedder,
        Tier tier,
        ContentType type,
        string id,
        string content,
        EntryType? entryType = null,
        string? scope = null)
    {
        // InsertWithEmbedding writes both the content row and its vec companion
        // in one transaction — this is what gives the legacy rows a real vec row
        // for the migration's source-vec-delete step (RC2) to act on.
        var opts = new InsertEntryOpts(
            Content: content,
            Id: id,
            Scope: scope,
            EntryType: entryType);
        store.InsertWithEmbedding(tier, type, opts, embedder.Embed(content));
    }

    private static long ScalarCount(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is long l ? l : System.Convert.ToInt64(result);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RunOnce_PinnedBecomeStickyHot_AndHotBecomeWarm_PreservingColumns()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var store = new SqliteStore(conn);
        var embedder = new RecordingFakeEmbedder();

        // legacy rows: a pinned Correction (type + scope must survive) + an old hot note
        InsertViaStore(store, embedder, Tier.Pinned, ContentType.Memory,
            id: "p1", content: "pinned fact", entryType: EntryType.Correction, scope: "global");
        InsertViaStore(store, embedder, Tier.Hot, ContentType.Memory,
            id: "h1", content: "old hot note");

        TierV2DataMigration.RunOnce(conn, embedder, log: null);

        var p1 = store.Get(Tier.Hot, ContentType.Memory, "p1");
        Assert.NotNull(p1);
        Assert.True(store.IsSticky(ContentType.Memory, "p1"));
        Assert.Equal(EntryType.Correction, p1!.EntryType); // C2: entry_type preserved
        Assert.Equal("global", p1.Scope);                  // C2: scope preserved
        Assert.Equal(1.0, p1.DecayScore);                  // pinned -> sticky-hot resets decay
        Assert.Equal(1, store.Count(Tier.Warm, ContentType.Memory)); // h1 -> warm
        Assert.Equal(0, store.Count(Tier.Cold, ContentType.Memory));

        // RC2 no orphan collision: a hot_*_vec row exists for p1's (re-embedded) rowid.
        Assert.Equal(1, ScalarCount(conn,
            "SELECT COUNT(*) FROM hot_memories_vec WHERE rowid = (SELECT rowid FROM hot_memories WHERE id='p1')"));
        // h1's vec row moved to warm; no orphan left in hot_memories_vec.
        Assert.Equal(1, ScalarCount(conn, "SELECT COUNT(*) FROM hot_memories_vec"));
        Assert.Equal(1, ScalarCount(conn, "SELECT COUNT(*) FROM warm_memories_vec"));

        // NC1: pinned_* tables dropped by RunOnce itself.
        Assert.Equal(0, ScalarCount(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE name='pinned_memories'"));

        // idempotent: second run is a no-op (flag set).
        TierV2DataMigration.RunOnce(conn, embedder, log: null);
        Assert.Equal(1, store.Count(Tier.Hot, ContentType.Memory)); // still just p1
        Assert.True(store.IsSticky(ContentType.Memory, "p1"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RunOnce_NullEmbedder_MovesContentAndFts_SkipsVec()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var store = new SqliteStore(conn);
        var embedder = new RecordingFakeEmbedder();

        InsertViaStore(store, embedder, Tier.Pinned, ContentType.Memory,
            id: "p1", content: "pinned fact", entryType: EntryType.Decision, scope: "global");
        InsertViaStore(store, embedder, Tier.Hot, ContentType.Memory,
            id: "h1", content: "old hot note");

        // NI1: null embedder — content + FTS still move, vec is simply absent.
        TierV2DataMigration.RunOnce(conn, embedder: null, log: null);

        var p1 = store.Get(Tier.Hot, ContentType.Memory, "p1");
        Assert.NotNull(p1);
        Assert.True(store.IsSticky(ContentType.Memory, "p1"));
        Assert.Equal(EntryType.Decision, p1!.EntryType);
        Assert.Equal(1, store.Count(Tier.Warm, ContentType.Memory));
        // vec step skipped: no hot vec row was written for the migrated pinned row.
        Assert.Equal(0, ScalarCount(conn, "SELECT COUNT(*) FROM hot_memories_vec"));
        // flag still set — session_start completes and never re-runs.
        Assert.Equal(0, ScalarCount(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE name='pinned_memories'"));
    }
}
