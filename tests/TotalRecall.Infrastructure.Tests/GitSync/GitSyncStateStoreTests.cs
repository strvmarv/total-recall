using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-gsss-" + Guid.NewGuid().ToString("N"));
    private readonly MsSqliteConnection _conn;

    public GitSyncStateStoreTests() => _conn = SyncDb.OpenMigrated(Path.Combine(_root, "t.db"));

    public void Dispose()
    {
        _conn.Dispose();
        MsSqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private void Seed()
    {
        SyncDb.Exec(_conn, "INSERT INTO git_sync_entry (entry_id, state) VALUES ('e1','active'), ('e2','left')");
        SyncDb.Exec(_conn, "INSERT INTO git_sync_issue (entry_id, kind) VALUES ('e1','apply-failed')");
        SyncDb.Exec(_conn, "INSERT INTO git_sync_alias (canonical_id, other_id, state) VALUES ('a','b','open')");
        SyncDb.Exec(_conn, "INSERT INTO git_sync_review (entry_id, kind, state, arrived_at) VALUES ('e1','new-pin','open',1), ('e1','conflict','open',1), ('e2','held-delete','unseen',1)");
        foreach (var key in new[] { "git_sync_last_sha", "git_sync_last_ok", "git_sync_last_error", "git_sync_arrivals", "git_sync_hlc", "git_sync_clock_warning", "git_sync_machine" })
            SyncDb.Exec(_conn, "INSERT INTO _meta (key, value) VALUES ($k, 'v')", ("$k", key));
        SyncDb.Exec(_conn, "INSERT INTO hot_memories (id, content, created_at, updated_at, last_accessed_at) VALUES ('m1','kept',1,1,1)");
    }

    private void Run(Action<SqliteTransaction> body)
    {
        using var tx = _conn.BeginTransaction();
        body(tx);
        tx.Commit();
    }

    [Fact]
    public void ClearForNewRepository_EmptiesTheStateKeepsConflictsClocksAndEveryMemory()
    {
        Seed();
        Run(tx => GitSyncStateStore.ClearForNewRepository(_conn, tx));

        Assert.Equal(0, SyncDb.Count(_conn, "git_sync_entry"));
        Assert.Equal(0, SyncDb.Count(_conn, "git_sync_issue"));
        Assert.Equal(0, SyncDb.Count(_conn, "git_sync_alias"));
        Assert.Equal(1, SyncDb.Count(_conn, "git_sync_review"));
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT kind FROM git_sync_review";
            Assert.Equal("conflict", cmd.ExecuteScalar());
        }
        foreach (var key in new[] { "git_sync_last_sha", "git_sync_last_ok", "git_sync_last_error", "git_sync_arrivals" })
            Assert.Null(SyncDb.Meta(_conn, key));
        Assert.Equal("v", SyncDb.Meta(_conn, "git_sync_hlc"));
        Assert.Equal("v", SyncDb.Meta(_conn, "git_sync_clock_warning"));
        Assert.Equal("v", SyncDb.Meta(_conn, "git_sync_machine"));
        // a hot memory with no vector row is still there: nothing here sweeps content rows
        Assert.Equal(1, SyncDb.Count(_conn, "hot_memories"));
    }

    [Fact]
    public void WriteIdentity_WritesTheKeys_AndANullKeepsTheStoredOne()
    {
        Run(tx => GitSyncStateStore.WriteIdentity(_conn, tx, "git@h:o/r", "aaaa", "hostfp", "01234567"));
        Assert.Equal("git@h:o/r", SyncDb.Meta(_conn, SyncIdentity.MetaKeys.Remote));
        Assert.Equal("aaaa", SyncDb.Meta(_conn, SyncIdentity.MetaKeys.RepoId));
        Assert.Equal("hostfp", SyncDb.Meta(_conn, SyncIdentity.MetaKeys.Host));
        Assert.Equal("01234567", SyncDb.Meta(_conn, SyncIdentity.MetaKeys.Machine));

        Run(tx => GitSyncStateStore.WriteIdentity(_conn, tx, "ssh://h/o/r", null, "hostfp2", null));
        Assert.Equal("ssh://h/o/r", SyncDb.Meta(_conn, SyncIdentity.MetaKeys.Remote));
        Assert.Equal("aaaa", SyncDb.Meta(_conn, SyncIdentity.MetaKeys.RepoId));
        Assert.Equal("hostfp2", SyncDb.Meta(_conn, SyncIdentity.MetaKeys.Host));
        Assert.Equal("01234567", SyncDb.Meta(_conn, SyncIdentity.MetaKeys.Machine));
    }

    [Fact]
    public void ClearAndWriteInOneTransaction_RollBackAsAWhole()
    {
        Seed();
        using (var tx = _conn.BeginTransaction())
        {
            GitSyncStateStore.ClearForNewRepository(_conn, tx);
            GitSyncStateStore.WriteIdentity(_conn, tx, "r", "id", "h", "m");
            tx.Rollback();
        }
        Assert.Equal(2, SyncDb.Count(_conn, "git_sync_entry"));
        Assert.Equal(3, SyncDb.Count(_conn, "git_sync_review"));
        Assert.Equal("v", SyncDb.Meta(_conn, "git_sync_last_sha"));
        Assert.Null(SyncDb.Meta(_conn, SyncIdentity.MetaKeys.Remote));
        Assert.Null(SyncDb.Meta(_conn, SyncIdentity.MetaKeys.RepoId));
    }
}
