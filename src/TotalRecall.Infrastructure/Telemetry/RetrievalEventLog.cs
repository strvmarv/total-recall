using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Telemetry;

/// <summary>
/// One result row in a logged retrieval event. Field names mirror the TS
/// shape exactly because the JSON-encoded form is wire-compatible across
/// runtimes during the migration.
/// </summary>
public sealed record RetrievalResultItem(
    string EntryId,
    string Tier,
    string ContentType,
    double Score,
    int Rank);

/// <summary>Insert payload for <see cref="RetrievalEventLog.LogEvent"/>.</summary>
public sealed record RetrievalEventEntry(
    string SessionId,
    string QueryText,
    string QuerySource,
    IReadOnlyList<RetrievalResultItem> Results,
    IReadOnlyList<string> TiersSearched,
    string ConfigSnapshotId,
    byte[]? QueryEmbedding = null,
    long? LatencyMs = null,
    int? TotalCandidatesScanned = null);

/// <summary>Update payload for <see cref="RetrievalEventLog.UpdateOutcome"/>.</summary>
public sealed record RetrievalOutcome(bool Used, string? Signal = null);

/// <summary>Optional filters for <see cref="RetrievalEventLog.GetEvents"/>.</summary>
public sealed record RetrievalEventQuery(
    string? SessionId = null,
    string? ConfigSnapshotId = null,
    int? Days = null,
    int? Limit = null);

/// <summary>
/// Read-side row returned by <see cref="RetrievalEventLog.GetEvents"/>. The
/// <c>ResultsJson</c> and <c>TiersSearchedJson</c> fields are returned as raw
/// JSON strings to match the TS surface — typed deserialization is left to
/// callers (YAGNI for Plan 3b).
/// </summary>
public sealed record RetrievalEventRow(
    string Id,
    long Timestamp,
    string SessionId,
    string QueryText,
    string QuerySource,
    byte[]? QueryEmbedding,
    string ResultsJson,
    int ResultCount,
    double? TopScore,
    string? TopTier,
    string? TopContentType,
    bool? OutcomeUsed,
    string? OutcomeSignal,
    string ConfigSnapshotId,
    long? LatencyMs,
    string TiersSearchedJson,
    int? TotalCandidatesScanned);

/// <summary>
/// Append-only writer (with one in-place outcome update) and reader for the
/// <c>retrieval_events</c> table. Ports <c>src-ts/eval/event-logger.ts</c>.
/// Borrows a non-owning <see cref="MsSqliteConnection"/>.
/// </summary>
public sealed class RetrievalEventLog
{
    private readonly MsSqliteConnection _conn;

    public RetrievalEventLog(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        _conn = conn;
    }

    /// <summary>
    /// INSERT a new retrieval_events row. Top-result fields are derived from
    /// <c>Results[0]</c> if non-empty; otherwise NULL. Returns the new id.
    /// </summary>
    public string LogEvent(RetrievalEventEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var id = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var resultCount = entry.Results.Count;
        RetrievalResultItem? top = resultCount > 0 ? entry.Results[0] : null;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO retrieval_events
  (id, timestamp, session_id, query_text, query_source, query_embedding,
   results, result_count, top_score, top_tier, top_content_type,
   config_snapshot_id, latency_ms, tiers_searched, total_candidates_scanned)
VALUES
  ($id, $ts, $sid, $qt, $qs, $qe,
   $res, $rc, $tscore, $ttier, $tct,
   $cfg, $lat, $tiers, $tcs)";

        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.Parameters.AddWithValue("$sid", entry.SessionId);
        cmd.Parameters.AddWithValue("$qt", entry.QueryText);
        cmd.Parameters.AddWithValue("$qs", entry.QuerySource);
        cmd.Parameters.AddWithValue("$qe", (object?)entry.QueryEmbedding ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$res", SerializeResults(entry.Results));
        cmd.Parameters.AddWithValue("$rc", resultCount);
        cmd.Parameters.AddWithValue("$tscore", top is null ? DBNull.Value : top.Score);
        cmd.Parameters.AddWithValue("$ttier", top is null ? DBNull.Value : top.Tier);
        cmd.Parameters.AddWithValue("$tct", top is null ? DBNull.Value : top.ContentType);
        cmd.Parameters.AddWithValue("$cfg", entry.ConfigSnapshotId);
        cmd.Parameters.AddWithValue("$lat", (object?)entry.LatencyMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tiers", CompactionLog.EncodeStringArray(entry.TiersSearched));
        cmd.Parameters.AddWithValue("$tcs", (object?)entry.TotalCandidatesScanned ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        return id;
    }

    /// <summary>
    /// UPDATE the outcome columns of an already-logged event. No-op if the
    /// id does not exist.
    /// </summary>
    public void UpdateOutcome(string eventId, RetrievalOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(outcome);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
UPDATE retrieval_events
   SET outcome_used = $used, outcome_signal = $signal
 WHERE id = $id";
        cmd.Parameters.AddWithValue("$used", outcome.Used ? 1 : 0);
        cmd.Parameters.AddWithValue("$signal", (object?)outcome.Signal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", eventId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// SELECT events with optional filters, ordered by <c>timestamp DESC</c>.
    /// </summary>
    public IReadOnlyList<RetrievalEventRow> GetEvents(RetrievalEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var sql = new StringBuilder();
        sql.Append("SELECT id, timestamp, session_id, query_text, query_source, query_embedding, ");
        sql.Append("results, result_count, top_score, top_tier, top_content_type, ");
        sql.Append("outcome_used, outcome_signal, config_snapshot_id, latency_ms, ");
        sql.Append("tiers_searched, total_candidates_scanned ");
        sql.Append("FROM retrieval_events");

        var clauses = new List<string>();
        if (query.SessionId is not null) clauses.Add("session_id = $sid");
        if (query.ConfigSnapshotId is not null) clauses.Add("config_snapshot_id = $cfg");
        if (query.Days is not null) clauses.Add("timestamp >= $cutoff");

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ");
            sql.Append(string.Join(" AND ", clauses));
        }

        sql.Append(" ORDER BY timestamp DESC");
        if (query.Limit is not null) sql.Append(" LIMIT $limit");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        if (query.SessionId is not null)
            cmd.Parameters.AddWithValue("$sid", query.SessionId);
        if (query.ConfigSnapshotId is not null)
            cmd.Parameters.AddWithValue("$cfg", query.ConfigSnapshotId);
        if (query.Days is not null)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-query.Days.Value).ToUnixTimeMilliseconds();
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
        }
        if (query.Limit is not null)
            cmd.Parameters.AddWithValue("$limit", query.Limit.Value);

        var rows = new List<RetrievalEventRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RetrievalEventRow(
                Id: reader.GetString(0),
                Timestamp: reader.GetInt64(1),
                SessionId: reader.GetString(2),
                QueryText: reader.GetString(3),
                QuerySource: reader.GetString(4),
                QueryEmbedding: reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                ResultsJson: reader.GetString(6),
                ResultCount: reader.GetInt32(7),
                TopScore: reader.IsDBNull(8) ? null : reader.GetDouble(8),
                TopTier: reader.IsDBNull(9) ? null : reader.GetString(9),
                TopContentType: reader.IsDBNull(10) ? null : reader.GetString(10),
                OutcomeUsed: reader.IsDBNull(11) ? null : reader.GetInt64(11) != 0,
                OutcomeSignal: reader.IsDBNull(12) ? null : reader.GetString(12),
                ConfigSnapshotId: reader.GetString(13),
                LatencyMs: reader.IsDBNull(14) ? null : reader.GetInt64(14),
                TiersSearchedJson: reader.GetString(15),
                TotalCandidatesScanned: reader.IsDBNull(16) ? null : reader.GetInt32(16)));
        }
        return rows;
    }

    /// <summary>
    /// Encode a list of <see cref="RetrievalResultItem"/> as a JSON array
    /// using snake_case keys to match the TS wire format.
    /// </summary>
    internal static string SerializeResults(IReadOnlyList<RetrievalResultItem> results)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < results.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = results[i];
            sb.Append('{');
            sb.Append("\"entry_id\":");
            JsonStringWriter.AppendEscaped(sb, r.EntryId);
            sb.Append(",\"tier\":");
            JsonStringWriter.AppendEscaped(sb, r.Tier);
            sb.Append(",\"content_type\":");
            JsonStringWriter.AppendEscaped(sb, r.ContentType);
            sb.Append(",\"score\":");
            sb.Append(r.Score.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(",\"rank\":");
            sb.Append(r.Rank.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }
}
