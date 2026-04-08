using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

[Trait("Category", "Integration")]
public sealed class RetrievalEventLogTests
{
    private static (MsSqliteConnection conn, RetrievalEventLog log) NewLog()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new RetrievalEventLog(conn));
    }

    private static RetrievalEventEntry MakeEntry(
        IReadOnlyList<RetrievalResultItem>? results = null,
        IReadOnlyList<string>? tiers = null,
        byte[]? embedding = null,
        string sessionId = "sess-1",
        long? latency = 12,
        int? scanned = 100)
    {
        return new RetrievalEventEntry(
            SessionId: sessionId,
            QueryText: "what is x?",
            QuerySource: "user",
            Results: results ?? new[]
            {
                new RetrievalResultItem("e1", "hot", "memory", 0.95, 1),
                new RetrievalResultItem("e2", "warm", "memory", 0.80, 2),
            },
            TiersSearched: tiers ?? new[] { "hot", "warm" },
            ConfigSnapshotId: "cfg-1",
            QueryEmbedding: embedding,
            LatencyMs: latency,
            TotalCandidatesScanned: scanned);
    }

    [Fact]
    public void LogEvent_FullEntry_InsertsAndReturnsId()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var id = log.LogEvent(MakeEntry());
            Assert.False(string.IsNullOrEmpty(id));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM retrieval_events WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void LogEvent_TopScoreFromFirstResult()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(MakeEntry(results: new[]
            {
                new RetrievalResultItem("e1", "hot", "memory", 0.95, 1),
                new RetrievalResultItem("e2", "warm", "knowledge", 0.80, 2),
                new RetrievalResultItem("e3", "cold", "memory", 0.10, 3),
            }));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT top_score, top_tier, top_content_type, result_count FROM retrieval_events";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0.95, reader.GetDouble(0));
            Assert.Equal("hot", reader.GetString(1));
            Assert.Equal("memory", reader.GetString(2));
            Assert.Equal(3, reader.GetInt32(3));
        }
    }

    [Fact]
    public void LogEvent_EmptyResults_TopFieldsNull()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(MakeEntry(results: Array.Empty<RetrievalResultItem>()));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT top_score, top_tier, top_content_type, result_count FROM retrieval_events";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
            Assert.Equal(0, reader.GetInt32(3));
        }
    }

    [Fact]
    public void LogEvent_ResultsJsonMatchesTsFormat()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(MakeEntry(results: new[]
            {
                new RetrievalResultItem("e1", "hot", "memory", 0.5, 1),
            }));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT results FROM retrieval_events";
            var json = (string)cmd.ExecuteScalar()!;
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
            Assert.Equal(1, arr.GetArrayLength());
            var item = arr[0];
            Assert.Equal("e1", item.GetProperty("entry_id").GetString());
            Assert.Equal("hot", item.GetProperty("tier").GetString());
            Assert.Equal("memory", item.GetProperty("content_type").GetString());
            Assert.Equal(0.5, item.GetProperty("score").GetDouble());
            Assert.Equal(1, item.GetProperty("rank").GetInt32());
        }
    }

    [Fact]
    public void LogEvent_QueryEmbeddingBlob_RoundTrip()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var blob = new byte[] { 1, 2, 3, 4, 5 };
            log.LogEvent(MakeEntry(embedding: blob));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT query_embedding FROM retrieval_events";
            var got = (byte[])cmd.ExecuteScalar()!;
            Assert.Equal(blob, got);
        }
    }

    [Fact]
    public void LogEvent_QueryEmbeddingNull_StoredAsNull()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(MakeEntry(embedding: null));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT query_embedding FROM retrieval_events";
            var got = cmd.ExecuteScalar();
            Assert.True(got is null || got is DBNull);
        }
    }

    [Fact]
    public void LogEvent_TiersSearchedJson_RoundTrip()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(MakeEntry(tiers: new[] { "hot", "warm", "cold" }));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT tiers_searched FROM retrieval_events";
            var json = (string)cmd.ExecuteScalar()!;
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            Assert.Equal(3, arr.GetArrayLength());
            Assert.Equal("hot", arr[0].GetString());
            Assert.Equal("warm", arr[1].GetString());
            Assert.Equal("cold", arr[2].GetString());
        }
    }

    [Fact]
    public void UpdateOutcome_AfterLog_UpdatesColumns()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var id = log.LogEvent(MakeEntry());
            log.UpdateOutcome(id, new RetrievalOutcome(Used: true, Signal: "explicit"));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT outcome_used, outcome_signal FROM retrieval_events WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("explicit", reader.GetString(1));
        }
    }

    [Fact]
    public void UpdateOutcome_NullSignal_StoredAsNull()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var id = log.LogEvent(MakeEntry());
            log.UpdateOutcome(id, new RetrievalOutcome(Used: false, Signal: null));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT outcome_used, outcome_signal FROM retrieval_events WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.True(reader.IsDBNull(1));
        }
    }

    [Fact]
    public void GetEvents_NoFilters_ReturnsAllOrderedDescByTimestamp()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var ids = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                ids.Add(log.LogEvent(MakeEntry(sessionId: $"s{i}")));
                Thread.Sleep(2);
            }

            var rows = log.GetEvents(new RetrievalEventQuery());
            Assert.Equal(3, rows.Count);
            // Newest first
            Assert.Equal(ids[2], rows[0].Id);
            Assert.Equal(ids[1], rows[1].Id);
            Assert.Equal(ids[0], rows[2].Id);
            Assert.True(rows[0].Timestamp >= rows[1].Timestamp);
            Assert.True(rows[1].Timestamp >= rows[2].Timestamp);
        }
    }

    [Fact]
    public void GetEvents_SessionIdFilter_OnlyMatching()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(MakeEntry(sessionId: "alpha"));
            log.LogEvent(MakeEntry(sessionId: "beta"));
            log.LogEvent(MakeEntry(sessionId: "alpha"));

            var rows = log.GetEvents(new RetrievalEventQuery(SessionId: "alpha"));
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("alpha", r.SessionId));
        }
    }

    [Fact]
    public void GetEvents_DaysFilter_ExcludesOlder()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            // Insert one fresh row via the writer.
            var freshId = log.LogEvent(MakeEntry(sessionId: "fresh"));

            // Insert one stale row directly with a 10-day-old timestamp.
            var oldTs = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeMilliseconds();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO retrieval_events
  (id, timestamp, session_id, query_text, query_source, query_embedding,
   results, result_count, top_score, top_tier, top_content_type,
   config_snapshot_id, latency_ms, tiers_searched, total_candidates_scanned)
VALUES
  ('old-id', $ts, 'old', 'q', 'user', NULL,
   '[]', 0, NULL, NULL, NULL,
   'cfg', NULL, '[]', NULL)";
                cmd.Parameters.AddWithValue("$ts", oldTs);
                cmd.ExecuteNonQuery();
            }

            var rows = log.GetEvents(new RetrievalEventQuery(Days: 1));
            Assert.Single(rows);
            Assert.Equal(freshId, rows[0].Id);
        }
    }

    [Fact]
    public void GetEvents_LimitClause_CapsResults()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            for (var i = 0; i < 5; i++)
            {
                log.LogEvent(MakeEntry(sessionId: $"s{i}"));
                Thread.Sleep(2);
            }
            var rows = log.GetEvents(new RetrievalEventQuery(Limit: 2));
            Assert.Equal(2, rows.Count);
        }
    }
}
