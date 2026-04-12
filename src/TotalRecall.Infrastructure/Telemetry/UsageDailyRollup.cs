// src/TotalRecall.Infrastructure/Telemetry/UsageDailyRollup.cs
//
// Rolling aggregation — takes events older than the cutoff (default 30
// days), groups by (day_utc, host, model, project), INSERT OR REPLACE-es
// into usage_daily, then deletes the source rows. The whole operation
// is wrapped in a transaction so partial failures don't leave a split
// state (some events rolled up but not deleted, or vice versa).
//
// Idempotency: INSERT OR REPLACE on the composite PK means re-running
// is safe. If a rollup is interrupted between INSERT and DELETE, the
// next run re-inserts the same aggregated values (no change) and then
// deletes. Correctness holds as long as cutoff only moves forward.

using System;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Telemetry;

public sealed record UsageRollupResult(int EventsAggregated, int DailyRowsWritten);

public sealed class UsageDailyRollup
{
    private readonly MsSqliteConnection _conn;

    public UsageDailyRollup(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        _conn = conn;
    }

    /// <summary>
    /// Roll up all events with ts &lt; cutoffMs into usage_daily, then
    /// delete them from usage_events. Returns (events aggregated, daily
    /// rows written after dedup).
    /// </summary>
    public UsageRollupResult RollupOlderThan(long cutoffMs)
    {
        using var tx = _conn.BeginTransaction();

        int eventCount;
        using (var countCmd = _conn.CreateCommand())
        {
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM usage_events WHERE ts < $cutoff";
            countCmd.Parameters.AddWithValue("$cutoff", cutoffMs);
            eventCount = (int)(long)countCmd.ExecuteScalar()!;
        }

        if (eventCount == 0)
        {
            tx.Commit();
            return new UsageRollupResult(0, 0);
        }

        // Aggregate into usage_daily. day_utc is stored as unix seconds
        // (start-of-day UTC) to match the schema comment; use integer
        // arithmetic on milliseconds to floor to the day.
        int dailyRows;
        using (var aggCmd = _conn.CreateCommand())
        {
            aggCmd.Transaction = tx;
            aggCmd.CommandText = @"
INSERT OR REPLACE INTO usage_daily
  (day_utc, host, model, project,
   session_count, turn_count,
   input_tokens, cache_creation_tokens, cache_read_tokens, output_tokens)
SELECT
  (ts / 86400000) * 86400 AS day_utc,
  host,
  model,
  COALESCE(project_repo, project_path) AS project,
  COUNT(DISTINCT session_id),
  COUNT(*),
  SUM(input_tokens),
  SUM(COALESCE(cache_creation_5m, 0) + COALESCE(cache_creation_1h, 0)),
  SUM(cache_read),
  SUM(output_tokens)
FROM usage_events
WHERE ts < $cutoff
GROUP BY day_utc, host, model, project";
            aggCmd.Parameters.AddWithValue("$cutoff", cutoffMs);
            dailyRows = aggCmd.ExecuteNonQuery();
        }

        // Delete the source events
        using (var delCmd = _conn.CreateCommand())
        {
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM usage_events WHERE ts < $cutoff";
            delCmd.Parameters.AddWithValue("$cutoff", cutoffMs);
            delCmd.ExecuteNonQuery();
        }

        tx.Commit();
        return new UsageRollupResult(eventCount, dailyRows);
    }
}
