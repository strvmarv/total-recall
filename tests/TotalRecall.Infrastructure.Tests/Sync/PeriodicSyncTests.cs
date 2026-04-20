using System;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

public sealed class PeriodicSyncTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection OpenAndMigrate()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    // -----------------------------------------------------------------------
    // Test 1: Start fires after interval
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Start_FiresAfterInterval()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        remote.GetUserMemoriesModifiedSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new SyncPullResult(Array.Empty<SyncEntry>(), null));

        var svc = new SyncService(local, remote, syncQueue, conn);
        using var periodic = new PeriodicSync(svc, 1);

        periodic.Start();
        await Task.Delay(1500);

        await remote.Received().GetUserMemoriesModifiedSinceAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Test 1b: Start fires immediately (dueTime = 0)
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Start_FiresImmediatelyBeforeInterval()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        remote.GetUserMemoriesModifiedSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new SyncPullResult(Array.Empty<SyncEntry>(), null));

        var svc = new SyncService(local, remote, syncQueue, conn);
        using var periodic = new PeriodicSync(svc, intervalSeconds: 60); // long interval

        periodic.Start();
        await Task.Delay(500); // well under 60s interval

        // First tick should have fired immediately, before any interval elapses.
        await remote.Received().GetUserMemoriesModifiedSinceAsync(
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Test 2: Overlapping ticks are skipped
    // -----------------------------------------------------------------------
    [Fact]
    public async Task OverlappingTicks_AreSkipped()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        // Track calls with a counter so we don't rely on timing alone.
        var callCount = 0;
        remote.GetUserMemoriesModifiedSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Delay(2000);
                return new SyncPullResult(Array.Empty<SyncEntry>(), null);
            });

        var svc = new SyncService(local, remote, syncQueue, conn);
        using var periodic = new PeriodicSync(svc, 1);

        periodic.Start();
        // Wait long enough for the immediate tick (0ms) plus the slow
        // pull (2s) plus margin for a second tick attempt.
        await Task.Delay(5000);

        // With 1s interval and 2s pull, the semaphore should skip overlapping
        // ticks. We expect 1-2 completed calls, well under the ~4 that would
        // fire without the gate.
        Assert.InRange(Volatile.Read(ref callCount), 1, 3);
    }

    // -----------------------------------------------------------------------
    // Test 3: Dispose stops timer
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Dispose_StopsTimer()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();
        var callCount = 0;

        remote.GetUserMemoriesModifiedSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref callCount);
                return new SyncPullResult(Array.Empty<SyncEntry>(), null);
            });

        var svc = new SyncService(local, remote, syncQueue, conn);
        var periodic = new PeriodicSync(svc, intervalSeconds: 1);

        periodic.Start();
        await Task.Delay(400); // let the immediate tick complete
        var countAtDispose = Volatile.Read(ref callCount);
        periodic.Dispose();
        await Task.Delay(1500); // would allow 1+ more ticks if timer kept running

        Assert.Equal(countAtDispose, Volatile.Read(ref callCount));
    }

    // -----------------------------------------------------------------------
    // Test 4: OnTick exception does not stop timer
    // -----------------------------------------------------------------------
    [Fact]
    public async Task OnTick_ExceptionDoesNotStopTimer()
    {
        using var conn = OpenAndMigrate();
        var syncQueue = new SyncQueue(conn);
        var local = Substitute.For<IStore>();
        var remote = Substitute.For<IRemoteBackend>();

        var callCount = 0;
        remote.GetUserMemoriesModifiedSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n == 1)
                    throw new InvalidOperationException("first call fails");
                return new SyncPullResult(Array.Empty<SyncEntry>(), null);
            });

        var svc = new SyncService(local, remote, syncQueue, conn);
        using var periodic = new PeriodicSync(svc, 1);

        periodic.Start();
        await Task.Delay(2500);

        // After 2.5s with 1s interval, we expect at least 2 calls
        // (first throws, second succeeds — timer keeps running).
        Assert.True(Volatile.Read(ref callCount) >= 2,
            $"Expected at least 2 calls but got {Volatile.Read(ref callCount)}");
    }
}
