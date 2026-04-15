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
            FSharpOption<string>.None, FSharpOption<string>.None, "", "{}");

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
        var syncQueue = new SyncQueue(conn);
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

        // Item should have incremented attempts
        var items = syncQueue.Drain(10);
        Assert.Single(items);
        Assert.Equal(1, items[0].Attempts);
        Assert.Equal("connection refused", items[0].LastError);
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
