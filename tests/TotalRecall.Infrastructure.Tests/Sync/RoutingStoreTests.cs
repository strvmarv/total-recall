using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using NSubstitute;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

public sealed class RoutingStoreTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection OpenAndMigrate()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static Entry MakeEntry(string id, string content = "some content") =>
        new(
            id, content,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            0L, 0L, 0L, 0, 1.0,
            FSharpOption<string>.None, FSharpOption<string>.None, "", EntryType.Preference, "{}");

    [Fact]
    public void Insert_WritesLocallyAndEnqueues()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        var opts = new InsertEntryOpts("hello world");
        local.Insert(Tier.Hot, ContentType.Memory, opts).Returns("id-1");
        // EnqueueUpsert re-reads the entry to serialize the full payload.
        local.Get(Tier.Hot, ContentType.Memory, "id-1")
            .Returns(MakeEntry("id-1", "hello world"));

        var store = new RoutingStore(local, remote, syncQueue);
        var id = store.Insert(Tier.Hot, ContentType.Memory, opts);

        Assert.Equal("id-1", id);
        local.Received(1).Insert(Tier.Hot, ContentType.Memory, opts);

        var items = syncQueue.Drain(10);
        Assert.Single(items);
        Assert.Equal("memory", items[0].EntityType);
        Assert.Equal("upsert", items[0].Operation);
        Assert.Equal("id-1", items[0].EntityId);
        Assert.Contains("hello world", items[0].Payload);
    }

    [Fact]
    public void Delete_DeletesLocallyAndEnqueues()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        var store = new RoutingStore(local, remote, syncQueue);
        store.Delete(Tier.Hot, ContentType.Memory, "id-42");

        local.Received(1).Delete(Tier.Hot, ContentType.Memory, "id-42");

        var items = syncQueue.Drain(10);
        Assert.Single(items);
        Assert.Equal("memory", items[0].EntityType);
        Assert.Equal("delete", items[0].Operation);
        Assert.Equal("id-42", items[0].EntityId);
        Assert.Contains("id-42", items[0].Payload);
    }

    [Fact]
    public void InsertWithEmbedding_WhenLocalGetThrowsInEnqueueUpsert_DoesNotBubble()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        var opts = new InsertEntryOpts("diag");
        local.InsertWithEmbedding(Tier.Hot, ContentType.Memory, opts, Arg.Any<ReadOnlyMemory<float>>())
            .Returns("id-post-commit");
        // Simulate the scenario observed 2026-04-20: local write commits,
        // but the post-write Get (inside EnqueueUpsert) throws. The caller's
        // InsertWithEmbedding must still return the committed id — not
        // surface the enqueue failure as a write error.
        local.Get(Tier.Hot, ContentType.Memory, "id-post-commit")
            .Returns(_ => throw new KeyNotFoundException(
                "An index satisfying the predicate was not found in the collection."));

        var store = new RoutingStore(local, remote, syncQueue);
        var id = store.InsertWithEmbedding(
            Tier.Hot, ContentType.Memory, opts, new ReadOnlyMemory<float>(new float[384]));

        Assert.Equal("id-post-commit", id);
        // Enqueue was skipped because Get threw before we reached Enqueue.
        Assert.Empty(syncQueue.Drain(10));
    }

    [Fact]
    public void Insert_WhenLocalGetThrowsInEnqueueUpsert_DoesNotBubble()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        var opts = new InsertEntryOpts("diag2");
        local.Insert(Tier.Warm, ContentType.Memory, opts).Returns("id-warm");
        local.Get(Tier.Warm, ContentType.Memory, "id-warm")
            .Returns(_ => throw new InvalidOperationException("downstream failure"));

        var store = new RoutingStore(local, remote, syncQueue);
        var id = store.Insert(Tier.Warm, ContentType.Memory, opts);

        Assert.Equal("id-warm", id);
        Assert.Empty(syncQueue.Drain(10));
    }

    [Fact]
    public void Get_DelegatesToLocalStoreOnly()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        var entry = MakeEntry("id-1");
        local.Get(Tier.Hot, ContentType.Memory, "id-1").Returns(entry);

        var store = new RoutingStore(local, remote, syncQueue);
        var result = store.Get(Tier.Hot, ContentType.Memory, "id-1");

        Assert.NotNull(result);
        Assert.Equal("id-1", result!.Id);
        local.Received(1).Get(Tier.Hot, ContentType.Memory, "id-1");

        // No items should be enqueued for a read
        var items = syncQueue.Drain(10);
        Assert.Empty(items);
    }

    [Fact]
    public void Count_DelegatesToLocalStoreOnly()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        local.Count(Tier.Warm, ContentType.Memory).Returns(42);

        var store = new RoutingStore(local, remote, syncQueue);
        var count = store.Count(Tier.Warm, ContentType.Memory);

        Assert.Equal(42, count);
        local.Received(1).Count(Tier.Warm, ContentType.Memory);

        // No items should be enqueued for a read
        var items = syncQueue.Drain(10);
        Assert.Empty(items);
    }
}
