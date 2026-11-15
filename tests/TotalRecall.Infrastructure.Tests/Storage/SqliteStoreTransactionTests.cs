using System;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Storage;

/// <summary>
/// Git sync milestone 0a: <see cref="SqliteStoreTransaction"/>, the
/// all-or-nothing store path used by sync. Failure injection goes through the
/// internal <c>BeforeStep</c> seam.
/// </summary>
public class SqliteStoreTransactionTests
{
    private static (MsSqliteConnection conn, SqliteStore store) NewStore()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new SqliteStore(conn));
    }

    private static float[] Embed(string text) => new FakeEmbedder().Embed(text);

    private static long ScalarLong(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static Action<string> FailAt(string step, int occurrence = 1)
    {
        var seen = 0;
        return s =>
        {
            if (s == step && ++seen == occurrence)
                throw new InvalidOperationException("injected at " + step);
        };
    }

    // ---- commit and basic behavior ----

    [Fact]
    public void Commit_InsertWithEmbedding_PersistsRowAndVector()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            using (var tx = store.BeginTransaction())
            {
                tx.Insert(Tier.Hot, ContentType.Memory,
                    new InsertEntryOpts("hello", Id: "id-1"), Embed("hello"));
                tx.Commit();
            }

            Assert.NotNull(store.Get(Tier.Hot, ContentType.Memory, "id-1"));
            Assert.Equal(1, ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec"));
            Assert.Equal(
                ScalarLong(conn, "SELECT rowid FROM hot_memories WHERE id = 'id-1'"),
                ScalarLong(conn, "SELECT rowid FROM hot_memories_vec"));
        }
    }

    [Fact]
    public void Insert_WithoutEmbedding_HasNoVectorRow()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            using var tx = store.BeginTransaction();
            tx.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("x", Id: "id-1"));
            tx.Commit();

            Assert.Equal(0, ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec"));
            Assert.NotNull(store.Get(Tier.Hot, ContentType.Memory, "id-1"));
        }
    }

    [Fact]
    public void Insert_HonorsIdAndCreatedAt()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            using var tx = store.BeginTransaction();
            var id = tx.Insert(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("x", Id: "fixed-id", CreatedAt: 4_000L));
            var entry = tx.Get(Tier.Warm, ContentType.Memory, "fixed-id")!;
            tx.Commit();

            Assert.Equal("fixed-id", id);
            Assert.Equal(4_000L, entry.CreatedAt);
        }
    }

    [Fact]
    public void Dispose_WithoutCommit_RollsBack()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var before = StoreSnapshot.Take(conn);
            using (var tx = store.BeginTransaction())
            {
                tx.Insert(Tier.Hot, ContentType.Memory,
                    new InsertEntryOpts("x", Id: "id-1"), Embed("x"));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
        }
    }

    [Fact]
    public void BeginTransaction_WhileOpen_Throws()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            using var tx = store.BeginTransaction();
            Assert.Throws<InvalidOperationException>(() => store.BeginTransaction());
        }
    }

    [Fact]
    public void BeginTransaction_AfterCommitOrDispose_Works()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var first = store.BeginTransaction();
            first.Commit();
            first.Dispose();

            var second = store.BeginTransaction();
            second.Dispose();

            using var third = store.BeginTransaction();
            third.Commit();
        }
    }

    [Fact]
    public void OperationsAfterCommit_Throw()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var tx = store.BeginTransaction();
            tx.Commit();

            Assert.Throws<InvalidOperationException>(() =>
                tx.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("x")));
            Assert.Throws<InvalidOperationException>(() =>
                tx.Get(Tier.Hot, ContentType.Memory, "x"));
            Assert.Throws<InvalidOperationException>(() => tx.Commit());
            tx.Dispose();
        }
    }

    // ---- failure injection ----

    [Fact]
    public void FailureAtInsertRow_RollsBack()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var before = StoreSnapshot.Take(conn);
            using (var tx = store.BeginTransaction())
            {
                tx.BeforeStep = FailAt("insert-row");
                Assert.Throws<InvalidOperationException>(() =>
                    tx.Insert(Tier.Hot, ContentType.Memory,
                        new InsertEntryOpts("x", Id: "id-1"), Embed("x")));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
        }
    }

    [Fact]
    public void FailureAtInsertVec_RollsBack()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var before = StoreSnapshot.Take(conn);
            using (var tx = store.BeginTransaction())
            {
                tx.BeforeStep = FailAt("insert-vec");
                Assert.Throws<InvalidOperationException>(() =>
                    tx.Insert(Tier.Hot, ContentType.Memory,
                        new InsertEntryOpts("x", Id: "id-1"), Embed("x")));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
        }
    }

    [Fact]
    public void RolledBackVector_LeavesNoOrphan_AndRowidReuseIsClean()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var before = StoreSnapshot.Take(conn);
            var eA = Embed("aaaa");
            using (var tx = store.BeginTransaction())
            {
                tx.BeforeStep = FailAt("insert-row", occurrence: 2);
                tx.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("aaaa", Id: "A"), eA);
                Assert.Throws<InvalidOperationException>(() =>
                    tx.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("other", Id: "other")));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));

            var eB = Embed("bbbbbbbb");
            using (var tx = store.BeginTransaction())
            {
                tx.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("bbbbbbbb", Id: "B"), eB);
                tx.Commit();
            }

            var snap = StoreSnapshot.Take(conn);
            Assert.Contains("embedding=" + StoreSnapshot.Hex(eB), snap);
            Assert.DoesNotContain(StoreSnapshot.Hex(eA), snap);
            Assert.Equal(1, ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec"));
        }
    }

    [Fact]
    public void Insert_RemovesOrphanVectorRowAtTheReusedRowid()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            store.InsertWithEmbedding(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("old", Id: "old"), Embed("old"));
            // Delete only the content row: its vector row is now an orphan.
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM hot_memories WHERE id = 'old'";
                del.ExecuteNonQuery();
            }
            Assert.Equal(1, ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec"));

            var fresh = Embed("fresh-content");
            using (var tx = store.BeginTransaction())
            {
                tx.Insert(Tier.Hot, ContentType.Memory,
                    new InsertEntryOpts("fresh-content", Id: "fresh"), fresh);
                tx.Commit();
            }

            Assert.Equal(1, ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec"));
            Assert.Contains("embedding=" + StoreSnapshot.Hex(fresh), StoreSnapshot.Take(conn));
        }
    }

    [Fact]
    public void Insert_WithExistingId_ThrowsAndRollsBack()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            using (var first = store.BeginTransaction())
            {
                first.Insert(Tier.Hot, ContentType.Memory,
                    new InsertEntryOpts("one", Id: "dup"), Embed("one"));
                first.Commit();
            }

            var before = StoreSnapshot.Take(conn);
            using (var second = store.BeginTransaction())
            {
                Assert.ThrowsAny<Exception>(() =>
                    second.Insert(Tier.Hot, ContentType.Memory,
                        new InsertEntryOpts("two", Id: "dup"), Embed("two")));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
        }
    }

    // ---- equivalence with the existing store method ----

    [Fact]
    public void Insert_MatchesStoreInsertWithEmbedding()
    {
        var (connA, storeA) = NewStore();
        var (connB, storeB) = NewStore();
        using (connA)
        using (connB)
        {
            var opts = new InsertEntryOpts(
                "same content", Source: "src", Project: "p", Tags: new[] { "t1", "t2" },
                Id: "fixed-id", Scope: "s", EntryType: EntryType.Decision, CreatedAt: 5_000L);
            var embedding = Embed("same content");

            using (var tx = storeA.BeginTransaction())
            {
                tx.Insert(Tier.Hot, ContentType.Memory, opts, embedding);
                tx.Commit();
            }
            storeB.InsertWithEmbedding(Tier.Hot, ContentType.Memory, opts, embedding);

            Assert.Equal(
                StoreSnapshot.Take(connB, maskTimes: true),
                StoreSnapshot.Take(connA, maskTimes: true));
        }
    }

    // ---- Task 3: update, delete, sticky, import log ----

    private static void SeedHot(SqliteStore store, string id, string text) =>
        store.InsertWithEmbedding(Tier.Hot, ContentType.Memory,
            new InsertEntryOpts(text, Id: id, CreatedAt: 1_000L, Tags: new[] { "a" }),
            Embed(text));

    /// <summary>
    /// Runs <paramref name="op"/> in a transaction after a first insert, with a
    /// failure injected at <paramref name="step"/>; asserts the whole
    /// transaction (the extra insert included) rolled back.
    /// </summary>
    private static void AssertFailureRollsBack(
        string step, Action<SqliteStoreTransaction> op, Action<SqliteStore>? seed = null)
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            seed?.Invoke(store);
            var before = StoreSnapshot.Take(conn);
            using (var tx = store.BeginTransaction())
            {
                tx.Insert(Tier.Warm, ContentType.Memory,
                    new InsertEntryOpts("extra", Id: "extra"), Embed("extra"));
                tx.BeforeStep = FailAt(step);
                Assert.Throws<InvalidOperationException>(() => op(tx));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
        }
    }

    [Fact]
    public void Update_Commits_AndMatchesStoreUpdate()
    {
        var (connA, storeA) = NewStore();
        var (connB, storeB) = NewStore();
        using (connA)
        using (connB)
        {
            SeedHot(storeA, "id-1", "before");
            SeedHot(storeB, "id-1", "before");
            var opts = new UpdateEntryOpts
            {
                Content = "after", Summary = "sum", Tags = new[] { "x", "y" },
                Project = "proj", DecayScore = 0.5, MetadataJson = "{\"k\":\"v\"}",
            };

            using (var tx = storeA.BeginTransaction())
            {
                tx.Update(Tier.Hot, ContentType.Memory, "id-1", opts);
                tx.Commit();
            }
            storeB.Update(Tier.Hot, ContentType.Memory, "id-1", opts);

            Assert.Equal(
                StoreSnapshot.Take(connB, maskTimes: true),
                StoreSnapshot.Take(connA, maskTimes: true));
        }
    }

    [Fact]
    public void Update_ScopeAndEntryType_Work()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "x");
            using (var tx = store.BeginTransaction())
            {
                tx.Update(Tier.Hot, ContentType.Memory, "id-1",
                    new UpdateEntryOpts { Scope = "s2", EntryType = EntryType.Decision });
                tx.Commit();
            }

            var entry = store.Get(Tier.Hot, ContentType.Memory, "id-1")!;
            Assert.Equal("s2", entry.Scope);
            Assert.True(entry.EntryType.IsDecision);
        }
    }

    [Fact]
    public void Delete_RemovesRowAndVector()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "x");
            using (var tx = store.BeginTransaction())
            {
                tx.Delete(Tier.Hot, ContentType.Memory, "id-1");
                tx.Commit();
            }

            Assert.Null(store.Get(Tier.Hot, ContentType.Memory, "id-1"));
            Assert.Equal(0, ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec"));
        }
    }

    [Fact]
    public void Delete_MissingId_IsNoOp()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "x");
            var before = StoreSnapshot.Take(conn);
            using (var tx = store.BeginTransaction())
            {
                tx.Delete(Tier.Hot, ContentType.Memory, "nope");
                tx.Commit();
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
        }
    }

    [Fact]
    public void FailureAtDeleteVec_RollsBackBothRows() =>
        AssertFailureRollsBack("delete-vec",
            tx => tx.Delete(Tier.Hot, ContentType.Memory, "id-1"),
            store => SeedHot(store, "id-1", "x"));

    [Fact]
    public void FailureAtDeleteRow_RollsBack() =>
        AssertFailureRollsBack("delete-row",
            tx => tx.Delete(Tier.Hot, ContentType.Memory, "id-1"),
            store => SeedHot(store, "id-1", "x"));

    [Fact]
    public void FailureAtUpdate_RollsBack() =>
        AssertFailureRollsBack("update",
            tx => tx.Update(Tier.Hot, ContentType.Memory, "id-1", new UpdateEntryOpts { Content = "z" }),
            store => SeedHot(store, "id-1", "x"));

    [Fact]
    public void FailureAtSetSticky_RollsBack() =>
        AssertFailureRollsBack("set-sticky",
            tx => tx.SetSticky(ContentType.Memory, "id-1", true),
            store => SeedHot(store, "id-1", "x"));

    [Fact]
    public void SetSticky_IsSticky_RoundTrip()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "x");
            using (var tx = store.BeginTransaction())
            {
                Assert.False(tx.IsSticky(ContentType.Memory, "id-1"));
                tx.SetSticky(ContentType.Memory, "id-1", true);
                Assert.True(tx.IsSticky(ContentType.Memory, "id-1"));
                tx.Commit();
            }

            Assert.True(store.IsSticky(ContentType.Memory, "id-1"));
        }
    }

    [Fact]
    public void SetSticky_RollsBackOnDispose()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "x");
            using (var tx = store.BeginTransaction())
            {
                tx.SetSticky(ContentType.Memory, "id-1", true);
            }

            Assert.False(store.IsSticky(ContentType.Memory, "id-1"));
        }
    }

    [Fact]
    public void LogImport_WritesSameRowAsImportLog()
    {
        var (connA, storeA) = NewStore();
        var (connB, _) = NewStore();
        using (connA)
        using (connB)
        {
            using (var tx = storeA.BeginTransaction())
            {
                tx.LogImport("claude-code", "/p/a.md", "hash-1", "entry-1", Tier.Warm, ContentType.Memory);
                tx.Commit();
            }
            new ImportLog(connB).LogImport(
                "claude-code", "/p/a.md", "hash-1", "entry-1", Tier.Warm, ContentType.Memory);

            Assert.Equal(
                StoreSnapshot.Take(connB, maskTimes: true),
                StoreSnapshot.Take(connA, maskTimes: true));
            Assert.True(new ImportLog(connA).IsAlreadyImported("hash-1"));
        }
    }

    [Fact]
    public void FailureAtLogImport_RollsBack() =>
        AssertFailureRollsBack("log-import",
            tx => tx.LogImport("t", "/p", "h", "e", Tier.Warm, ContentType.Memory));

    [Fact]
    public void LogImport_RollsBackOnDispose()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            using (var tx = store.BeginTransaction())
            {
                tx.LogImport("t", "/p", "h", "e", Tier.Warm, ContentType.Memory);
            }

            Assert.False(new ImportLog(conn).IsAlreadyImported("h"));
        }
    }
}
