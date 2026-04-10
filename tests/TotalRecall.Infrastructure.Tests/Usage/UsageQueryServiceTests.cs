using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Usage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Usage;

public sealed class UsageQueryServiceTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection OpenSeeded(Action<UsageEventLog> seed)
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var log = new UsageEventLog(conn);
        seed(log);
        return conn;
    }

    private static UsageEvent E(string host, string eid, string session, long ts,
        int? input, int? output, string? model = null, string? project = null) =>
        new UsageEvent(
            Host: host, HostEventId: eid, SessionId: session, TimestampMs: ts,
            TurnIndex: 0, Model: model, ProjectPath: project, ProjectRepo: null,
            ProjectBranch: null, ProjectCommit: null, InteractionId: null,
            InputTokens: input, CacheCreation5m: null, CacheCreation1h: null,
            CacheRead: null, OutputTokens: output, ServiceTier: null,
            ServerToolUseJson: null, HostRequestId: null);

    private static UsageQuery Last(GroupBy group, TimeSpan window) =>
        new UsageQuery(
            Start: DateTimeOffset.UtcNow - window,
            End: DateTimeOffset.UtcNow,
            HostFilter: null,
            ProjectFilter: null,
            GroupBy: group,
            TopN: 0);

    [Fact]
    public void Query_GroupByHost_SumsPerHost()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = OpenSeeded(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a1", "s1", nowMs - 1000, input: 100, output: 20));
            log.InsertOrIgnore(E("claude-code", "a2", "s1", nowMs - 2000, input: 50, output: 10));
            log.InsertOrIgnore(E("copilot-cli", "b1", "s2", nowMs - 1000, input: null, output: 15));
        });

        var svc = new UsageQueryService(conn);
        var report = svc.Query(Last(GroupBy.Host, TimeSpan.FromHours(1)));

        Assert.Equal(2, report.Buckets.Count);
        var cc = report.Buckets.First(b => b.Key == "claude-code");
        Assert.Equal(150L, cc.Totals.InputTokens);
        Assert.Equal(30L, cc.Totals.OutputTokens);
        var co = report.Buckets.First(b => b.Key == "copilot-cli");
        Assert.Null(co.Totals.InputTokens); // nothing in bucket had input_tokens
        Assert.Equal(15L, co.Totals.OutputTokens);
    }

    [Fact]
    public void Query_GroupByHost_GrandTotalIsUnionOfNonNulls()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = OpenSeeded(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a1", "s1", nowMs - 1000, input: 100, output: 20));
            log.InsertOrIgnore(E("copilot-cli", "b1", "s2", nowMs - 1000, input: null, output: 15));
        });

        var svc = new UsageQueryService(conn);
        var report = svc.Query(Last(GroupBy.Host, TimeSpan.FromHours(1)));

        Assert.Equal(100L, report.GrandTotal.InputTokens); // only Claude Code had input
        Assert.Equal(35L, report.GrandTotal.OutputTokens);
        Assert.Equal(2, report.GrandTotal.SessionCount);
    }

    [Fact]
    public void Query_Coverage_SplitsFullAndPartial()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = OpenSeeded(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a1", "sess-full", nowMs - 500, input: 100, output: 20));
            log.InsertOrIgnore(E("copilot-cli", "b1", "sess-part1", nowMs - 500, input: null, output: 10));
            log.InsertOrIgnore(E("copilot-cli", "b2", "sess-part2", nowMs - 500, input: null, output: 10));
        });

        var svc = new UsageQueryService(conn);
        var report = svc.Query(Last(GroupBy.Host, TimeSpan.FromHours(1)));

        Assert.Equal(1, report.SessionsWithFullTokenData);      // sess-full
        Assert.Equal(2, report.SessionsWithPartialTokenData);   // sess-part1, sess-part2
    }

    [Fact]
    public void Query_GroupByProject_UsesCoalesceOfRepoAndPath()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = OpenSeeded(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a1", "s1", nowMs - 500, input: 100, output: 20, project: "/work/foo"));
            log.InsertOrIgnore(E("claude-code", "a2", "s2", nowMs - 500, input: 50, output: 10, project: "/work/bar"));
        });

        var svc = new UsageQueryService(conn);
        var report = svc.Query(Last(GroupBy.Project, TimeSpan.FromHours(1)));

        Assert.Equal(2, report.Buckets.Count);
    }

    [Fact]
    public void Query_HostFilter_LimitsToNamedHost()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = OpenSeeded(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a1", "s1", nowMs - 500, input: 100, output: 20));
            log.InsertOrIgnore(E("copilot-cli", "b1", "s2", nowMs - 500, input: null, output: 10));
        });

        var svc = new UsageQueryService(conn);
        var report = svc.Query(new UsageQuery(
            Start: DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
            End: DateTimeOffset.UtcNow,
            HostFilter: new[] { "claude-code" },
            ProjectFilter: null,
            GroupBy: GroupBy.Host,
            TopN: 0));

        Assert.Single(report.Buckets);
        Assert.Equal("claude-code", report.Buckets[0].Key);
    }

    [Fact]
    public void Query_WindowBeforeAnyEvents_ReturnsEmpty()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = OpenSeeded(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a1", "s1", nowMs - 1000, input: 100, output: 20));
        });

        var svc = new UsageQueryService(conn);
        var report = svc.Query(new UsageQuery(
            Start: DateTimeOffset.UtcNow.AddDays(-100),
            End: DateTimeOffset.UtcNow.AddDays(-99),
            HostFilter: null, ProjectFilter: null,
            GroupBy: GroupBy.Host, TopN: 0));

        Assert.Empty(report.Buckets);
        Assert.Equal(0, report.GrandTotal.SessionCount);
    }
}
