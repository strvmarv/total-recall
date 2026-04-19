using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
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
    public async Task RunAsync_WithSyncQueue_EnqueuesUsageEventForNewRow()
    {
        using var conn = OpenMigrated();
        var eventLog = new UsageEventLog(conn);
        var watermarks = new UsageWatermarkStore(conn);
        var syncQueue = new SyncQueue(conn);

        var evt = new UsageEvent(
            Host: "claude-code",
            HostEventId: "e1",
            SessionId: "session-42",
            TimestampMs: 1_700_000_000_000L,
            TurnIndex: 0,
            Model: "claude-sonnet-4-5",
            ProjectPath: "/tmp/proj",
            ProjectRepo: null,
            ProjectBranch: null,
            ProjectCommit: null,
            InteractionId: null,
            InputTokens: 123,
            CacheCreation5m: 10,
            CacheCreation1h: 7,
            CacheRead: 40,
            OutputTokens: 55,
            ServiceTier: null,
            ServerToolUseJson: null,
            HostRequestId: null);

        var fake = new FakeImporter("claude-code");
        fake.Events.Add(evt);

        var indexer = new UsageIndexer(
            new[] { (IUsageImporter)fake }, eventLog, watermarks,
            stderr: new StringWriter(),
            syncQueue: syncQueue);

        await indexer.RunAsync(CancellationToken.None);

        var items = syncQueue.Drain(10);
        Assert.Single(items);
        var item = items[0];
        Assert.Equal("usage", item.EntityType);
        Assert.Equal("push", item.Operation);
        Assert.Null(item.EntityId);

        using var doc = JsonDocument.Parse(item.Payload);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(1, root.GetArrayLength());
        var obj = root[0];
        Assert.Equal("session-42", obj.GetProperty("session_id").GetString());
        Assert.Equal("claude-code", obj.GetProperty("host").GetString());
        Assert.Equal("claude-sonnet-4-5", obj.GetProperty("model").GetString());
        Assert.Equal("/tmp/proj", obj.GetProperty("project").GetString());
        Assert.Equal(123, obj.GetProperty("input_tokens").GetInt32());
        Assert.Equal(55, obj.GetProperty("output_tokens").GetInt32());
        // 5m + 1h sum, cortex lacks the distinction
        Assert.Equal(17, obj.GetProperty("cache_creation_tokens").GetInt32());
        Assert.Equal(40, obj.GetProperty("cache_read_tokens").GetInt32());
        // timestamp must be ISO-8601 round-trip parseable
        var ts = obj.GetProperty("timestamp").GetDateTime();
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L).UtcDateTime, ts);
    }

    [Fact]
    public async Task RunAsync_WithSyncQueue_DoesNotEnqueueDedupHits()
    {
        using var conn = OpenMigrated();
        var eventLog = new UsageEventLog(conn);
        var watermarks = new UsageWatermarkStore(conn);
        var syncQueue = new SyncQueue(conn);

        // Seed the same host_event_id via InsertOrIgnore so the next run's
        // scan sees a dedup hit (InsertOrIgnore returns 0).
        var evt = E("claude-code", "dup-id", 100);
        Assert.Equal(1, eventLog.InsertOrIgnore(evt));

        var fake = new FakeImporter("claude-code");
        fake.Events.Add(evt);   // same host_event_id → dedup on second run

        var indexer = new UsageIndexer(
            new[] { (IUsageImporter)fake }, eventLog, watermarks,
            stderr: new StringWriter(),
            syncQueue: syncQueue);

        await indexer.RunAsync(CancellationToken.None);

        // Dedup hits must not produce a queue item — cortex never sees duplicates.
        Assert.Empty(syncQueue.Drain(10));
    }

    [Fact]
    public async Task RunAsync_WithSyncQueue_NullCacheCreationSerializesAsNull()
    {
        using var conn = OpenMigrated();
        var eventLog = new UsageEventLog(conn);
        var watermarks = new UsageWatermarkStore(conn);
        var syncQueue = new SyncQueue(conn);

        var fake = new FakeImporter("copilot-cli");
        fake.Events.Add(E("copilot-cli", "c1", 100)); // both CacheCreation5m/1h null

        var indexer = new UsageIndexer(
            new[] { (IUsageImporter)fake }, eventLog, watermarks,
            stderr: new StringWriter(),
            syncQueue: syncQueue);

        await indexer.RunAsync(CancellationToken.None);

        var items = syncQueue.Drain(10);
        Assert.Single(items);
        using var doc = JsonDocument.Parse(items[0].Payload);
        var obj = doc.RootElement[0];
        Assert.Equal(JsonValueKind.Null, obj.GetProperty("cache_creation_tokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, obj.GetProperty("cache_read_tokens").ValueKind);
    }

    [Fact]
    public async Task RunAsync_WithoutSyncQueue_DoesNotThrow()
    {
        using var conn = OpenMigrated();
        var eventLog = new UsageEventLog(conn);
        var watermarks = new UsageWatermarkStore(conn);
        var fake = new FakeImporter("claude-code");
        fake.Events.Add(E("claude-code", "a", 100));

        // syncQueue omitted — sqlite-only path.
        var indexer = new UsageIndexer(new[] { (IUsageImporter)fake }, eventLog, watermarks, new StringWriter());
        await indexer.RunAsync(CancellationToken.None);

        Assert.Equal(1, eventLog.CountForHost("claude-code"));
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
