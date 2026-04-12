using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Usage;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

public sealed class UsageCommandTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection Seed(Action<UsageEventLog> seed)
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var log = new UsageEventLog(conn);
        seed(log);
        return conn;
    }

    private static UsageEvent E(string host, string eid, long ts, int? input, int? output) =>
        new UsageEvent(
            Host: host, HostEventId: eid, SessionId: "s1", TimestampMs: ts,
            TurnIndex: 0, Model: "claude-opus-4.1", ProjectPath: "/p",
            ProjectRepo: null, ProjectBranch: null, ProjectCommit: null,
            InteractionId: null, InputTokens: input, CacheCreation5m: null,
            CacheCreation1h: null, CacheRead: null, OutputTokens: output,
            ServiceTier: null, ServerToolUseJson: null, HostRequestId: null);

    [Fact]
    public async Task RunAsync_DefaultFlags_PrintsTableWithTotalRow()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = Seed(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a", nowMs - 1000, input: 100, output: 20));
            log.InsertOrIgnore(E("copilot-cli", "b", nowMs - 1000, input: null, output: 10));
        });
        var svc = new UsageQueryService(conn);
        var output = new StringWriter();
        var cmd = new UsageCommand(svc, output);

        var exit = await cmd.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("claude-code", text);
        Assert.Contains("copilot-cli", text);
        Assert.Contains("total", text);       // total row
        Assert.Contains("—", text);           // em-dash for null input on copilot
    }

    [Fact]
    public async Task RunAsync_ByDay_FormatsDateKeys()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = Seed(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a", nowMs - 1000, input: 100, output: 20));
        });
        var svc = new UsageQueryService(conn);
        var output = new StringWriter();
        var cmd = new UsageCommand(svc, output);

        var exit = await cmd.RunAsync(new[] { "--by", "day" });

        Assert.Equal(0, exit);
        // Should contain today's date in YYYY-MM-DD form
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        Assert.Contains(today, output.ToString());
    }

    [Fact]
    public async Task RunAsync_InvalidWindow_ReturnsTwoAndPrintsError()
    {
        using var conn = Seed(_ => { });
        var svc = new UsageQueryService(conn);
        var errOut = new StringWriter();
        var cmd = new UsageCommand(svc, TextWriter.Null, errOut);

        var exit = await cmd.RunAsync(new[] { "--last", "bogus" });

        Assert.Equal(2, exit);
        Assert.Contains("--last", errOut.ToString());
    }

    [Fact]
    public async Task RunAsync_BySessionWithLongWindow_ReturnsTwoWithGuidance()
    {
        using var conn = Seed(_ => { });
        var svc = new UsageQueryService(conn);
        var errOut = new StringWriter();
        var cmd = new UsageCommand(svc, TextWriter.Null, errOut);

        var exit = await cmd.RunAsync(new[] { "--by", "session", "--last", "90d" });

        Assert.Equal(2, exit);
        Assert.Contains("--by session requires --last", errOut.ToString());
    }

    [Fact]
    public async Task RunAsync_NoEvents_PrintsEmptyTable()
    {
        using var conn = Seed(_ => { });
        var svc = new UsageQueryService(conn);
        var output = new StringWriter();
        var cmd = new UsageCommand(svc, output);

        var exit = await cmd.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exit);
        Assert.Contains("no usage events", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_JsonFlag_EmitsStableJsonShape()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = Seed(log =>
        {
            log.InsertOrIgnore(E("claude-code", "a", nowMs - 1000, input: 100, output: 20));
            log.InsertOrIgnore(E("copilot-cli", "b", nowMs - 1000, input: null, output: 10));
        });
        var svc = new UsageQueryService(conn);
        var output = new StringWriter();
        var cmd = new UsageCommand(svc, output);

        var exit = await cmd.RunAsync(new[] { "--json" });
        Assert.Equal(0, exit);

        var json = output.ToString();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(System.Text.Json.JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("query", out _));
        Assert.True(root.TryGetProperty("buckets", out var buckets));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, buckets.ValueKind);
        Assert.Equal(2, buckets.GetArrayLength());
        Assert.True(root.TryGetProperty("grand_total", out _));
        Assert.True(root.TryGetProperty("coverage", out var cov));
        Assert.True(cov.TryGetProperty("fidelity_percent", out _));
    }

    [Fact]
    public async Task RunAsync_JsonFlag_NullTokensBecomeJsonNull()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var conn = Seed(log =>
        {
            log.InsertOrIgnore(E("copilot-cli", "b", nowMs - 1000, input: null, output: 10));
        });
        var svc = new UsageQueryService(conn);
        var output = new StringWriter();
        var cmd = new UsageCommand(svc, output);

        await cmd.RunAsync(new[] { "--json" });

        using var doc = System.Text.Json.JsonDocument.Parse(output.ToString());
        var bucket = doc.RootElement.GetProperty("buckets")[0];
        Assert.Equal(System.Text.Json.JsonValueKind.Null, bucket.GetProperty("input_tokens").ValueKind);
        Assert.Equal(10, bucket.GetProperty("output_tokens").GetInt32());
    }
}
