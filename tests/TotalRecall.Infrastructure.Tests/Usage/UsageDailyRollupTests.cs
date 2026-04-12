using System;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Usage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Usage;

public sealed class UsageDailyRollupTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection OpenMigrated()
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static UsageEvent E(string host, string eid, long ts, int? input, int? output, string? model = "opus") =>
        new UsageEvent(
            Host: host, HostEventId: eid, SessionId: "s-" + eid, TimestampMs: ts,
            TurnIndex: 0, Model: model, ProjectPath: "/p", ProjectRepo: null,
            ProjectBranch: null, ProjectCommit: null, InteractionId: null,
            InputTokens: input, CacheCreation5m: null, CacheCreation1h: null,
            CacheRead: null, OutputTokens: output, ServiceTier: null,
            ServerToolUseJson: null, HostRequestId: null);

    [Fact]
    public void Rollup_EventsOlderThanCutoff_AggregatedAndDeleted()
    {
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        var rollup = new UsageDailyRollup(conn);

        var oldDay = DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds();
        log.InsertOrIgnore(E("claude-code", "a1", oldDay, input: 100, output: 20));
        log.InsertOrIgnore(E("claude-code", "a2", oldDay + 1000, input: 50, output: 10));

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        var result = rollup.RollupOlderThan(cutoff);

        Assert.Equal(2, result.EventsAggregated);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM usage_events";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(*), SUM(input_tokens), SUM(output_tokens) FROM usage_daily";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(1L, r.GetInt64(0));       // one daily row
        Assert.Equal(150L, r.GetInt64(1));     // summed input
        Assert.Equal(30L, r.GetInt64(2));      // summed output
    }

    [Fact]
    public void Rollup_EventsWithinCutoff_Untouched()
    {
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        var rollup = new UsageDailyRollup(conn);

        var recent = DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeMilliseconds();
        log.InsertOrIgnore(E("claude-code", "a1", recent, input: 100, output: 20));

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        var result = rollup.RollupOlderThan(cutoff);

        Assert.Equal(0, result.EventsAggregated);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM usage_events";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void Rollup_IsIdempotent_SecondRunNoOp()
    {
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        var rollup = new UsageDailyRollup(conn);

        var oldDay = DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds();
        log.InsertOrIgnore(E("claude-code", "a1", oldDay, input: 100, output: 20));
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();

        var first = rollup.RollupOlderThan(cutoff);
        var second = rollup.RollupOlderThan(cutoff);

        Assert.Equal(1, first.EventsAggregated);
        Assert.Equal(0, second.EventsAggregated);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), SUM(input_tokens), SUM(output_tokens) FROM usage_daily";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(1L, r.GetInt64(0));   // still one row, not duplicated
        Assert.Equal(100L, r.GetInt64(1)); // input tokens preserved, not zeroed
        Assert.Equal(20L, r.GetInt64(2));  // output tokens preserved, not zeroed
    }

    [Fact]
    public void Rollup_AllNullCacheFields_PreservesNullInDailyRow()
    {
        // Bug 1 regression: rollup SQL must preserve NULL in
        // usage_daily.cache_creation_tokens when every source row has both
        // cache_creation_5m and cache_creation_1h = NULL. Per-row COALESCE
        // to 0 would erase the "we don't know" signal forever.
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        var rollup = new UsageDailyRollup(conn);

        var oldDay = DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds();
        log.InsertOrIgnore(E("copilot-cli", "a1", oldDay, input: null, output: 20));
        log.InsertOrIgnore(E("copilot-cli", "a2", oldDay + 1000, input: null, output: 10));

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        rollup.RollupOlderThan(cutoff);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT cache_creation_tokens FROM usage_daily";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.True(r.IsDBNull(0));
    }

    [Fact]
    public void Rollup_GroupsByDayHostModelProject()
    {
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        var rollup = new UsageDailyRollup(conn);

        // Two past days, both comfortably older than the 30-day retention
        // cutoff. Snapped to 10:00 UTC to keep them unambiguously inside the
        // "older than cutoff" range regardless of when the test runs.
        var day1Base = DateTimeOffset.UtcNow.Date.AddDays(-35);
        var day2Base = DateTimeOffset.UtcNow.Date.AddDays(-34);
        var day1 = new DateTimeOffset(day1Base.Year, day1Base.Month, day1Base.Day, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var day2 = new DateTimeOffset(day2Base.Year, day2Base.Month, day2Base.Day, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        log.InsertOrIgnore(E("claude-code", "a1", day1, input: 100, output: 20, model: "opus"));
        log.InsertOrIgnore(E("claude-code", "a2", day1, input: 50, output: 10, model: "opus"));
        log.InsertOrIgnore(E("claude-code", "a3", day1, input: 75, output: 15, model: "sonnet"));
        log.InsertOrIgnore(E("claude-code", "a4", day2, input: 25, output: 5, model: "opus"));

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        rollup.RollupOlderThan(cutoff);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM usage_daily";
        // day1/opus, day1/sonnet, day2/opus -> 3 rows
        Assert.Equal(3L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = @"
SELECT model, SUM(input_tokens), SUM(output_tokens), SUM(turn_count)
FROM usage_daily
WHERE day_utc = $day
GROUP BY model
ORDER BY model";

        // day1: opus = 150 in / 30 out / 2 turns, sonnet = 75 in / 15 out / 1 turn
        cmd.Parameters.AddWithValue("$day", day1 / 1000 / 86400 * 86400);
        using (var rd1 = cmd.ExecuteReader())
        {
            Assert.True(rd1.Read());
            Assert.Equal("opus", rd1.GetString(0));
            Assert.Equal(150L, rd1.GetInt64(1));
            Assert.Equal(30L, rd1.GetInt64(2));
            Assert.Equal(2L, rd1.GetInt64(3));
            Assert.True(rd1.Read());
            Assert.Equal("sonnet", rd1.GetString(0));
            Assert.Equal(75L, rd1.GetInt64(1));
            Assert.Equal(15L, rd1.GetInt64(2));
            Assert.Equal(1L, rd1.GetInt64(3));
            Assert.False(rd1.Read());
        }

        // day2: opus = 25 in / 5 out / 1 turn
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$day", day2 / 1000 / 86400 * 86400);
        using (var rd2 = cmd.ExecuteReader())
        {
            Assert.True(rd2.Read());
            Assert.Equal("opus", rd2.GetString(0));
            Assert.Equal(25L, rd2.GetInt64(1));
            Assert.Equal(5L, rd2.GetInt64(2));
            Assert.Equal(1L, rd2.GetInt64(3));
        }
    }
}
