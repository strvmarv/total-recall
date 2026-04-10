// src/TotalRecall.Infrastructure/Usage/UsageQueryService.cs
//
// Single read path for UsageCommand, usage_status MCP tool, and
// QuotaNudgeComposer. Phase 1 queries usage_events only; Phase 2
// extends the CTE to UNION ALL with usage_daily via a dynamic
// rollup_cutoff read from usage_watermarks. See spec §6.2.
//
// Null handling: SUM() ignores nulls by default in SQLite, so a
// bucket containing a mix of Claude Code (full token data) and
// Copilot CLI (output_tokens only) events produces InputTokens =
// "only the Claude Code sum". UsageReport exposes coverage counts
// separately so callers can caveat "X tokens across Y of Z sessions".

using System;
using System.Collections.Generic;
using System.Text;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Usage;

public sealed class UsageQueryService
{
    private readonly MsSqliteConnection _conn;

    public UsageQueryService(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        _conn = conn;
    }

    public UsageReport Query(UsageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var startMs = query.Start.ToUnixTimeMilliseconds();
        var endMs = query.End.ToUnixTimeMilliseconds();

        var keyExpr = query.GroupBy switch
        {
            GroupBy.None    => "'_total_'",
            GroupBy.Host    => "host",
            GroupBy.Project => "COALESCE(project_repo, project_path, '(none)')",
            GroupBy.Day     => "strftime('%Y-%m-%d', ts/1000, 'unixepoch')",
            GroupBy.Model   => "COALESCE(model, '(unknown)')",
            GroupBy.Session => "session_id",
            _ => "host",
        };

        var sql = new StringBuilder();
        sql.Append($@"
SELECT
    {keyExpr} AS bucket_key,
    COUNT(DISTINCT session_id) AS session_count,
    COUNT(*)                   AS turn_count,
    SUM(input_tokens)          AS input_tokens,
    SUM(COALESCE(cache_creation_5m,0) + COALESCE(cache_creation_1h,0)) AS cache_creation_tokens,
    SUM(cache_read)            AS cache_read_tokens,
    SUM(output_tokens)         AS output_tokens
FROM usage_events
WHERE ts BETWEEN $start AND $end");

        if (query.HostFilter is { Count: > 0 })
        {
            sql.Append(" AND host IN (");
            for (var i = 0; i < query.HostFilter.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append($"$h{i}");
            }
            sql.Append(")");
        }

        if (query.ProjectFilter is { Count: > 0 })
        {
            sql.Append(" AND COALESCE(project_repo, project_path) IN (");
            for (var i = 0; i < query.ProjectFilter.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append($"$p{i}");
            }
            sql.Append(")");
        }

        sql.Append($" GROUP BY {keyExpr}");
        sql.Append(" ORDER BY output_tokens DESC NULLS LAST");
        if (query.TopN > 0) sql.Append($" LIMIT {query.TopN}");

        var buckets = new List<UsageBucket>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = sql.ToString();
            cmd.Parameters.AddWithValue("$start", startMs);
            cmd.Parameters.AddWithValue("$end", endMs);
            if (query.HostFilter is { } hf)
            {
                for (var i = 0; i < hf.Count; i++)
                    cmd.Parameters.AddWithValue($"$h{i}", hf[i]);
            }
            if (query.ProjectFilter is { } pf)
            {
                for (var i = 0; i < pf.Count; i++)
                    cmd.Parameters.AddWithValue($"$p{i}", pf[i]);
            }
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var totals = new UsageTotals(
                    SessionCount: r.GetInt32(1),
                    TurnCount: r.GetInt64(2),
                    InputTokens: r.IsDBNull(3) ? null : r.GetInt64(3),
                    CacheCreationTokens: r.IsDBNull(4) ? null : r.GetInt64(4),
                    CacheReadTokens: r.IsDBNull(5) ? null : r.GetInt64(5),
                    OutputTokens: r.IsDBNull(6) ? null : r.GetInt64(6));
                buckets.Add(new UsageBucket(
                    Key: r.IsDBNull(0) ? "(null)" : r.GetString(0),
                    Totals: totals));
            }
        }

        var grand = QueryGrandTotal(startMs, endMs, query);
        var (full, partial) = QueryCoverage(startMs, endMs, query);

        return new UsageReport(
            Start: query.Start,
            End: query.End,
            Buckets: buckets,
            GrandTotal: grand,
            SessionsWithFullTokenData: full,
            SessionsWithPartialTokenData: partial);
    }

    private UsageTotals QueryGrandTotal(long startMs, long endMs, UsageQuery query)
    {
        var sql = new StringBuilder();
        sql.Append(@"
SELECT
    COUNT(DISTINCT session_id),
    COUNT(*),
    SUM(input_tokens),
    SUM(COALESCE(cache_creation_5m,0) + COALESCE(cache_creation_1h,0)),
    SUM(cache_read),
    SUM(output_tokens)
FROM usage_events
WHERE ts BETWEEN $start AND $end");
        AppendFilters(sql, query);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$start", startMs);
        cmd.Parameters.AddWithValue("$end", endMs);
        BindFilters(cmd, query);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return new UsageTotals(0, 0, null, null, null, null);
        return new UsageTotals(
            SessionCount: r.GetInt32(0),
            TurnCount: r.GetInt64(1),
            InputTokens: r.IsDBNull(2) ? null : r.GetInt64(2),
            CacheCreationTokens: r.IsDBNull(3) ? null : r.GetInt64(3),
            CacheReadTokens: r.IsDBNull(4) ? null : r.GetInt64(4),
            OutputTokens: r.IsDBNull(5) ? null : r.GetInt64(5));
    }

    private (int full, int partial) QueryCoverage(long startMs, long endMs, UsageQuery query)
    {
        // A "full" session has at least one event with input_tokens NOT NULL.
        // A "partial" session has NO events with input_tokens but at least one with output_tokens.
        var sql = new StringBuilder();
        sql.Append(@"
WITH per_session AS (
    SELECT
        session_id,
        MAX(CASE WHEN input_tokens IS NOT NULL THEN 1 ELSE 0 END) AS has_full,
        MAX(CASE WHEN output_tokens IS NOT NULL THEN 1 ELSE 0 END) AS has_any
    FROM usage_events
    WHERE ts BETWEEN $start AND $end");
        AppendFilters(sql, query);
        sql.Append(@"
    GROUP BY session_id
)
SELECT
    SUM(has_full),
    SUM(CASE WHEN has_full = 0 AND has_any = 1 THEN 1 ELSE 0 END)
FROM per_session");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$start", startMs);
        cmd.Parameters.AddWithValue("$end", endMs);
        BindFilters(cmd, query);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, 0);
        var full = r.IsDBNull(0) ? 0 : (int)r.GetInt64(0);
        var partial = r.IsDBNull(1) ? 0 : (int)r.GetInt64(1);
        return (full, partial);
    }

    private static void AppendFilters(StringBuilder sql, UsageQuery query)
    {
        if (query.HostFilter is { Count: > 0 } hf)
        {
            sql.Append(" AND host IN (");
            for (var i = 0; i < hf.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append($"$h{i}");
            }
            sql.Append(")");
        }
        if (query.ProjectFilter is { Count: > 0 } pf)
        {
            sql.Append(" AND COALESCE(project_repo, project_path) IN (");
            for (var i = 0; i < pf.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append($"$p{i}");
            }
            sql.Append(")");
        }
    }

    private static void BindFilters(Microsoft.Data.Sqlite.SqliteCommand cmd, UsageQuery query)
    {
        if (query.HostFilter is { } hf)
            for (var i = 0; i < hf.Count; i++)
                cmd.Parameters.AddWithValue($"$h{i}", hf[i]);
        if (query.ProjectFilter is { } pf)
            for (var i = 0; i < pf.Count; i++)
                cmd.Parameters.AddWithValue($"$p{i}", pf[i]);
    }
}
