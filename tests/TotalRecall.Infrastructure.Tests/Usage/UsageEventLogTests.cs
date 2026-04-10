using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Usage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Usage;

public sealed class UsageEventLogTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection OpenMigrated()
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static UsageEvent MakeEvent(
        string host = "claude-code",
        string hostEventId = "evt-1",
        string sessionId = "sess-1",
        long ts = 1000,
        int? input = 100,
        int? output = 50) =>
        new UsageEvent(
            Host: host, HostEventId: hostEventId, SessionId: sessionId,
            TimestampMs: ts, TurnIndex: 0, Model: "claude-opus-4.1",
            ProjectPath: "/p", ProjectRepo: null, ProjectBranch: null, ProjectCommit: null,
            InteractionId: null,
            InputTokens: input, CacheCreation5m: null, CacheCreation1h: null,
            CacheRead: null, OutputTokens: output,
            ServiceTier: null, ServerToolUseJson: null, HostRequestId: null);

    [Fact]
    public void InsertOrIgnore_NewEvent_WritesRow()
    {
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        var evt = MakeEvent();

        var inserted = log.InsertOrIgnore(evt);

        Assert.Equal(1, inserted);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM usage_events";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void InsertOrIgnore_DuplicateHostEventId_IsSkipped()
    {
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        var evt1 = MakeEvent(hostEventId: "dup");
        var evt2 = MakeEvent(hostEventId: "dup", ts: 9999);

        log.InsertOrIgnore(evt1);
        var inserted = log.InsertOrIgnore(evt2);

        Assert.Equal(0, inserted);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM usage_events";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void InsertOrIgnore_NullableTokenColumns_PersistAsNull()
    {
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        var evt = MakeEvent(input: null, output: null);

        log.InsertOrIgnore(evt);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT input_tokens, output_tokens FROM usage_events LIMIT 1";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.True(r.IsDBNull(0));
        Assert.True(r.IsDBNull(1));
    }

    [Fact]
    public void CountForHost_ReturnsOnlyMatchingHost()
    {
        using var conn = OpenMigrated();
        var log = new UsageEventLog(conn);
        log.InsertOrIgnore(MakeEvent(host: "claude-code", hostEventId: "a"));
        log.InsertOrIgnore(MakeEvent(host: "claude-code", hostEventId: "b"));
        log.InsertOrIgnore(MakeEvent(host: "copilot-cli", hostEventId: "c"));

        Assert.Equal(2, log.CountForHost("claude-code"));
        Assert.Equal(1, log.CountForHost("copilot-cli"));
        Assert.Equal(0, log.CountForHost("unknown"));
    }
}
