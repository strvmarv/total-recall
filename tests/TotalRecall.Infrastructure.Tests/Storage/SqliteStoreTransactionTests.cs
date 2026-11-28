using System;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
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
                Project = "proj", DecayScore = 0.5, MetadataJson = "{\"k\":\"v\"}", Touch = true,
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
    public void FailureAtDeleteVec_RollsBack() =>
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

    // ---- Task 4: move and the all-steps sweep ----

    private static string VecHex(MsSqliteConnection conn, string vecTable)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT embedding FROM {vecTable}";
        return Convert.ToHexString((byte[])cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Move_CopiesRowAndVector()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "text");
            var startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using (var tx = store.BeginTransaction())
            {
                tx.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, "id-1");
                tx.Commit();
            }

            Assert.Null(store.Get(Tier.Hot, ContentType.Memory, "id-1"));
            var moved = store.Get(Tier.Warm, ContentType.Memory, "id-1")!;
            Assert.Equal(1_000L, moved.CreatedAt);
            Assert.True(moved.UpdatedAt >= startMs);
            Assert.Equal("text", moved.Content);
            Assert.Equal(new[] { "a" }, moved.Tags);
            Assert.Equal(0, ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec"));
            Assert.Equal(StoreSnapshot.Hex(Embed("text")), VecHex(conn, "warm_memories_vec"));
        }
    }

    [Fact]
    public void Move_WithEmbedding_UsesIt()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "text");
            var fresh = Embed("a much longer replacement text");
            using (var tx = store.BeginTransaction())
            {
                tx.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, "id-1", fresh);
                tx.Commit();
            }

            Assert.Equal(StoreSnapshot.Hex(fresh), VecHex(conn, "warm_memories_vec"));
        }
    }

    [Fact]
    public void Move_NoSourceVectorNoEmbedding_TargetHasNone_AndOrphanIsRemoved()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("x", Id: "id-1"));
            // Pre-plant an orphan vector row at the rowid the target row will get.
            using (var plant = conn.CreateCommand())
            {
                plant.CommandText = "INSERT INTO warm_memories_vec (rowid, embedding) VALUES (1, $e)";
                plant.Parameters.AddWithValue("$e",
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        new ReadOnlySpan<float>(Embed("orphan"))).ToArray());
                plant.ExecuteNonQuery();
            }

            using (var tx = store.BeginTransaction())
            {
                tx.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, "id-1");
                tx.Commit();
            }

            Assert.NotNull(store.Get(Tier.Warm, ContentType.Memory, "id-1"));
            Assert.Equal(0, ScalarLong(conn, "SELECT count(*) FROM warm_memories_vec"));
        }
    }

    [Fact]
    public void Move_MissingSource_ThrowsAndChangesNothing()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "x");
            var before = StoreSnapshot.Take(conn);
            using (var tx = store.BeginTransaction())
            {
                Assert.Throws<InvalidOperationException>(() =>
                    tx.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, "nope"));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
        }
    }

    [Fact]
    public void Move_StickyHotToWarmAndBack_DoesNotCarryStickyFlag()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "x");
            store.SetSticky(ContentType.Memory, "id-1", true);

            using (var tx = store.BeginTransaction())
            {
                tx.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, "id-1");
                tx.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, "id-1");
                tx.Commit();
            }

            Assert.NotNull(store.Get(Tier.Hot, ContentType.Memory, "id-1"));
            Assert.False(store.IsSticky(ContentType.Memory, "id-1"));
        }
    }

    [Fact]
    public void Move_SameTierAndType_ThrowsAndChangesNothing()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedHot(store, "id-1", "x");
            var before = StoreSnapshot.Take(conn);
            using (var tx = store.BeginTransaction())
            {
                Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
                    tx.Move(Tier.Hot, ContentType.Memory, Tier.Hot, ContentType.Memory, "id-1"));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
        }
    }

    [Fact]
    public void Move_MatchesStoreMoveAndMoveAndReEmbed()
    {
        var (connA, storeA) = NewStore();
        var (connB, storeB) = NewStore();
        using (connA)
        using (connB)
        {
            SeedHot(storeA, "id-1", "same text");
            SeedHot(storeB, "id-1", "same text");

            using (var tx = storeA.BeginTransaction())
            {
                tx.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory,
                    "id-1", Embed("same text"));
                tx.Commit();
            }
            var entry = storeB.Get(Tier.Hot, ContentType.Memory, "id-1")!;
            MoveHelpers.MoveAndReEmbed(
                storeB, new VectorSearch(connB), new FakeEmbedder(), entry,
                Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory);

            Assert.Equal(
                StoreSnapshot.Take(connB, maskTimes: true),
                StoreSnapshot.Take(connA, maskTimes: true));
        }
    }

    [Theory]
    [InlineData("move-copy")]
    [InlineData("move-vec")]
    [InlineData("delete-vec")]
    [InlineData("delete-row")]
    public void FailureAtEachMoveStep_RollsBack(string step) =>
        AssertFailureRollsBack(step,
            tx => tx.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, "id-1"),
            store => SeedHot(store, "id-1", "x"));

    // ---- git sync alias convergence: RekeyMemory (#165, D3 step 2) ----

    private const string OldId = "3f9c2a7e-0000-4000-8000-0000000000b2";
    private const string NewId = "3f9c2a7e-0000-4000-8000-0000000000a1";

    /// <summary>Every column of the row with this id except <c>id</c>, as one line (blobs as hex), or null.</summary>
    private static string? RawRowExceptId(MsSqliteConnection conn, string table, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < r.FieldCount; i++)
        {
            if (r.GetName(i) == "id") continue;
            sb.Append(r.GetName(i)).Append('=');
            sb.Append(r.IsDBNull(i) ? "NULL"
                : r.GetValue(i) is byte[] b ? Convert.ToHexString(b)
                : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('|');
        }
        return sb.ToString();
    }

    private static string? IdAtFtsMatch(MsSqliteConnection conn, string table, string word)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT t.id FROM {table}_fts f JOIN {table} t ON t.rowid = f.rowid WHERE {table}_fts MATCH $w";
        cmd.Parameters.AddWithValue("$w", word);
        return cmd.ExecuteScalar() as string;
    }

    private static string? ParentOf(MsSqliteConnection conn, string table, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT parent_id FROM {table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    private static void SeedRekeySource(SqliteStore store, MsSqliteConnection conn)
    {
        store.InsertWithEmbedding(Tier.Hot, ContentType.Memory,
            new InsertEntryOpts("Prefer zebracorn builds.", Summary: "sum", Source: "src.md",
                Tags: new[] { "a", "b" }, MetadataJson: """{"k":"v"}""", Id: OldId, CreatedAt: 1_000L),
            Embed("Prefer zebracorn builds."));
        store.SetSticky(ContentType.Memory, OldId, true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE hot_memories SET access_count = 7, decay_score = 0.25 WHERE id = '{OldId}'";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void RekeyMemory_KeepsRowidVectorFtsAndEveryColumn()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedRekeySource(store, conn);
            var rowBefore = RawRowExceptId(conn, "hot_memories", OldId);
            var rowid = ScalarLong(conn, $"SELECT rowid FROM hot_memories WHERE id = '{OldId}'");
            var vecCount = ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec");
            var vecBytes = VecRows.ReadBytes(conn, null, "hot_memories_vec", rowid);

            using (var tx = store.BeginTransaction())
            {
                tx.RekeyMemory(Tier.Hot, OldId, NewId);
                tx.Commit();
            }

            Assert.NotNull(store.Get(Tier.Hot, ContentType.Memory, NewId));
            Assert.Null(store.Get(Tier.Hot, ContentType.Memory, OldId));
            Assert.Equal(rowBefore, RawRowExceptId(conn, "hot_memories", NewId));
            Assert.Equal(rowid, ScalarLong(conn, $"SELECT rowid FROM hot_memories WHERE id = '{NewId}'"));
            Assert.Equal(vecCount, ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec"));
            Assert.Equal(vecBytes, VecRows.ReadBytes(conn, null, "hot_memories_vec", rowid));
            Assert.Equal(NewId, IdAtFtsMatch(conn, "hot_memories", "zebracorn"));
            Assert.True(store.IsSticky(ContentType.Memory, NewId));
            var hit = Assert.Single(new VectorSearch(conn).SearchByVector(
                Tier.Hot, ContentType.Memory, Embed("Prefer zebracorn builds."), new VectorSearchOpts(5)));
            Assert.Equal(NewId, hit.Id);
            var fts = Assert.Single(new FtsSearch(conn).SearchByFts(
                Tier.Hot, ContentType.Memory, "zebracorn", new FtsSearchOpts(5)));
            Assert.Equal(NewId, fts.Id);
        }
    }

    [Fact]
    public void RekeyMemory_RewritesParentIdInEveryContentTable()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedRekeySource(store, conn);
            store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts("child one", ParentId: OldId, Id: "child-1"));
            store.Insert(Tier.Cold, ContentType.Knowledge, new InsertEntryOpts("child two", ParentId: OldId, Id: "child-2"));
            store.Insert(Tier.Hot, ContentType.Knowledge, new InsertEntryOpts("other", ParentId: "someone-else", Id: "child-3"));

            using (var tx = store.BeginTransaction())
            {
                tx.RekeyMemory(Tier.Hot, OldId, NewId);
                tx.Commit();
            }

            Assert.Equal(NewId, ParentOf(conn, "warm_memories", "child-1"));
            Assert.Equal(NewId, ParentOf(conn, "cold_knowledge", "child-2"));
            Assert.Equal("someone-else", ParentOf(conn, "hot_knowledge", "child-3"));
            Assert.Equal(0, ScalarLong(conn,
                "SELECT (SELECT count(*) FROM hot_memories WHERE parent_id = '" + OldId + "') + " +
                "(SELECT count(*) FROM warm_memories WHERE parent_id = '" + OldId + "') + " +
                "(SELECT count(*) FROM cold_knowledge WHERE parent_id = '" + OldId + "')"));
        }
    }

    [Fact]
    public void RekeyMemory_RefusesAMissingSourceOrATakenTarget()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedRekeySource(store, conn);
            store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts("child", ParentId: OldId, Id: "child-1"));
            store.Insert(Tier.Warm, ContentType.Knowledge, new InsertEntryOpts("taken", Id: "taken-id"));
            var before = StoreSnapshot.Take(conn);

            using (var tx = store.BeginTransaction())
            {
                // absent in the given tier (it is in hot, not warm), and absent everywhere
                Assert.Throws<InvalidOperationException>(() => tx.RekeyMemory(Tier.Warm, OldId, NewId));
                Assert.Throws<InvalidOperationException>(() => tx.RekeyMemory(Tier.Hot, "no-such-id", NewId));
                // the target is an id in another table (knowledge), or the same table
                Assert.Throws<InvalidOperationException>(() => tx.RekeyMemory(Tier.Hot, OldId, "taken-id"));
                Assert.Throws<InvalidOperationException>(() => tx.RekeyMemory(Tier.Hot, OldId, "child-1"));
                Assert.Throws<InvalidOperationException>(() => tx.RekeyMemory(Tier.Hot, OldId, OldId));
                Assert.Equal(before, StoreSnapshot.Take(conn));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
            Assert.Equal(OldId, IdAtFtsMatch(conn, "hot_memories", "zebracorn"));
        }
    }

    [Fact]
    public void AFaultAfterTheRekey_RollsTheRekeyBack()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            SeedRekeySource(store, conn);
            store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts("child one", ParentId: OldId, Id: "child-1"));
            store.Insert(Tier.Cold, ContentType.Knowledge, new InsertEntryOpts("child two", ParentId: OldId, Id: "child-2"));
            var before = StoreSnapshot.Take(conn);

            using (var tx = store.BeginTransaction())
            {
                tx.BeforeStep = FailAt("rekey-parent");
                Assert.Throws<InvalidOperationException>(() => tx.RekeyMemory(Tier.Hot, OldId, NewId));
            }

            Assert.Equal(before, StoreSnapshot.Take(conn));
            Assert.NotNull(store.Get(Tier.Hot, ContentType.Memory, OldId));
            Assert.Null(store.Get(Tier.Hot, ContentType.Memory, NewId));
            Assert.Equal(OldId, IdAtFtsMatch(conn, "hot_memories", "zebracorn"));
            Assert.Equal(OldId, ParentOf(conn, "warm_memories", "child-1"));
            Assert.Equal(OldId, ParentOf(conn, "cold_knowledge", "child-2"));
        }
    }

    [Theory]
    [InlineData("rekey-row")]
    [InlineData("rekey-parent")]
    public void FailureAtEachRekeyStep_RollsBack(string step) =>
        AssertFailureRollsBack(step,
            tx => tx.RekeyMemory(Tier.Hot, "id-1", "id-2"),
            store => SeedHot(store, "id-1", "x"));

    // One transaction that uses every operation, so the sweep covers every
    // step in every operation.
    private static void RunSweep(SqliteStoreTransaction tx)
    {
        tx.Insert(Tier.Hot, ContentType.Memory,
            new InsertEntryOpts("sweep", Id: "s1"), Embed("sweep"));
        tx.Update(Tier.Hot, ContentType.Memory, "s1", new UpdateEntryOpts { Content = "sweep2" });
        tx.SetSticky(ContentType.Memory, "s1", true);
        tx.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, "s1");
        tx.LogImport("t", "/p", "h", "s1", Tier.Warm, ContentType.Memory);
        tx.Delete(Tier.Warm, ContentType.Memory, "s1");
    }

    [Fact]
    public void AllStepsSweep_EveryFailurePointRollsBackEverything()
    {
        // 1. A clean run: count the steps and check the committed result.
        int total;
        {
            var (conn, store) = NewStore();
            using (conn)
            {
                var count = 0;
                using (var tx = store.BeginTransaction())
                {
                    tx.BeforeStep = _ => count++;
                    RunSweep(tx);
                    tx.Commit();
                }
                total = count;

                Assert.Equal(11, total);
                Assert.Null(store.Get(Tier.Hot, ContentType.Memory, "s1"));
                Assert.Null(store.Get(Tier.Warm, ContentType.Memory, "s1"));
                Assert.Equal(0, ScalarLong(conn, "SELECT count(*) FROM hot_memories_vec"));
                Assert.Equal(0, ScalarLong(conn, "SELECT count(*) FROM warm_memories_vec"));
                Assert.True(new ImportLog(conn).IsAlreadyImported("h"));
            }
        }

        // 2. Fail at each step in turn: the whole transaction rolls back.
        for (var k = 1; k <= total; k++)
        {
            var (conn, store) = NewStore();
            using (conn)
            {
                var before = StoreSnapshot.Take(conn);
                var seen = 0;
                using (var tx = store.BeginTransaction())
                {
                    var failAt = k;
                    tx.BeforeStep = _ =>
                    {
                        if (++seen == failAt)
                            throw new InvalidOperationException("injected at step " + failAt);
                    };
                    Assert.Throws<InvalidOperationException>(() => RunSweep(tx));
                }

                Assert.Equal(before, StoreSnapshot.Take(conn));
            }
        }
    }
}
