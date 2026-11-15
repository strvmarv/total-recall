using System;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
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
}
