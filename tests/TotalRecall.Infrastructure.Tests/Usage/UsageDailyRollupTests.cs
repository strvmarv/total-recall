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
        cmd.CommandText = "SELECT COUNT(*) FROM usage_daily";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);  // still one row, not duplicated
    }

    [Fact]
    public void Rollup_GroupsByDayHostModelProject()
    {
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        var rollup = new UsageDailyRollup(conn);

        var day1 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var day2 = new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
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
    }
}
