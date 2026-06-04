using TotalRecall.Infrastructure.Telemetry;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

public sealed class RetrievalEventStatsTests
{
    private static MsSqliteConnection OpenWithTable()
    {
        var conn = new MsSqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Mirror of the Migration 1 retrieval_events DDL (Schema.cs).
        cmd.CommandText = """
            CREATE TABLE retrieval_events (
                id                       TEXT PRIMARY KEY NOT NULL,
                timestamp                INTEGER NOT NULL,
                session_id               TEXT NOT NULL,
                query_text               TEXT NOT NULL,
                query_source             TEXT NOT NULL,
                query_embedding          BLOB,
                results                  TEXT NOT NULL DEFAULT '[]',
                result_count             INTEGER NOT NULL DEFAULT 0,
                top_score                REAL,
                top_tier                 TEXT,
                top_content_type         TEXT,
                outcome_used             INTEGER,
                outcome_signal           TEXT,
                config_snapshot_id       TEXT NOT NULL,
                latency_ms               INTEGER,
                tiers_searched           TEXT NOT NULL DEFAULT '[]',
                total_candidates_scanned INTEGER
            )
            """;
        cmd.ExecuteNonQuery();
        return conn;
    }

    [Fact]
    public void GetStatsSince_CountsAndAveragesLatency()
    {
        using var conn = OpenWithTable();
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO retrieval_events
                  (id, timestamp, session_id, query_text, query_source, config_snapshot_id, latency_ms)
                VALUES
                  ('e1', 1000, 'unknown', 'q1', 'memory_search', 'cfg', 10),
                  ('e2', 2000, 'unknown', 'q2', 'memory_search', 'cfg', 30),
                  ('e3',  500, 'unknown', 'q3', 'memory_search', 'cfg', 99)
                """;
            ins.ExecuteNonQuery();
        }

        var log = new RetrievalEventLog(conn);
        var (count, avg) = log.GetStatsSince(1000);

        Assert.Equal(2, count);   // e1 + e2; e3 is before the cutoff
        Assert.Equal(20.0, avg);  // (10 + 30) / 2
    }

    [Fact]
    public void GetStatsSince_NoEvents_ReturnsZeros()
    {
        using var conn = OpenWithTable();
        var log = new RetrievalEventLog(conn);
        var (count, avg) = log.GetStatsSince(0);
        Assert.Equal(0, count);
        Assert.Equal(0.0, avg);
    }

    [Fact]
    public void GetStatsSince_NullLatencies_AverageIsZero()
    {
        using var conn = OpenWithTable();
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO retrieval_events
                  (id, timestamp, session_id, query_text, query_source, config_snapshot_id, latency_ms)
                VALUES ('e1', 1000, 'unknown', 'q1', 'memory_search', 'cfg', NULL)
                """;
            ins.ExecuteNonQuery();
        }

        var log = new RetrievalEventLog(conn);
        var (count, avg) = log.GetStatsSince(0);
        Assert.Equal(1, count);
        Assert.Equal(0.0, avg); // AVG over only-NULL latencies COALESCEs to 0
    }
}
