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
        var clock = new MutableClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var queue = new SyncQueue(conn, clock.Read);

        queue.Enqueue("memory", "upsert", "id-1", "{}");

        var items = queue.Drain(10);
        Assert.Equal(0, items[0].Attempts);
        Assert.Null(items[0].LastError);

        queue.MarkFailed(items[0].Id, "connection refused");

        // Advance past the 60s backoff so Drain re-surfaces the row.
        clock.Advance(TimeSpan.FromSeconds(61));
        var after = queue.Drain(10);
        Assert.Single(after);
        Assert.Equal(1, after[0].Attempts);
        Assert.Equal("connection refused", after[0].LastError);

        queue.MarkFailed(after[0].Id, "timeout");
        clock.Advance(TimeSpan.FromSeconds(121)); // 2nd failure → 120s
        var after2 = queue.Drain(10);
        Assert.Single(after2);
        Assert.Equal(2, after2[0].Attempts);
        Assert.Equal("timeout", after2[0].LastError);
    }

    [Fact]
    public void Drain_RespectsBackoffWindow_AndReleasesAfterExpiry()
    {
        using var conn = OpenAndMigrate();
        var clock = new MutableClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var queue = new SyncQueue(conn, clock.Read);

        queue.Enqueue("memory", "upsert", "id-1", "{}");
        var first = queue.Drain(10);
        Assert.Single(first);

        queue.MarkFailed(first[0].Id, "boom");

        // First failure → 60s backoff. Drain inside the window returns nothing.
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(queue.Drain(10));

        // After the window, item is eligible again.
        clock.Advance(TimeSpan.FromSeconds(31));
        var afterBackoff = queue.Drain(10);
        Assert.Single(afterBackoff);
        Assert.Equal(1, afterBackoff[0].Attempts);
    }

    [Fact]
    public void Drain_RetriesPastFormerCap_WithLongerBackoff()
    {
        using var conn = OpenAndMigrate();
        var clock = new MutableClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var queue = new SyncQueue(conn, clock.Read);

        queue.Enqueue("memory", "upsert", "id-1", "{}");
        var id = queue.Drain(10)[0].Id;

        // Fail 10 times — old behavior would have permanently dropped this.
        for (int i = 0; i < 10; i++)
        {
            queue.MarkFailed(id, $"fail {i}");
            clock.Advance(TimeSpan.FromHours(2)); // step well past every backoff window
        }

        var drained = queue.Drain(10);
        Assert.Single(drained);
        Assert.Equal(10, drained[0].Attempts);
    }

    [Fact]
    public void ComputeBackoffSeconds_IsExponentialAndCapped()
    {
        Assert.Equal(0, SyncQueue.ComputeBackoffSeconds(0));
        Assert.Equal(60, SyncQueue.ComputeBackoffSeconds(1));
        Assert.Equal(120, SyncQueue.ComputeBackoffSeconds(2));
        Assert.Equal(240, SyncQueue.ComputeBackoffSeconds(3));
        Assert.Equal(3600, SyncQueue.ComputeBackoffSeconds(7));
        Assert.Equal(3600, SyncQueue.ComputeBackoffSeconds(50));
    }

    [Fact]
    public void PendingCount_CountsAllNotYetCompleted()
    {
        using var conn = OpenAndMigrate();
        var queue = new SyncQueue(conn);

        Assert.Equal(0, queue.PendingCount());

        queue.Enqueue("memory", "upsert", "id-1", "{}");
        queue.Enqueue("memory", "upsert", "id-2", "{}");
        queue.Enqueue("memory", "upsert", "id-3", "{}");

        Assert.Equal(3, queue.PendingCount());

        var items = queue.Drain(10);
        queue.MarkCompleted(items[0].Id);
        Assert.Equal(2, queue.PendingCount());

        // Items in a backoff window still count as pending.
        for (int i = 0; i < 5; i++)
            queue.MarkFailed(items[1].Id, "err");

        Assert.Equal(2, queue.PendingCount());
    }

    private sealed class MutableClock
    {
        private DateTime _now;
        public MutableClock(DateTime start) => _now = start;
        public DateTime Read() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
