using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// The writes the setup commands make to the sync state in the database: the identity of the repository this machine
/// joined, and the clearing of one repository's bookkeeping before another's is recorded. Plain SQL on the caller's
/// connection and transaction, so the caller makes the whole change atomic. Never touches a memory or knowledge row.
/// </summary>
public static class GitSyncStateStore
{
    /// <summary>
    /// Forgets what this machine knew about the previous repository: every row of <c>git_sync_entry</c>,
    /// <c>git_sync_issue</c> and <c>git_sync_alias</c>, the <c>git_sync_review</c> rows whose kind is not
    /// <c>conflict</c> (a conflict row holds the only copy of a losing text, so it stays), and the per-repository
    /// <c>_meta</c> keys. The clock keys (<c>git_sync_hlc</c>, <c>git_sync_clock_warning</c>) are kept.
    /// </summary>
    public static void ClearForNewRepository(MsSqliteConnection conn, SqliteTransaction tx)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);

        foreach (var sql in new[]
                 {
                     "DELETE FROM git_sync_entry",
                     "DELETE FROM git_sync_issue",
                     "DELETE FROM git_sync_alias",
                     "DELETE FROM git_sync_review WHERE kind <> 'conflict'",
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        foreach (var key in new[]
                 {
                     SyncIdentity.MetaKeys.LastSha, SyncIdentity.MetaKeys.LastOk, SyncIdentity.MetaKeys.LastError,
                     SyncIdentity.MetaKeys.Arrivals,
                 })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM _meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Upserts the remote and the host, and the repository id and machine id when given (a null keeps the stored one).
    /// </summary>
    public static void WriteIdentity(
        MsSqliteConnection conn, SqliteTransaction tx, string remote, string? repoId, string host, string? machine)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(host);

        Upsert(conn, tx, SyncIdentity.MetaKeys.Remote, remote);
        Upsert(conn, tx, SyncIdentity.MetaKeys.Host, host);
        if (repoId is not null)
            Upsert(conn, tx, SyncIdentity.MetaKeys.RepoId, repoId);
        if (machine is not null)
            Upsert(conn, tx, SyncIdentity.MetaKeys.Machine, machine);
    }

    private static void Upsert(MsSqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO _meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
