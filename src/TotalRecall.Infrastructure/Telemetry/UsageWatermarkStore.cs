// src/TotalRecall.Infrastructure/Telemetry/UsageWatermarkStore.cs
//
// Per-host scan watermark store backing usage_watermarks table.
// Used by UsageIndexer to skip already-scanned events on repeated
// session_start passes. Unknown hosts return 0 (scan from beginning,
// which the indexer then bounds via config-driven initial_backfill_days).

using System;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Telemetry;

public sealed class UsageWatermarkStore
{
    private readonly MsSqliteConnection _conn;

    public UsageWatermarkStore(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        _conn = conn;
    }

    public long GetLastIndexedTs(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT last_indexed_ts FROM usage_watermarks WHERE host = $h";
        cmd.Parameters.AddWithValue("$h", host);
        var v = cmd.ExecuteScalar();
        return v is long l ? l : 0L;
    }

    public void SetLastIndexedTs(string host, long tsMs)
    {
        ArgumentNullException.ThrowIfNull(host);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO usage_watermarks (host, last_indexed_ts, last_scan_at, last_rollup_at)
VALUES ($h, $ts, $now, NULL)
ON CONFLICT(host) DO UPDATE SET
    last_indexed_ts = excluded.last_indexed_ts,
    last_scan_at    = excluded.last_scan_at";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$ts", tsMs);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public long GetLastRollupAt(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT last_rollup_at FROM usage_watermarks WHERE host = $h";
        cmd.Parameters.AddWithValue("$h", host);
        var v = cmd.ExecuteScalar();
        return v is long l ? l : 0L;
    }

    public void SetLastRollupAt(string host, long tsMs)
    {
        ArgumentNullException.ThrowIfNull(host);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO usage_watermarks (host, last_indexed_ts, last_scan_at, last_rollup_at)
VALUES ($h, 0, $now, $ts)
ON CONFLICT(host) DO UPDATE SET
    last_rollup_at = excluded.last_rollup_at";
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$ts", tsMs);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }
}
