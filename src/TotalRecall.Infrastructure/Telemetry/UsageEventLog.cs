// src/TotalRecall.Infrastructure/Telemetry/UsageEventLog.cs
//
// Writer/reader for the usage_events table. Mirrors the existing
// RetrievalEventLog pattern: thin wrapper around a borrowed (non-owning)
// SqliteConnection. Write path uses INSERT OR IGNORE against the
// UNIQUE (host, host_event_id) constraint so repeated indexer passes
// on overlapping transcript files are safe.

using System;
using TotalRecall.Infrastructure.Usage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Telemetry;

public sealed class UsageEventLog
{
    private readonly MsSqliteConnection _conn;

    public UsageEventLog(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        _conn = conn;
    }

    /// <summary>
    /// INSERT OR IGNORE a new usage_events row. Returns 1 on success,
    /// 0 if the (host, host_event_id) pair already existed.
    /// </summary>
    public int InsertOrIgnore(UsageEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO usage_events
  (host, host_event_id, session_id, interaction_id, turn_index, ts,
   project_path, project_repo, project_branch, project_commit,
   model,
   input_tokens, cache_creation_5m, cache_creation_1h, cache_read, output_tokens,
   service_tier, server_tool_use_json, host_request_id)
VALUES
  ($host, $heid, $sid, $iid, $ti, $ts,
   $pp, $pr, $pb, $pc,
   $model,
   $it, $cc5, $cc1, $cr, $ot,
   $st, $stu, $hrid)";

        cmd.Parameters.AddWithValue("$host", evt.Host);
        cmd.Parameters.AddWithValue("$heid", evt.HostEventId);
        cmd.Parameters.AddWithValue("$sid", evt.SessionId);
        cmd.Parameters.AddWithValue("$iid", (object?)evt.InteractionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ti", (object?)evt.TurnIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", evt.TimestampMs);
        cmd.Parameters.AddWithValue("$pp", (object?)evt.ProjectPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr", (object?)evt.ProjectRepo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pb", (object?)evt.ProjectBranch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pc", (object?)evt.ProjectCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)evt.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$it", (object?)evt.InputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cc5", (object?)evt.CacheCreation5m ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cc1", (object?)evt.CacheCreation1h ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cr", (object?)evt.CacheRead ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ot", (object?)evt.OutputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$st", (object?)evt.ServiceTier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stu", (object?)evt.ServerToolUseJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hrid", (object?)evt.HostRequestId ?? DBNull.Value);

        return cmd.ExecuteNonQuery();
    }

    /// <summary>Count of rows for the given host (debug/verbose CLI output).</summary>
    public int CountForHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM usage_events WHERE host = $h";
        cmd.Parameters.AddWithValue("$h", host);
        return (int)(long)cmd.ExecuteScalar()!;
    }
}
