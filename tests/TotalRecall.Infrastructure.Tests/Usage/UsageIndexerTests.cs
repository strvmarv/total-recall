using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Usage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Usage;

public sealed class UsageIndexerTests
{
    private sealed class FakeImporter : IUsageImporter
    {
        public bool DetectResult { get; set; } = true;
        public List<UsageEvent> Events { get; } = new();
        public bool ThrowOnScan { get; set; }
        public long LastSinceMs { get; private set; }

        public string HostName { get; }
        public FakeImporter(string host) { HostName = host; }

        public bool Detect() => DetectResult;

        public async IAsyncEnumerable<UsageEvent> ScanAsync(
            long sinceMs,
            [EnumeratorCancellation] CancellationToken ct)
        {
            LastSinceMs = sinceMs;
            if (ThrowOnScan) throw new InvalidOperationException("simulated scan failure");
            foreach (var e in Events)
            {
                if (e.TimestampMs > sinceMs)
                    yield return e;
            }
            await Task.CompletedTask;
        }
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenMigrated()
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static UsageEvent E(string host, string eid, long ts) =>
        new UsageEvent(
            Host: host, HostEventId: eid, SessionId: "s", TimestampMs: ts,
            TurnIndex: 0, Model: null, ProjectPath: null, ProjectRepo: null,
            ProjectBranch: null, ProjectCommit: null, InteractionId: null,
            InputTokens: 10, CacheCreation5m: null, CacheCreation1h: null,
            CacheRead: null, OutputTokens: 5, ServiceTier: null,
            ServerToolUseJson: null, HostRequestId: null);

    [Fact]
    public async Task RunAsync_SingleHost_WritesEventsAndAdvancesWatermark()
    {
        using var conn = OpenMigrated();
        var eventLog = new UsageEventLog(conn);
        var watermarks = new UsageWatermarkStore(conn);
        var fake = new FakeImporter("claude-code");
        fake.Events.Add(E("claude-code", "a", 100));
        fake.Events.Add(E("claude-code", "b", 200));

        var stderr = new StringWriter();
        var indexer = new UsageIndexer(new[] { fake }, eventLog, watermarks, stderr);

        await indexer.RunAsync(CancellationToken.None);

        Assert.Equal(2, eventLog.CountForHost("claude-code"));
        Assert.Equal(200L, watermarks.GetLastIndexedTs("claude-code"));
    }

    [Fact]
    public async Task RunAsync_UndetectedHost_IsSkipped()
    {
        using var conn = OpenMigrated();
        var eventLog = new UsageEventLog(conn);
        var watermarks = new UsageWatermarkStore(conn);
        var fake = new FakeImporter("claude-code") { DetectResult = false };
        fake.Events.Add(E("claude-code", "a", 100));

        var indexer = new UsageIndexer(new[] { fake }, eventLog, watermarks, new StringWriter());

        await indexer.RunAsync(CancellationToken.None);

        Assert.Equal(0, eventLog.CountForHost("claude-code"));
    }

    [Fact]
    public async Task RunAsync_PerHostFailure_DoesNotBlockOtherHosts()
    {
        using var conn = OpenMigrated();
        var eventLog = new UsageEventLog(conn);
        var watermarks = new UsageWatermarkStore(conn);

        var failing = new FakeImporter("claude-code") { ThrowOnScan = true };
        var working = new FakeImporter("copilot-cli");
        working.Events.Add(E("copilot-cli", "w1", 100));

        var stderr = new StringWriter();
        var indexer = new UsageIndexer(new[] { (IUsageImporter)failing, working }, eventLog, watermarks, stderr);

        await indexer.RunAsync(CancellationToken.None);

        Assert.Equal(0, eventLog.CountForHost("claude-code"));
        Assert.Equal(1, eventLog.CountForHost("copilot-cli"));
        Assert.Equal(0L, watermarks.GetLastIndexedTs("claude-code")); // failed host NOT advanced
        Assert.Equal(100L, watermarks.GetLastIndexedTs("copilot-cli"));
        Assert.Contains("claude-code scan failed", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_SecondRun_RespectsWatermark()
    {
        using var conn = OpenMigrated();
        var eventLog = new UsageEventLog(conn);
        var watermarks = new UsageWatermarkStore(conn);
        var fake = new FakeImporter("claude-code");
        fake.Events.Add(E("claude-code", "a", 100));

        var indexer = new UsageIndexer(new[] { fake }, eventLog, watermarks, new StringWriter());

        await indexer.RunAsync(CancellationToken.None);
        // Second run: fake should be called with sinceMs = 100 (prior max)
        fake.Events.Add(E("claude-code", "b", 200));
        await indexer.RunAsync(CancellationToken.None);

        Assert.Equal(100L, fake.LastSinceMs); // last ScanAsync saw the prior watermark
        Assert.Equal(2, eventLog.CountForHost("claude-code"));
        Assert.Equal(200L, watermarks.GetLastIndexedTs("claude-code"));
    }

    [Fact]
    public async Task RunAsync_NoEvents_WatermarkStays()
    {
        using var conn = OpenMigrated();
        var eventLog = new UsageEventLog(conn);
        var watermarks = new UsageWatermarkStore(conn);
        watermarks.SetLastIndexedTs("claude-code", 500);
        var fake = new FakeImporter("claude-code");

        var stderr = new StringWriter();
        var indexer = new UsageIndexer(new[] { fake }, eventLog, watermarks, stderr);
        await indexer.RunAsync(CancellationToken.None);

        Assert.Equal(500L, watermarks.GetLastIndexedTs("claude-code"));
        Assert.Equal(string.Empty, stderr.ToString());  // silent when no new events
    }
}
