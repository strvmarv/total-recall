using System;
using System.Collections.Generic;
using System.Text.Json;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

[Trait("Category", "Integration")]
public sealed class CompactionLogTests
{
    private static (MsSqliteConnection conn, CompactionLog log) NewLog()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new CompactionLog(conn));
    }

    private static CompactionLogEntry SampleEntry(
        IReadOnlyList<string>? sources = null,
        IReadOnlyDictionary<string, double>? scores = null,
        string? targetTier = "warm",
        string? targetEntryId = "tgt")
    {
        return new CompactionLogEntry(
            SessionId: "sess-1",
            SourceTier: "hot",
            TargetTier: targetTier,
            SourceEntryIds: sources ?? new[] { "a", "b" },
            TargetEntryId: targetEntryId,
            DecayScores: scores ?? new Dictionary<string, double> { ["a"] = 0.5, ["b"] = 0.7 },
            Reason: "decay",
            ConfigSnapshotId: "cfg-1");
    }

    [Fact]
    public void LogEvent_FullEntry_InsertsRowAndReturnsId()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var id = log.LogEvent(SampleEntry());
            Assert.False(string.IsNullOrEmpty(id));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, session_id, source_tier, target_tier, target_entry_id, reason, config_snapshot_id FROM compaction_log";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(id, reader.GetString(0));
            Assert.Equal("sess-1", reader.GetString(1));
            Assert.Equal("hot", reader.GetString(2));
            Assert.Equal("warm", reader.GetString(3));
            Assert.Equal("tgt", reader.GetString(4));
            Assert.Equal("decay", reader.GetString(5));
            Assert.Equal("cfg-1", reader.GetString(6));
        }
    }

    [Fact]
    public void LogEvent_NullableFields_StoredAsNull()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(SampleEntry(targetTier: null, targetEntryId: null));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT target_tier, target_entry_id, semantic_drift, facts_preserved, facts_in_original, preservation_ratio FROM compaction_log";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
            Assert.True(reader.IsDBNull(4));
            Assert.True(reader.IsDBNull(5));
        }
    }

    [Fact]
    public void LogEvent_SourceEntryIdsJson_RoundTrip()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(SampleEntry(sources: new[] { "x", "y", "z" }));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT source_entry_ids FROM compaction_log";
            var json = (string)cmd.ExecuteScalar()!;
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
            Assert.Equal(3, arr.GetArrayLength());
            Assert.Equal("x", arr[0].GetString());
            Assert.Equal("y", arr[1].GetString());
            Assert.Equal("z", arr[2].GetString());
        }
    }

    [Fact]
    public void LogEvent_DecayScoresJson_RoundTrip()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(SampleEntry(scores: new Dictionary<string, double>
            {
                ["alpha"] = 0.25,
                ["beta"] = 0.875,
            }));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT decay_scores FROM compaction_log";
            var json = (string)cmd.ExecuteScalar()!;
            using var doc = JsonDocument.Parse(json);
            var obj = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, obj.ValueKind);
            Assert.Equal(0.25, obj.GetProperty("alpha").GetDouble());
            Assert.Equal(0.875, obj.GetProperty("beta").GetDouble());
        }
    }

    [Fact]
    public void LogEvent_AnalyticFieldsProvided_StoredAsValues()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var entry = new CompactionLogEntry(
                SessionId: "s",
                SourceTier: "hot",
                TargetTier: "warm",
                SourceEntryIds: Array.Empty<string>(),
                TargetEntryId: "t",
                DecayScores: new Dictionary<string, double>(),
                Reason: "r",
                ConfigSnapshotId: "c",
                SemanticDrift: 0.42,
                FactsPreserved: 7,
                FactsInOriginal: 10,
                PreservationRatio: 0.7);
            log.LogEvent(entry);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT semantic_drift, facts_preserved, facts_in_original, preservation_ratio FROM compaction_log";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(0.42, reader.GetDouble(0));
            Assert.Equal(7, reader.GetInt32(1));
            Assert.Equal(10, reader.GetInt32(2));
            Assert.Equal(0.7, reader.GetDouble(3));
        }
    }

    [Fact]
    public void GetAllForAnalytics_ReturnsProjectedRows_DescByTimestamp()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            // Insert three entries with varying preservation / drift values.
            log.LogEvent(new CompactionLogEntry(
                SessionId: "s",
                SourceTier: "hot",
                TargetTier: "warm",
                SourceEntryIds: new[] { "a" },
                TargetEntryId: "t1",
                DecayScores: new Dictionary<string, double>(),
                Reason: "decay",
                ConfigSnapshotId: "cfg",
                SemanticDrift: 0.1,
                PreservationRatio: 0.9));
            System.Threading.Thread.Sleep(5);
            log.LogEvent(new CompactionLogEntry(
                SessionId: "s",
                SourceTier: "hot",
                TargetTier: "warm",
                SourceEntryIds: new[] { "b" },
                TargetEntryId: "t2",
                DecayScores: new Dictionary<string, double>(),
                Reason: "decay",
                ConfigSnapshotId: "cfg",
                SemanticDrift: null,
                PreservationRatio: null));
            System.Threading.Thread.Sleep(5);
            log.LogEvent(new CompactionLogEntry(
                SessionId: "s",
                SourceTier: "hot",
                TargetTier: "warm",
                SourceEntryIds: new[] { "c" },
                TargetEntryId: "t3",
                DecayScores: new Dictionary<string, double>(),
                Reason: "decay",
                ConfigSnapshotId: "cfg",
                SemanticDrift: 0.3,
                PreservationRatio: 0.6));

            var rows = log.GetAllForAnalytics();
            Assert.Equal(3, rows.Count);
            // DESC order: most recently inserted first.
            Assert.Equal(0.6, rows[0].PreservationRatio);
            Assert.Equal(0.3, rows[0].SemanticDrift);
            Assert.Null(rows[1].PreservationRatio);
            Assert.Null(rows[1].SemanticDrift);
            Assert.Equal(0.9, rows[2].PreservationRatio);
            Assert.Equal(0.1, rows[2].SemanticDrift);
            // Timestamps strictly non-increasing.
            Assert.True(rows[0].Timestamp >= rows[1].Timestamp);
            Assert.True(rows[1].Timestamp >= rows[2].Timestamp);
        }
    }

    [Fact]
    public void GetAllForAnalytics_WithSinceCutoff_FiltersOlderRows()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogEvent(SampleEntry());
            System.Threading.Thread.Sleep(5);
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            System.Threading.Thread.Sleep(5);
            log.LogEvent(SampleEntry());

            var rows = log.GetAllForAnalytics(cutoff);
            Assert.Single(rows);
        }
    }

    // Direct-INSERT helper: lets tests control timestamp + malformed JSON.
    private static void InsertRow(
        MsSqliteConnection conn,
        string id,
        long timestamp,
        string sourceEntryIdsJson = "[]",
        string? targetEntryId = null,
        string? sessionId = "s",
        string sourceTier = "hot",
        string? targetTier = "warm",
        string reason = "decay",
        string decayScoresJson = "{}")
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO compaction_log
  (id, timestamp, session_id, source_tier, target_tier, source_entry_ids,
   target_entry_id, decay_scores, reason, config_snapshot_id)
VALUES
  ($id, $ts, $sid, $stier, $ttier, $sids, $teid, $decay, $reason, 'cfg')";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.Parameters.AddWithValue("$sid", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stier", sourceTier);
        cmd.Parameters.AddWithValue("$ttier", (object?)targetTier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sids", sourceEntryIdsJson);
        cmd.Parameters.AddWithValue("$teid", (object?)targetEntryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$decay", decayScoresJson);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void GetRecentMovements_ReturnsRowsDescByTimestamp_RespectsLimit()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            InsertRow(conn, id: "a", timestamp: 100, targetEntryId: "t-a");
            InsertRow(conn, id: "b", timestamp: 300, targetEntryId: "t-b");
            InsertRow(conn, id: "c", timestamp: 200, targetEntryId: "t-c");

            var rows = log.GetRecentMovements(limit: 2);
            Assert.Equal(2, rows.Count);
            // DESC: b (300) then c (200).
            Assert.Equal("b", rows[0].Id);
            Assert.Equal(300, rows[0].Timestamp);
            Assert.Equal("c", rows[1].Id);
            Assert.Equal(200, rows[1].Timestamp);
        }
    }

    [Fact]
    public void GetRecentMovements_ParsesSourceEntryIdsAndDecayScores()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            InsertRow(
                conn,
                id: "a",
                timestamp: 100,
                sourceEntryIdsJson: "[\"x\",\"y\",\"z\"]",
                decayScoresJson: "{\"x\":0.25,\"y\":0.5}");

            var rows = log.GetRecentMovements(limit: 10);
            var row = Assert.Single(rows);
            Assert.Equal(new[] { "x", "y", "z" }, row.SourceEntryIds);
            Assert.Equal(2, row.DecayScores.Count);
            Assert.Equal(0.25, row.DecayScores["x"]);
            Assert.Equal(0.5, row.DecayScores["y"]);
        }
    }

    [Fact]
    public void GetRecentMovements_MalformedJsonFallsBackToEmpty()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            InsertRow(
                conn,
                id: "a",
                timestamp: 100,
                sourceEntryIdsJson: "{bad json",
                decayScoresJson: "not an object");

            var rows = log.GetRecentMovements(limit: 10);
            var row = Assert.Single(rows);
            Assert.Empty(row.SourceEntryIds);
            Assert.Empty(row.DecayScores);
        }
    }

    [Fact]
    public void GetByTargetEntryId_Found_ReturnsRow()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            InsertRow(conn, id: "log-1", timestamp: 100, targetEntryId: "X");

            var row = log.GetByTargetEntryId("X");
            Assert.NotNull(row);
            Assert.Equal("log-1", row!.Id);
            Assert.Equal("X", row.TargetEntryId);
        }
    }

    [Fact]
    public void GetByTargetEntryId_NotFound_ReturnsNull()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            InsertRow(conn, id: "log-1", timestamp: 100, targetEntryId: "X");
            Assert.Null(log.GetByTargetEntryId("Y"));
        }
    }

    [Fact]
    public void LogEvent_TimestampWithinReasonableRange()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            log.LogEvent(SampleEntry());
            var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT timestamp FROM compaction_log";
            var ts = (long)cmd.ExecuteScalar()!;
            Assert.InRange(ts, before, after);
        }
    }
}
