// tests/TotalRecall.Server.Tests/UsageStatusHandlerTests.cs
//
// Task 13 — UsageStatusHandler contract tests. Seeds an in-memory
// SQLite DB with a few usage events via UsageEventLog, then exercises
// the handler through the IToolHandler surface the registry actually
// dispatches against.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Usage;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public sealed class UsageStatusHandlerTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection Seed(Action<UsageEventLog> seed)
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        seed(new UsageEventLog(conn));
        return conn;
    }

    private static UsageEvent E(string host, string eid, long ts, int? input, int? output) =>
        new UsageEvent(
            Host: host, HostEventId: eid, SessionId: "s-" + eid, TimestampMs: ts,
            TurnIndex: 0, Model: "opus", ProjectPath: "/p", ProjectRepo: null,
            ProjectBranch: null, ProjectCommit: null, InteractionId: null,
            InputTokens: input, CacheCreation5m: null, CacheCreation1h: null,
            CacheRead: null, OutputTokens: output, ServiceTier: null,
            ServerToolUseJson: null, HostRequestId: null);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task ExecuteAsync_EmptyArgs_ReturnsDefaultSevenDayReport()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = Seed(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a", nowMs - 1000, 100, 20));
        });
        var svc = new UsageQueryService(conn);
        var handler = new UsageStatusHandler(svc);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        var text = result.Content[0].Text;
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("buckets", out _));
        Assert.Equal("host", doc.RootElement.GetProperty("query").GetProperty("group_by").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_WindowAndGroupByArgs_Honored()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = Seed(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a", nowMs - 1000, 100, 20));
        });
        var svc = new UsageQueryService(conn);
        var handler = new UsageStatusHandler(svc);

        var result = await handler.ExecuteAsync(
            Args("""{"window":"5h","group_by":"day"}"""),
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("day", doc.RootElement.GetProperty("query").GetProperty("group_by").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_HostAndProjectFilter_PassedThrough()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = Seed(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a", nowMs - 1000, 100, 20));
        });
        var svc = new UsageQueryService(conn);
        var handler = new UsageStatusHandler(svc);

        var result = await handler.ExecuteAsync(
            Args("""{"host":"claude-code","project":"/p","top":5}"""),
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var q = doc.RootElement.GetProperty("query");
        Assert.Equal("claude-code", q.GetProperty("host_filter")[0].GetString());
        Assert.Equal("/p", q.GetProperty("project_filter")[0].GetString());
    }

    [Fact]
    public async Task ExecuteAsync_InvalidWindow_ReturnsError()
    {
        using var conn = Seed(_ => { });
        var svc = new UsageQueryService(conn);
        var handler = new UsageStatusHandler(svc);

        var result = await handler.ExecuteAsync(
            Args("""{"window":"bogus"}"""),
            CancellationToken.None);

        Assert.Equal(true, result.IsError);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidGroupBy_ReturnsError()
    {
        using var conn = Seed(_ => { });
        var svc = new UsageQueryService(conn);
        var handler = new UsageStatusHandler(svc);

        var result = await handler.ExecuteAsync(
            Args("""{"group_by":"galaxy"}"""),
            CancellationToken.None);

        Assert.Equal(true, result.IsError);
    }

    [Fact]
    public void Name_And_InputSchema_StableShape()
    {
        using var conn = Seed(_ => { });
        var handler = new UsageStatusHandler(new UsageQueryService(conn));

        Assert.Equal("usage_status", handler.Name);
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
        Assert.True(handler.InputSchema.TryGetProperty("properties", out _));
    }
}
