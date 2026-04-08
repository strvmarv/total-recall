using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Telemetry;

/// <summary>
/// Single-row write payload for <see cref="CompactionLog.LogEvent"/>. Mirrors
/// the <c>logCompactionEvent</c> shape in <c>src-ts/compaction/compactor.ts</c>
/// — the four analytic fields default to <c>null</c> because the TS writer
/// always inserts them as NULL; downstream analytics fills them in via a
/// later UPDATE that we have not yet ported.
/// </summary>
public sealed record CompactionLogEntry(
    string SessionId,
    string SourceTier,
    string? TargetTier,
    IReadOnlyList<string> SourceEntryIds,
    string? TargetEntryId,
    IReadOnlyDictionary<string, double> DecayScores,
    string Reason,
    string ConfigSnapshotId,
    double? SemanticDrift = null,
    int? FactsPreserved = null,
    int? FactsInOriginal = null,
    double? PreservationRatio = null);

/// <summary>
/// Slim row used by Plan 5 Task 5.3a's eval metrics aggregator. Only the
/// fields the metric pass touches are projected. Lives here (next to the
/// reader interface) so the read seam stays self-contained.
/// </summary>
public sealed record CompactionAnalyticsRow(
    string Id,
    long Timestamp,
    double? PreservationRatio,
    double? SemanticDrift);

/// <summary>
/// Read seam over <c>compaction_log</c> used by SessionLifecycle (Plan 4
/// Task 4.3) and the Plan 5 eval metrics aggregator. Unit tests can fake
/// the read surface without instantiating a real SQLite connection.
/// </summary>
public interface ICompactionLogReader
{
    /// <summary>
    /// Returns the maximum <c>timestamp</c> across all <c>compaction_log</c>
    /// rows whose <c>reason</c> is NOT equal to <paramref name="excludedReason"/>,
    /// or <c>null</c> if no such rows exist. Mirrors the TS query in
    /// <c>session-tools.ts</c>'s <c>getLastSessionAge</c>.
    /// </summary>
    long? GetLastTimestampExcludingReason(string excludedReason);

    /// <summary>
    /// Returns every compaction_log row projected to the small analytics
    /// shape. Optionally filtered to rows newer than <paramref name="sinceTimestamp"/>
    /// (inclusive). Used by the Plan 5 eval metrics aggregator.
    /// </summary>
    IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null);
}

/// <summary>
/// Append-only writer for the <c>compaction_log</c> table. Borrows a
/// non-owning <see cref="MsSqliteConnection"/>.
/// </summary>
public sealed class CompactionLog : ICompactionLogReader
{
    private readonly MsSqliteConnection _conn;

    public CompactionLog(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        _conn = conn;
    }

    /// <inheritdoc />
    public long? GetLastTimestampExcludingReason(string excludedReason)
    {
        ArgumentNullException.ThrowIfNull(excludedReason);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT MAX(timestamp) FROM compaction_log WHERE reason != $reason";
        cmd.Parameters.AddWithValue("$reason", excludedReason);
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return null;
        return result is long l ? l : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null)
    {
        using var cmd = _conn.CreateCommand();
        if (sinceTimestamp.HasValue)
        {
            cmd.CommandText =
                "SELECT id, timestamp, preservation_ratio, semantic_drift " +
                "FROM compaction_log WHERE timestamp >= $cutoff ORDER BY timestamp DESC";
            cmd.Parameters.AddWithValue("$cutoff", sinceTimestamp.Value);
        }
        else
        {
            cmd.CommandText =
                "SELECT id, timestamp, preservation_ratio, semantic_drift " +
                "FROM compaction_log ORDER BY timestamp DESC";
        }

        var rows = new List<CompactionAnalyticsRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CompactionAnalyticsRow(
                Id: reader.GetString(0),
                Timestamp: reader.GetInt64(1),
                PreservationRatio: reader.IsDBNull(2) ? null : reader.GetDouble(2),
                SemanticDrift: reader.IsDBNull(3) ? null : reader.GetDouble(3)));
        }
        return rows;
    }

    /// <summary>
    /// INSERT a new compaction_log row. Returns the generated UUID.
    /// </summary>
    public string LogEvent(CompactionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var id = Guid.NewGuid().ToString();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO compaction_log
  (id, timestamp, session_id, source_tier, target_tier, source_entry_ids,
   target_entry_id, semantic_drift, facts_preserved, facts_in_original,
   preservation_ratio, decay_scores, reason, config_snapshot_id)
VALUES
  ($id, $ts, $sid, $stier, $ttier, $sids,
   $teid, $drift, $fp, $fio,
   $ratio, $decay, $reason, $cfg)";

        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$sid", (object?)entry.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stier", entry.SourceTier);
        cmd.Parameters.AddWithValue("$ttier", (object?)entry.TargetTier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sids", JsonStringWriter.EncodeStringArray(entry.SourceEntryIds));
        cmd.Parameters.AddWithValue("$teid", (object?)entry.TargetEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$drift", (object?)entry.SemanticDrift ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fp", (object?)entry.FactsPreserved ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fio", (object?)entry.FactsInOriginal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ratio", (object?)entry.PreservationRatio ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$decay", EncodeDecayScores(entry.DecayScores));
        cmd.Parameters.AddWithValue("$reason", entry.Reason);
        cmd.Parameters.AddWithValue("$cfg", entry.ConfigSnapshotId);
        cmd.ExecuteNonQuery();

        return id;
    }

    /// <summary>
    /// Encode a <see cref="IReadOnlyDictionary{TKey, TValue}"/> of decay
    /// scores as a JSON object. Doubles are formatted with the round-trip
    /// "R" specifier under <see cref="CultureInfo.InvariantCulture"/> so the
    /// output is locale-independent and matches <c>JSON.stringify</c>.
    /// </summary>
    internal static string EncodeDecayScores(IReadOnlyDictionary<string, double> map)
    {
        if (map is null || map.Count == 0) return "{}";
        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        foreach (var kvp in map)
        {
            if (!first) sb.Append(',');
            JsonStringWriter.AppendEscaped(sb, kvp.Key);
            sb.Append(':');
            sb.Append(kvp.Value.ToString("R", CultureInfo.InvariantCulture));
            first = false;
        }
        sb.Append('}');
        return sb.ToString();
    }
}
