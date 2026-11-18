using Microsoft.Data.Sqlite;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>One open row of <c>git_sync_issue</c> of a kind (<c>apply-failed</c>, <c>rejected-record</c>).</summary>
internal sealed record IssueRow(string EntryId, string Kind, string? RecordHash, int Attempts, string? Detail);

/// <summary>
/// The <c>git_sync_issue</c> statements of the pull. Plain SQL on the caller's connection; every command carries the
/// optional transaction, so the statements run inside an entry's <see cref="SqliteTransaction"/>.
/// </summary>
internal static class GitSyncIssues
{
    public const string ApplyFailed = "apply-failed";
    public const string RejectedRecord = "rejected-record";

    /// <summary>Every row of a kind, by entry id (ordinal).</summary>
    public static Dictionary<string, IssueRow> ReadOpen(MsSqliteConnection conn, string kind, SqliteTransaction? tx = null)
    {
        var rows = new Dictionary<string, IssueRow>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT entry_id, kind, record_hash, attempts, detail FROM git_sync_issue WHERE kind = $kind";
        cmd.Parameters.AddWithValue("$kind", kind);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new IssueRow(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4));
            rows[row.EntryId] = row;
        }
        return rows;
    }

    /// <summary>One row, or null.</summary>
    public static IssueRow? Read(MsSqliteConnection conn, string entryId, string kind, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT entry_id, kind, record_hash, attempts, detail FROM git_sync_issue WHERE entry_id = $id AND kind = $kind";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$kind", kind);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return new IssueRow(
            r.GetString(0), r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.GetInt32(3),
            r.IsDBNull(4) ? null : r.GetString(4));
    }

    /// <summary>Inserts the row, or replaces the hash, attempts and detail of the row with the same <c>(entry_id, kind)</c>.</summary>
    public static void Upsert(
        MsSqliteConnection conn, string entryId, string kind, string? recordHash, int attempts, string? detail,
        SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO git_sync_issue (entry_id, kind, record_hash, attempts, detail) VALUES ($id, $kind, $hash, $attempts, $detail) " +
            "ON CONFLICT(entry_id, kind) DO UPDATE SET record_hash = excluded.record_hash, attempts = excluded.attempts, detail = excluded.detail";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$hash", (object?)recordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$attempts", attempts);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes the row; a missing row is a no-op.</summary>
    public static void Delete(MsSqliteConnection conn, string entryId, string kind, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM git_sync_issue WHERE entry_id = $id AND kind = $kind";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.ExecuteNonQuery();
    }
}

/// <summary><c>_meta</c> access inside a transaction (<c>SqliteStore.GetMeta</c> builds a command with no transaction and throws while one is open).</summary>
internal static class SyncSql
{
    public static string? GetMeta(MsSqliteConnection conn, string key, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public static void SetMeta(MsSqliteConnection conn, string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO _meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
