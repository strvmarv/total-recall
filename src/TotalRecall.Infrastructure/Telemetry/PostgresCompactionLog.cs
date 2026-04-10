using System;
using System.Collections.Generic;
using System.Globalization;
using Npgsql;

namespace TotalRecall.Infrastructure.Telemetry;

/// <summary>
/// Postgres-backed implementation of <see cref="ICompactionLogReader"/> and
/// the write surface (<see cref="LogEvent"/>). Mirrors
/// <see cref="CompactionLog"/> but uses <see cref="NpgsqlDataSource"/> and
/// positional parameters ($1, $2, …) instead of a SQLite connection.
/// The <c>compaction_log</c> table is created by
/// <c>PostgresMigrationRunner</c> before this class is instantiated.
/// </summary>
public sealed class PostgresCompactionLog : ICompactionLogReader
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresCompactionLog(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public long? GetLastTimestampExcludingReason(string excludedReason)
    {
        ArgumentNullException.ThrowIfNull(excludedReason);
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT MAX(timestamp) FROM compaction_log WHERE reason != $1";
        cmd.Parameters.AddWithValue(excludedReason);
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return null;
        return result is long l ? l : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        if (sinceTimestamp.HasValue)
        {
            cmd.CommandText =
                "SELECT id, timestamp, preservation_ratio, semantic_drift " +
                "FROM compaction_log WHERE timestamp >= $1 ORDER BY timestamp DESC";
            cmd.Parameters.AddWithValue(sinceTimestamp.Value);
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
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, timestamp, session_id, source_tier, target_tier, " +
            "       source_entry_ids::text, target_entry_id, reason, decay_scores::text " +
            "FROM compaction_log ORDER BY timestamp DESC LIMIT $1";
        cmd.Parameters.AddWithValue(limit);

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
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, timestamp, session_id, source_tier, target_tier, " +
            "       source_entry_ids::text, target_entry_id, reason, decay_scores::text " +
            "FROM compaction_log WHERE target_entry_id = $1 " +
            "ORDER BY timestamp DESC LIMIT 1";
        cmd.Parameters.AddWithValue(targetEntryId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadMovementRow(reader);
    }

    private static CompactionMovementRow ReadMovementRow(NpgsqlDataReader reader)
    {
        return new CompactionMovementRow(
            Id: reader.GetString(0),
            Timestamp: reader.GetInt64(1),
            SessionId: reader.IsDBNull(2) ? null : reader.GetString(2),
            SourceTier: reader.GetString(3),
            TargetTier: reader.IsDBNull(4) ? null : reader.GetString(4),
            SourceEntryIds: CompactionLog.ParseStringArray(reader.IsDBNull(5) ? null : reader.GetString(5)),
            TargetEntryId: reader.IsDBNull(6) ? null : reader.GetString(6),
            Reason: reader.GetString(7),
            DecayScores: CompactionLog.ParseDoubleMap(reader.IsDBNull(8) ? null : reader.GetString(8)));
    }

    /// <summary>
    /// INSERT a new compaction_log row. Returns the generated UUID.
    /// </summary>
    public string LogEvent(CompactionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var id = Guid.NewGuid().ToString();

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO compaction_log
  (id, timestamp, session_id, source_tier, target_tier, source_entry_ids,
   target_entry_id, semantic_drift, facts_preserved, facts_in_original,
   preservation_ratio, decay_scores, reason, config_snapshot_id)
VALUES
  ($1, $2, $3, $4, $5, $6::jsonb,
   $7, $8, $9, $10,
   $11, $12::jsonb, $13, $14)";

        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue((object?)entry.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.SourceTier);
        cmd.Parameters.AddWithValue((object?)entry.TargetTier ?? DBNull.Value);
        cmd.Parameters.AddWithValue(JsonStringWriter.EncodeStringArray(entry.SourceEntryIds));
        cmd.Parameters.AddWithValue((object?)entry.TargetEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.SemanticDrift ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.FactsPreserved ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.FactsInOriginal ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.PreservationRatio ?? DBNull.Value);
        cmd.Parameters.AddWithValue(CompactionLog.EncodeDecayScores(entry.DecayScores));
        cmd.Parameters.AddWithValue(entry.Reason);
        cmd.Parameters.AddWithValue(entry.ConfigSnapshotId);
        cmd.ExecuteNonQuery();

        return id;
    }
}
