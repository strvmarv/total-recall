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
