using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
/// Full-row projection of a <c>compaction_log</c> entry used by Plan 5
/// Task 5.5's <c>memory history</c> and <c>memory lineage</c> CLI verbs.
/// Mirrors the shape emitted by <c>memory_history</c> in
/// <c>src-ts/tools/extra-tools.ts</c>. Parsed JSON columns
/// (<c>source_entry_ids</c>, <c>decay_scores</c>) fall back to empty
/// collections on malformed input, matching the TS
/// <c>try { JSON.parse } catch { [] }</c> behavior.
/// </summary>
public sealed record CompactionMovementRow(
    string Id,
    long Timestamp,
    string? SessionId,
    string SourceTier,
    string? TargetTier,
    IReadOnlyList<string> SourceEntryIds,
    string? TargetEntryId,
    string Reason,
    IReadOnlyDictionary<string, double> DecayScores);

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

    /// <summary>
    /// Returns up to <paramref name="limit"/> <c>compaction_log</c> rows
    /// ordered by descending timestamp. Used by <c>memory history</c>.
    /// </summary>
    IReadOnlyList<CompactionMovementRow> GetRecentMovements(int limit);

    /// <summary>
    /// Returns the most recent <c>compaction_log</c> row whose
    /// <c>target_entry_id</c> equals <paramref name="targetEntryId"/>, or
    /// <c>null</c> if none. Used by <c>memory lineage</c>.
    /// </summary>
    CompactionMovementRow? GetByTargetEntryId(string targetEntryId);
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

    /// <inheritdoc />
    public IReadOnlyList<CompactionMovementRow> GetRecentMovements(int limit)
    {
        if (limit <= 0) return Array.Empty<CompactionMovementRow>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, timestamp, session_id, source_tier, target_tier, " +
            "       source_entry_ids, target_entry_id, reason, decay_scores " +
            "FROM compaction_log ORDER BY timestamp DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var rows = new List<CompactionMovementRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadMovementRow(reader));
        }
        return rows;
    }

    /// <inheritdoc />
    public CompactionMovementRow? GetByTargetEntryId(string targetEntryId)
    {
        ArgumentNullException.ThrowIfNull(targetEntryId);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, timestamp, session_id, source_tier, target_tier, " +
            "       source_entry_ids, target_entry_id, reason, decay_scores " +
            "FROM compaction_log WHERE target_entry_id = $id " +
            "ORDER BY timestamp DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id", targetEntryId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadMovementRow(reader);
    }

    private static CompactionMovementRow ReadMovementRow(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new CompactionMovementRow(
            Id: reader.GetString(0),
            Timestamp: reader.GetInt64(1),
            SessionId: reader.IsDBNull(2) ? null : reader.GetString(2),
            SourceTier: reader.GetString(3),
            TargetTier: reader.IsDBNull(4) ? null : reader.GetString(4),
            SourceEntryIds: ParseStringArray(reader.IsDBNull(5) ? null : reader.GetString(5)),
            TargetEntryId: reader.IsDBNull(6) ? null : reader.GetString(6),
            Reason: reader.GetString(7),
            DecayScores: ParseDoubleMap(reader.IsDBNull(8) ? null : reader.GetString(8)));
    }

    /// <summary>
    /// Parses a JSON string array (e.g. <c>["a","b"]</c>) into a list.
    /// Returns an empty list on null/malformed input, matching the TS
    /// <c>try { JSON.parse } catch { [] }</c> semantics at
    /// <c>src-ts/tools/extra-tools.ts:247-250</c>.
    /// </summary>
    internal static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            var list = new List<string>(doc.RootElement.GetArrayLength());
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (s is not null) list.Add(s);
                }
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Parses a JSON object of <c>string -> number</c> pairs into a
    /// dictionary. Returns an empty dictionary on null/malformed input.
    /// </summary>
    internal static IReadOnlyDictionary<string, double> ParseDoubleMap(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new Dictionary<string, double>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, double>();
            var dict = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number &&
                    prop.Value.TryGetDouble(out var d))
                {
                    dict[prop.Name] = d;
                }
            }
            return dict;
        }
        catch (JsonException)
        {
            return new Dictionary<string, double>();
        }
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
