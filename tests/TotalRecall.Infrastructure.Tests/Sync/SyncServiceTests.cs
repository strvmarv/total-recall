using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

public sealed class SyncServiceTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection OpenAndMigrate()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static Entry MakeEntry(string id, string content = "some content", long updatedAt = 0) =>
        new(
            id, content,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            0L, updatedAt, 0L, 0, 1.0,
            FSharpOption<string>.None, FSharpOption<string>.None, "", EntryType.Preference, "{}", 0);

    // -----------------------------------------------------------------------
    // Test 1: PullAsync inserts new memories locally
    // -----------------------------------------------------------------------
    [Fact]
    public async Task PullAsync_InsertsNewMemoriesLocally()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        // Remote returns one new entry
        var remoteEntry = new SyncEntry(
            Id: "remote-1",
            Content: "remote content",
            EntryType: "memory",
            ContentType: "memory",
            Tags: new[] { "tag1" },
            Source: "test",
            AccessCount: 0,
            DecayScore: 1.0,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow);

        remote.GetUserMemoriesModifiedSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new SyncPullResult(new[] { remoteEntry }, null));

        // Local store does not have this entry in any tier
        local.Get(Arg.Any<Tier>(), ContentType.Memory, "remote-1").Returns((Entry?)null);

        var svc = new SyncService(local, remote, syncQueue, conn);
        await svc.PullAsync(CancellationToken.None);

        // Verify Insert was called on hot tier with preserved ID
        local.Received(1).Insert(
            Tier.Hot,
            ContentType.Memory,
            Arg.Is<InsertEntryOpts>(o => o.Id == "remote-1" && o.Content == "remote content"));
    }

    // -----------------------------------------------------------------------
    // Test 2: PullAsync processes tombstones
    // -----------------------------------------------------------------------
    [Fact]
    public async Task PullAsync_ProcessesTombstones()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        var tombstone = new SyncEntry(
            Id: "dead-1",
            Content: "deleted",
            EntryType: "memory",
            ContentType: "memory",
            Tags: Array.Empty<string>(),
            Source: null,
            AccessCount: 0,
            DecayScore: 1.0,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            DeletedAt: DateTime.UtcNow);

        remote.GetUserMemoriesModifiedSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new SyncPullResult(new[] { tombstone }, null));

        // Entry exists locally in warm tier
        local.Get(Tier.Hot, ContentType.Memory, "dead-1").Returns((Entry?)null);
        local.Get(Tier.Warm, ContentType.Memory, "dead-1").Returns(MakeEntry("dead-1"));

        var svc = new SyncService(local, remote, syncQueue, conn);
        await svc.PullAsync(CancellationToken.None);

        local.Received(1).Delete(Tier.Warm, ContentType.Memory, "dead-1");
    }

    // -----------------------------------------------------------------------
    // Test 3: FlushAsync drains queue and pushes to remote
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FlushAsync_DrainsQueueAndPushesToRemote()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        // Enqueue a memory upsert
        syncQueue.Enqueue("memory", "upsert", "id-1",
            JsonSerializer.Serialize(new { id = "id-1", content = "hello" }));

        Assert.Equal(1, syncQueue.PendingCount());

        var svc = new SyncService(local, remote, syncQueue, conn);
        await svc.FlushAsync(CancellationToken.None);

        // Remote UpsertMemoriesAsync should have been called
        await remote.Received(1).UpsertMemoriesAsync(
            Arg.Is<SyncEntry[]>(arr => arr.Length == 1 && arr[0].Id == "id-1"),
            Arg.Any<CancellationToken>());

        // Queue should be empty
        Assert.Equal(0, syncQueue.PendingCount());
    }

    // -----------------------------------------------------------------------
    // Test 4: FlushAsync on unreachable marks items as failed
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FlushAsync_OnUnreachable_MarksItemsAsFailed()
    {
        using var conn = OpenAndMigrate();
        // Inject a clock so we can advance past the post-failure backoff window
        // and observe the marked attempts/error via Drain.
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = t0;
        var syncQueue = new SyncQueue(conn, () => now);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        syncQueue.Enqueue("memory", "upsert", "id-1",
            JsonSerializer.Serialize(new { id = "id-1", content = "hello" }));

        remote.UpsertMemoriesAsync(Arg.Any<SyncEntry[]>(), Arg.Any<CancellationToken>())
            .Throws(new CortexUnreachableException("connection refused"));

        var svc = new SyncService(local, remote, syncQueue, conn);
        await svc.FlushAsync(CancellationToken.None);

        // Queue should still have the item (not completed)
        Assert.Equal(1, syncQueue.PendingCount());

        // Advance past the 60s first-failure backoff so Drain resurfaces it.
        now = t0.AddSeconds(61);

        var items = syncQueue.Drain(10);
        Assert.Single(items);
        Assert.Equal(1, items[0].Attempts);
        Assert.Equal("connection refused", items[0].LastError);
    }

    // -----------------------------------------------------------------------
    // Test: Enqueue via RoutingStore → Flush via SyncService round-trips all fields
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FlushAsync_EnqueuedFullEntry_BuildsSyncEntryWithAllFields()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        // Rich entry: tags, source, access_count, decay_score, created/updated epoch ms.
        var createdMs = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var updatedMs = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var entry = new Entry(
            "id-rich", "rich content",
            FSharpOption<string>.None,
            FSharpOption<string>.Some("test-source"),
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.OfSeq(new[] { "alpha", "beta" }),
            createdMs, updatedMs, 0L,
            7, 0.42,
            FSharpOption<string>.None, FSharpOption<string>.None,
            "work", EntryType.Decision, "{}", 0);

        // Route an insert through RoutingStore so it enqueues via SyncPayload.Upsert
        var opts = new InsertEntryOpts("rich content");
        local.Insert(Tier.Hot, ContentType.Memory, opts).Returns("id-rich");
        local.Get(Tier.Hot, ContentType.Memory, "id-rich").Returns(entry);

        var routing = new RoutingStore(local, remote, syncQueue);
        routing.Insert(Tier.Hot, ContentType.Memory, opts);

        // Flush
        var svc = new SyncService(local, remote, syncQueue, conn);
        await svc.FlushAsync(CancellationToken.None);

        // Verify remote.UpsertMemoriesAsync saw a SyncEntry populated from the payload
        await remote.Received(1).UpsertMemoriesAsync(
            Arg.Is<SyncEntry[]>(arr =>
                arr.Length == 1 &&
                arr[0].Id == "id-rich" &&
                arr[0].Content == "rich content" &&
                arr[0].EntryType == "Decision" &&
                arr[0].ContentType == "Memory" &&
                arr[0].Tags.Length == 2 &&
                arr[0].Tags[0] == "alpha" &&
                arr[0].Tags[1] == "beta" &&
                arr[0].Source == "test-source" &&
                arr[0].AccessCount == 7 &&
                arr[0].DecayScore == 0.42 &&
                arr[0].Scope == "work" &&
                arr[0].CreatedAt == DateTimeOffset.FromUnixTimeMilliseconds(createdMs).UtcDateTime &&
                arr[0].UpdatedAt == DateTimeOffset.FromUnixTimeMilliseconds(updatedMs).UtcDateTime),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Test: Heavy memory backlog must not starve telemetry pushes (Bug A regression)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FlushAsync_HeavyMemoryBacklog_StillPushesTelemetry()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        // 60 memory upserts queued before any telemetry — under a single
        // 50-item Drain that prioritized memory, the telemetry rows would
        // never surface until memory cleared.
        for (int i = 0; i < 60; i++)
        {
            syncQueue.Enqueue("memory", "upsert", $"id-{i}",
                JsonSerializer.Serialize(new { id = $"id-{i}", content = "hello" }));
        }

        syncQueue.Enqueue("usage", "push", null, JsonSerializer.Serialize(new[]
        {
            new SyncUsageEvent("sess-1", "host", "model", "proj", 100, 50, null, null, DateTime.UtcNow)
        }));
        syncQueue.Enqueue("retrieval", "push", null, JsonSerializer.Serialize(new[]
        {
            new SyncRetrievalEvent("query", new[] { "hot" }, 10, 0.9, 5, 42.0, null, DateTime.UtcNow)
        }));
        syncQueue.Enqueue("compaction", "push", null, JsonSerializer.Serialize(new[]
        {
            new SyncCompactionEntry("e-1", "hot", "warm", "demote", 0.1, 0.5, DateTime.UtcNow)
        }));

        var svc = new SyncService(local, remote, syncQueue, conn);
        await svc.FlushAsync(CancellationToken.None);

        await remote.Received(1).PushUsageEventsAsync(
            Arg.Any<SyncUsageEvent[]>(), Arg.Any<CancellationToken>());
        await remote.Received(1).PushRetrievalEventsAsync(
            Arg.Any<SyncRetrievalEvent[]>(), Arg.Any<CancellationToken>());
        await remote.Received(1).PushCompactionEntriesAsync(
            Arg.Any<SyncCompactionEntry[]>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Test: Heavy telemetry backlog must fully drain in a single flush.
    // Regression for the bug where per-type drain quotas (10/flush for usage)
    // were a cap rather than an anti-starvation floor — leaving e.g. 332
    // queued usage rows requiring 33+ flushes to clear.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FlushAsync_HeavyTelemetryBacklog_DrainsFully()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        // Enqueue 250 usage rows — well above the 10/flush Phase 1 quota and
        // also above CatchUpBatchSize (100), so we exercise multiple Phase 2
        // catch-up batches.
        for (int i = 0; i < 250; i++)
        {
            syncQueue.Enqueue("usage", "push", null, JsonSerializer.Serialize(new[]
            {
                new SyncUsageEvent($"sess-{i}", "host", "model", "proj", 100, 50, null, null, DateTime.UtcNow)
            }));
        }

        var svc = new SyncService(local, remote, syncQueue, conn);
        await svc.FlushAsync(CancellationToken.None);

        // After one flush, the queue should be empty.
        var leftover = syncQueue.Drain("usage", 1000);
        Assert.Empty(leftover);

        // Verify the remote saw all 250 events across the calls it received.
        var allEvents = remote.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IRemoteBackend.PushUsageEventsAsync))
            .Select(c => (SyncUsageEvent[])c.GetArguments()[0]!)
            .SelectMany(a => a)
            .ToList();
        Assert.Equal(250, allEvents.Count);
    }

    // -----------------------------------------------------------------------
    // Test: a single un-parseable payload must not crash the flush or wedge the
    // queue. Regression for the poison-pill bug where one memory row with a raw
    // control char (invalid JSON) threw an uncaught JsonException out of
    // FlushAsync — crashing session_end and blocking every row behind it
    // (none ever marked completed OR failed → attempts stayed 0 forever).
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FlushAsync_PoisonPayload_QuarantinesItAndFlushesTheRest()
    {
        using var conn = OpenAndMigrate();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = t0;
        var syncQueue = new SyncQueue(conn, () => now);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        // A raw U+0001 control char makes this payload invalid JSON — exactly
        // what JsonDocument.Parse rejects. Enqueued FIRST so it sits at the head
        // of the memory drain batch (where the real poison pill lived).
        var poison = "{\"id\":\"bad-1\",\"content\":\"x" + (char)0x01 + "y\"}";
        syncQueue.Enqueue("memory", "upsert", "bad-1", poison);
        syncQueue.Enqueue("memory", "upsert", "good-1",
            JsonSerializer.Serialize(new { id = "good-1", content = "fine" }));

        var svc = new SyncService(local, remote, syncQueue, conn);

        // Must NOT throw (this is what session_end relies on).
        await svc.FlushAsync(CancellationToken.None);

        // The well-formed row was pushed...
        await remote.Received(1).UpsertMemoriesAsync(
            Arg.Is<SyncEntry[]>(arr => arr.Length == 1 && arr[0].Id == "good-1"),
            Arg.Any<CancellationToken>());

        // ...and removed from the queue, while the poison row was quarantined
        // (marked failed with an error + backoff), not silently dropped.
        now = t0.AddSeconds(61);
        var leftover = syncQueue.Drain("memory", 10);
        Assert.Single(leftover);
        Assert.Equal("bad-1", leftover[0].EntityId);
        Assert.Equal(1, leftover[0].Attempts);
        Assert.NotNull(leftover[0].LastError);
    }

    // -----------------------------------------------------------------------
    // Test: the telemetry drain path is equally poison-resistant. A corrupt
    // usage payload must be quarantined while the valid rows still push.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FlushAsync_PoisonTelemetryPayload_QuarantinesItAndPushesTheRest()
    {
        using var conn = OpenAndMigrate();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = t0;
        var syncQueue = new SyncQueue(conn, () => now);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        // Invalid JSON (raw control char) — must not abort the usage drain.
        syncQueue.Enqueue("usage", "push", null, "[{\"session_id\":\"x" + (char)0x01 + "\"}]");
        syncQueue.Enqueue("usage", "push", null, JsonSerializer.Serialize(new[]
        {
            new SyncUsageEvent("sess-ok", "host", "model", "proj", 100, 50, null, null, DateTime.UtcNow)
        }));

        var svc = new SyncService(local, remote, syncQueue, conn);
        await svc.FlushAsync(CancellationToken.None);

        // The valid usage row was pushed (possibly across Phase-1 + catch-up calls).
        var pushed = remote.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IRemoteBackend.PushUsageEventsAsync))
            .SelectMany(c => (SyncUsageEvent[])c.GetArguments()[0]!)
            .ToList();
        Assert.Contains(pushed, e => e.SessionId == "sess-ok");
        Assert.DoesNotContain(pushed, e => e.SessionId.Contains((char)0x01));

        // The corrupt row was quarantined (failed + backoff), not dropped.
        now = t0.AddSeconds(61);
        var leftover = syncQueue.Drain("usage", 10);
        Assert.Single(leftover);
        Assert.Equal(1, leftover[0].Attempts);
        Assert.NotNull(leftover[0].LastError);
    }

    // -----------------------------------------------------------------------
    // Test 5: FlushAsync pushes telemetry (usage, retrieval, compaction)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task FlushAsync_PushesTelemetry()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        // Enqueue usage
        var usageEvents = new[]
        {
            new SyncUsageEvent("sess-1", "host", "model", "proj", 100, 50, null, null, DateTime.UtcNow)
        };
        syncQueue.Enqueue("usage", "push", null, JsonSerializer.Serialize(usageEvents));

        // Enqueue retrieval
        var retrievalEvents = new[]
        {
            new SyncRetrievalEvent("query", new[] { "hot" }, 10, 0.9, 5, 42.0, null, DateTime.UtcNow)
        };
        syncQueue.Enqueue("retrieval", "push", null, JsonSerializer.Serialize(retrievalEvents));

        // Enqueue compaction
        var compactionEntries = new[]
        {
            new SyncCompactionEntry("e-1", "hot", "warm", "demote", 0.1, 0.5, DateTime.UtcNow)
        };
        syncQueue.Enqueue("compaction", "push", null, JsonSerializer.Serialize(compactionEntries));

        var svc = new SyncService(local, remote, syncQueue, conn);
        await svc.FlushAsync(CancellationToken.None);

        await remote.Received(1).PushUsageEventsAsync(
            Arg.Is<SyncUsageEvent[]>(arr => arr.Length == 1 && arr[0].SessionId == "sess-1"),
            Arg.Any<CancellationToken>());

        await remote.Received(1).PushRetrievalEventsAsync(
            Arg.Is<SyncRetrievalEvent[]>(arr => arr.Length == 1 && arr[0].Query == "query"),
            Arg.Any<CancellationToken>());

        await remote.Received(1).PushCompactionEntriesAsync(
            Arg.Is<SyncCompactionEntry[]>(arr => arr.Length == 1 && arr[0].EntryId == "e-1"),
            Arg.Any<CancellationToken>());

        // Queue should be empty
        Assert.Equal(0, syncQueue.PendingCount());
    }
}
