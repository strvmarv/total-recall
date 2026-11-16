using Microsoft.Data.Sqlite;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncScanner
{
    /// <summary>
    /// Writes the whole plan in one <c>BEGIN IMMEDIATE</c> transaction (decision 13): the rows, the issue rows, the
    /// clock and the warning. A failure (or a lock timeout on <c>BEGIN IMMEDIATE</c>) leaves everything as it was.
    /// </summary>
    private void Write(ScanPlan plan)
    {
        Exec("BEGIN IMMEDIATE");
        WriteTransactionCount++;
        try
        {
            BeforeWrite?.Invoke("rows");
            foreach (var row in plan.Rows)
                GitSyncRows.Write(_conn, row);

            BeforeWrite?.Invoke("issues");
            foreach (var (id, detail) in plan.IssueUpserts)
                UpsertIneligibleIssue(id, detail);
            foreach (var id in plan.IssueDeletes)
                DeleteIneligibleIssue(id);

            BeforeWrite?.Invoke("clock");
            if (plan.ClockText is not null)
                SetMeta(SyncIdentity.MetaKeys.Hlc, plan.ClockText);

            BeforeWrite?.Invoke("warning");
            if (plan.WarningText is not null)
                SetMeta(SyncIdentity.MetaKeys.ClockWarning, plan.WarningText);

            Exec("COMMIT");
        }
        catch
        {
            try { Exec("ROLLBACK"); } catch (SqliteException) { }
            throw;
        }
    }

    private void UpsertIneligibleIssue(string id, string detail)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO git_sync_issue (entry_id, kind, record_hash, attempts, detail) VALUES ($id, 'ineligible-id', NULL, 0, $detail) " +
            "ON CONFLICT(entry_id, kind) DO UPDATE SET detail = excluded.detail";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$detail", detail);
        cmd.ExecuteNonQuery();
    }

    private void DeleteIneligibleIssue(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM git_sync_issue WHERE entry_id = $id AND kind = 'ineligible-id'";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO _meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
