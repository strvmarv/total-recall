using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

public sealed class SyncQueueTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection OpenAndMigrate()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    [Fact]
    public void Enqueue_and_Drain_ReturnsItemsInOrder()
    {
        using var conn = OpenAndMigrate();
        var queue = new SyncQueue(conn);

        queue.Enqueue("memory", "upsert", "id-1", "{\"a\":1}");
        queue.Enqueue("memory", "delete", "id-2", "{\"b\":2}");
        queue.Enqueue("knowledge", "upsert", null, "{\"c\":3}");

        var items = queue.Drain(10);

        Assert.Equal(3, items.Count);
        Assert.Equal("id-1", items[0].EntityId);
        Assert.Equal("upsert", items[0].Operation);
        Assert.Equal("{\"a\":1}", items[0].Payload);
        Assert.Equal("id-2", items[1].EntityId);
        Assert.Equal("delete", items[1].Operation);
        Assert.Null(items[2].EntityId);
        Assert.Equal("knowledge", items[2].EntityType);

        // Verify ordering is ascending by id
        Assert.True(items[0].Id < items[1].Id);
        Assert.True(items[1].Id < items[2].Id);
    }

    [Fact]
    public void MarkCompleted_RemovesItemFromQueue()
    {
        using var conn = OpenAndMigrate();
        var queue = new SyncQueue(conn);

        queue.Enqueue("memory", "upsert", "id-1", "{}");
        queue.Enqueue("memory", "upsert", "id-2", "{}");

        var items = queue.Drain(10);
        Assert.Equal(2, items.Count);

        queue.MarkCompleted(items[0].Id);

        var remaining = queue.Drain(10);
        Assert.Single(remaining);
        Assert.Equal("id-2", remaining[0].EntityId);
    }

    [Fact]
    public void MarkFailed_IncrementsAttemptsAndRecordsError()
    {
        using var conn = OpenAndMigrate();
        var queue = new SyncQueue(conn);

        queue.Enqueue("memory", "upsert", "id-1", "{}");

        var items = queue.Drain(10);
        Assert.Equal(0, items[0].Attempts);
        Assert.Null(items[0].LastError);

        queue.MarkFailed(items[0].Id, "connection refused");

        var after = queue.Drain(10);
        Assert.Single(after);
        Assert.Equal(1, after[0].Attempts);
        Assert.Equal("connection refused", after[0].LastError);

        queue.MarkFailed(after[0].Id, "timeout");

        var after2 = queue.Drain(10);
        Assert.Single(after2);
        Assert.Equal(2, after2[0].Attempts);
        Assert.Equal("timeout", after2[0].LastError);
    }

    [Fact]
    public void Drain_ExcludesItemsAtMaxAttempts()
    {
        using var conn = OpenAndMigrate();
        var queue = new SyncQueue(conn);

        queue.Enqueue("memory", "upsert", "id-1", "{}");

        var items = queue.Drain(10);
        var id = items[0].Id;

        // Fail 10 times to reach MaxAttempts
        for (int i = 0; i < 10; i++)
            queue.MarkFailed(id, $"error {i}");

        var drained = queue.Drain(10);
        Assert.Empty(drained);
    }

    [Fact]
    public void PendingCount_ReturnsCorrectCount()
    {
        using var conn = OpenAndMigrate();
        var queue = new SyncQueue(conn);

        Assert.Equal(0, queue.PendingCount());

        queue.Enqueue("memory", "upsert", "id-1", "{}");
        queue.Enqueue("memory", "upsert", "id-2", "{}");
        queue.Enqueue("memory", "upsert", "id-3", "{}");

        Assert.Equal(3, queue.PendingCount());

        // Complete one
        var items = queue.Drain(10);
        queue.MarkCompleted(items[0].Id);
        Assert.Equal(2, queue.PendingCount());

        // Exhaust attempts on another
        for (int i = 0; i < 10; i++)
            queue.MarkFailed(items[1].Id, "err");

        Assert.Equal(1, queue.PendingCount());
    }
}
