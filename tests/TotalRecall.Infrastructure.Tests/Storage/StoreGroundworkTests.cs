using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.GitSync;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Storage;

/// <summary>
/// Git sync milestone 1b-i-2, store groundwork: the immediate store transaction, source tool and <c>created_at</c>
/// updates, vector replacement, and the sync-table statements that run inside a store transaction.
/// </summary>
public sealed class StoreGroundworkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tr-groundwork-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "total-recall.db");
    private readonly MsSqliteConnection _conn;
    private readonly SqliteStore _store;

    public StoreGroundworkTests()
    {
        _conn = SyncDb.OpenMigrated(DbPath);
        _store = new SqliteStore(_conn);
    }

    public void Dispose()
    {
        _conn.Dispose();
        MsSqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static float[] Embed(string text) => new FakeEmbedder().Embed(text);

    private long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private string? Text(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>A second connection to the same file that fails at once when the database is locked.</summary>
    private MsSqliteConnection Other()
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false, DefaultTimeout = 1 };
        var conn = new MsSqliteConnection(csb.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=0";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static void SetMetaOn(MsSqliteConnection conn, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO _meta (key, value) VALUES ('probe', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ---- the immediate transaction ----

    [Fact]
    public void BeginImmediateTransaction_TakesTheWriteLockAtOnce()
    {
        using var tx = _store.BeginImmediateTransaction();
        using var other = Other();

        var ex = Assert.Throws<SqliteException>(() => SetMetaOn(other, "from the other connection"));

        Assert.Equal(5, ex.SqliteErrorCode); // SQLITE_BUSY, before the first connection ran a single statement
    }

    [Fact]
    public void BeginImmediateTransaction_ReleasesTheLockOnCommitAndRollback()
    {
        using (var tx = _store.BeginImmediateTransaction())
            tx.Commit();
        using (var tx = _store.BeginImmediateTransaction())
        { /* disposed: rolled back */ }
        using var other = Other();

        SetMetaOn(other, "free"); // no exception

        Assert.Equal("free", Text("SELECT value FROM _meta WHERE key = 'probe'"));
    }

    [Fact]
    public void BeginImmediateTransaction_KeepsTheSingleOpenTransactionRule()
    {
        using var tx = _store.BeginImmediateTransaction();

        Assert.Throws<InvalidOperationException>(() => _store.BeginImmediateTransaction());
        Assert.Throws<InvalidOperationException>(() => _store.BeginTransaction());
    }

    // ---- source tool and created_at ----

    private void SeedHot(string id, SourceTool? tool = null) =>
        _store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("text", Id: id, SourceTool: tool, CreatedAt: 1_000L));

    [Fact]
    public void Update_SourceTool_SetsClearsAndLeavesAlone()
    {
        SeedHot("id-1");

        _store.Update(Tier.Hot, ContentType.Memory, "id-1", new UpdateEntryOpts { SourceTool = SourceTool.Cursor });
        Assert.Equal("cursor", Text("SELECT source_tool FROM hot_memories WHERE id = 'id-1'"));

        _store.Update(Tier.Hot, ContentType.Memory, "id-1", new UpdateEntryOpts { Content = "other" });
        Assert.Equal("cursor", Text("SELECT source_tool FROM hot_memories WHERE id = 'id-1'"));

        _store.Update(Tier.Hot, ContentType.Memory, "id-1", new UpdateEntryOpts { ClearSourceTool = true });
        Assert.Null(Text("SELECT source_tool FROM hot_memories WHERE id = 'id-1'"));
    }

    [Fact]
    public void Update_ClearSourceTool_WinsOverSourceTool()
    {
        SeedHot("id-1", SourceTool.ClaudeCode);

        _store.Update(Tier.Hot, ContentType.Memory, "id-1", new UpdateEntryOpts { SourceTool = SourceTool.Cursor, ClearSourceTool = true });

        Assert.Null(Text("SELECT source_tool FROM hot_memories WHERE id = 'id-1'"));
    }

    [Fact]
    public void Update_CreatedAt_SetsCreatedAt_AndLeavesTheOtherFields()
    {
        SeedHot("id-1", SourceTool.ClaudeCode);
        var before = Scalar("SELECT created_at FROM hot_memories WHERE id = 'id-1'");
        Assert.Equal(1_000L, before);

        _store.Update(Tier.Hot, ContentType.Memory, "id-1", new UpdateEntryOpts { CreatedAt = 5_000L });

        Assert.Equal(5_000L, Scalar("SELECT created_at FROM hot_memories WHERE id = 'id-1'"));
        Assert.Equal("text", Text("SELECT content FROM hot_memories WHERE id = 'id-1'"));
        Assert.Equal("claude-code", Text("SELECT source_tool FROM hot_memories WHERE id = 'id-1'"));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM hot_memories"));
    }

    [Fact]
    public void BeginImmediateTransaction_WhenAnotherConnectionHoldsTheLock_ThrowsBusy_AndLeavesTheStoreUsable()
    {
        using var fastConn = Other(); // fails at once instead of waiting out the command timeout
        var fastStore = new SqliteStore(fastConn);
        using (var hold = _conn.CreateCommand())
        {
            hold.CommandText = "BEGIN IMMEDIATE";
            hold.ExecuteNonQuery();
        }

        var ex = Assert.Throws<SqliteException>(() => fastStore.BeginImmediateTransaction());
        Assert.Equal(5, ex.SqliteErrorCode);

        using (var release = _conn.CreateCommand())
        {
            release.CommandText = "ROLLBACK";
            release.ExecuteNonQuery();
        }
        using var tx = fastStore.BeginImmediateTransaction(); // the failed begin left no open-transaction flag behind
        tx.Commit();
    }

    [Fact]
    public void Update_TheNewFields_AreNotSupportedByPostgres()
    {
        using var dataSource = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=none;Username=none;Password=none");
        var store = new PostgresStore(dataSource, "owner");

        foreach (var opts in new[]
                 {
                     new UpdateEntryOpts { SourceTool = SourceTool.Cursor },
                     new UpdateEntryOpts { ClearSourceTool = true },
                     new UpdateEntryOpts { CreatedAt = 1L },
                 })
            Assert.Throws<NotSupportedException>(() => store.Update(Tier.Hot, ContentType.Memory, "id", opts));
    }

    // ---- vector replacement ----

    [Fact]
    public void ReplaceEmbedding_ReplacesTheVector_AndInsertsOneForAnEntryWithNone()
    {
        SeedHot("with-vec");
        SeedHot("no-vec");
        using (var seed = _store.BeginImmediateTransaction())
        {
            seed.ReplaceEmbedding(Tier.Hot, ContentType.Memory, "with-vec", Embed("one"));
            seed.Commit();
        }
        Assert.Equal(1, Scalar("SELECT count(*) FROM hot_memories_vec"));
        var first = VectorBytes("with-vec");

        using (var tx = _store.BeginImmediateTransaction())
        {
            tx.ReplaceEmbedding(Tier.Hot, ContentType.Memory, "with-vec", Embed("a different text"));
            tx.ReplaceEmbedding(Tier.Hot, ContentType.Memory, "no-vec", Embed("two"));
            tx.Commit();
        }

        Assert.Equal(2, Scalar("SELECT count(*) FROM hot_memories_vec"));
        Assert.NotEqual(first, VectorBytes("with-vec"));
        Assert.NotNull(VectorBytes("no-vec"));
    }

    [Fact]
    public void ReplaceEmbedding_RolledBack_LeavesTheOldVector()
    {
        SeedHot("id-1");
        using (var seed = _store.BeginImmediateTransaction())
        {
            seed.ReplaceEmbedding(Tier.Hot, ContentType.Memory, "id-1", Embed("one"));
            seed.Commit();
        }
        var before = VectorBytes("id-1");

        using (var tx = _store.BeginImmediateTransaction())
            tx.ReplaceEmbedding(Tier.Hot, ContentType.Memory, "id-1", Embed("changed"));

        Assert.Equal(before, VectorBytes("id-1"));
    }

    [Fact]
    public void ReplaceEmbedding_OfAMissingEntry_Throws()
    {
        using var tx = _store.BeginImmediateTransaction();

        Assert.Throws<InvalidOperationException>(() => tx.ReplaceEmbedding(Tier.Hot, ContentType.Memory, "nope", Embed("x")));
    }

    private byte[]? VectorBytes(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT embedding FROM hot_memories_vec WHERE rowid = (SELECT rowid FROM hot_memories WHERE id = $id)";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as byte[];
    }

    // ---- sync-table statements inside the store transaction ----

    private static GitSyncRow Row(string id) =>
        new(id, SyncMerge.RowState.Active, "{}", "ch", "th", false, Tier.Warm, null, null, null, null, null, null, null, null);

    [Fact]
    public void SyncTableStatements_InsideAStoreTransaction_AreRolledBackWithIt()
    {
        using (var tx = _store.BeginImmediateTransaction())
        {
            GitSyncRows.Write(tx.Connection, Row("aaaa1111"), tx.Transaction);
            GitSyncIssues.Upsert(tx.Connection, "aaaa1111", GitSyncIssues.ApplyFailed, "hash", 1, "boom", tx.Transaction);
            SyncSql.SetMeta(tx.Connection, "k", "v", tx.Transaction);
            Assert.Equal("v", SyncSql.GetMeta(tx.Connection, "k", tx.Transaction));
            Assert.NotNull(GitSyncRows.Read(tx.Connection, "aaaa1111", tx.Transaction));
        }

        Assert.Empty(GitSyncRows.ReadAll(_conn));
        Assert.Empty(GitSyncIssues.ReadOpen(_conn, GitSyncIssues.ApplyFailed));
        Assert.Null(SyncSql.GetMeta(_conn, "k"));
    }

    [Fact]
    public void SyncTableStatements_InsideACommittedStoreTransaction_ArePresent()
    {
        using (var tx = _store.BeginImmediateTransaction())
        {
            GitSyncRows.Write(tx.Connection, Row("aaaa1111"), tx.Transaction);
            GitSyncIssues.Upsert(tx.Connection, "aaaa1111", GitSyncIssues.ApplyFailed, "hash", 1, "boom", tx.Transaction);
            tx.Commit();
        }

        Assert.Single(GitSyncRows.ReadAll(_conn));
        Assert.Equal("boom", GitSyncIssues.Read(_conn, "aaaa1111", GitSyncIssues.ApplyFailed)!.Detail);
    }

    [Fact]
    public void ReadAll_WithATransaction_SeesTheTransactionsWrites()
    {
        using var tx = _store.BeginImmediateTransaction();
        GitSyncRows.Write(tx.Connection, Row("aaaa1111"), tx.Transaction);

        Assert.Single(GitSyncRows.ReadAll(tx.Connection, tx.Transaction));
    }

    [Fact]
    public void ACommandWithoutTheTransaction_FailsLoudly_InsideAStoreTransaction()
    {
        using var tx = _store.BeginImmediateTransaction();

        Assert.Throws<InvalidOperationException>(() => GitSyncRows.ReadAll(tx.Connection));
    }
}
