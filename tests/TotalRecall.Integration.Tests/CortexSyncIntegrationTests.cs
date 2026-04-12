using System;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Integration.Tests;

[Trait("Category", "Integration")]
public class CortexSyncIntegrationTests : IDisposable
{
    // Configure via env vars
    private readonly string _cortexUrl = Environment.GetEnvironmentVariable("CORTEX_URL") ?? "http://localhost:5000";
    private readonly string _cortexPat = Environment.GetEnvironmentVariable("CORTEX_PAT") ?? "tr_test";

    // In-memory SQLite for local store
    private readonly MsSqliteConnection _conn;
    private readonly SqliteStore _sqliteStore;
    private readonly SyncQueue _syncQueue;

    public CortexSyncIntegrationTests()
    {
        _conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(_conn);
        _sqliteStore = new SqliteStore(_conn);
        _syncQueue = new SyncQueue(_conn);
    }

    public void Dispose()
    {
        _sqliteStore.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task RoundTrip_StoreLocally_FlushToCortex_PullBack()
    {
        var cortexClient = CortexClient.Create(_cortexUrl, _cortexPat);
        var routingStore = new RoutingStore(_sqliteStore, cortexClient, _syncQueue);
        var syncService = new SyncService(_sqliteStore, cortexClient, _syncQueue, _conn);

        // 1. Store locally via RoutingStore
        var id = routingStore.Insert(Tier.Hot, ContentType.Memory,
            new InsertEntryOpts("integration test memory", Source: "integration-test"));
        Assert.Equal(1, _syncQueue.PendingCount());

        // 2. Flush to Cortex
        await syncService.FlushAsync(CancellationToken.None);
        Assert.Equal(0, _syncQueue.PendingCount());

        // 3. Simulate new device: delete locally, then pull
        _sqliteStore.Delete(Tier.Hot, ContentType.Memory, id);
        Assert.Null(_sqliteStore.Get(Tier.Hot, ContentType.Memory, id));

        await syncService.PullAsync(CancellationToken.None);

        // 4. Verify the memory came back
        var pulled = _sqliteStore.Get(Tier.Hot, ContentType.Memory, id);
        Assert.NotNull(pulled);
        Assert.Equal("integration test memory", pulled.Content);
    }
}
