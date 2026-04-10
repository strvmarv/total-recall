using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Usage;

public sealed class UsageWatermarkStoreTests
{
    private static Microsoft.Data.Sqlite.SqliteConnection OpenMigrated()
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    [Fact]
    public void GetLastIndexedTs_UnknownHost_ReturnsZero()
    {
        using var conn = OpenMigrated();
        var store = new UsageWatermarkStore(conn);

        Assert.Equal(0L, store.GetLastIndexedTs("claude-code"));
    }

    [Fact]
    public void SetLastIndexedTs_ThenGet_RoundTrips()
    {
        using var conn = OpenMigrated();
        var store = new UsageWatermarkStore(conn);

        store.SetLastIndexedTs("claude-code", 12345);

        Assert.Equal(12345L, store.GetLastIndexedTs("claude-code"));
    }

    [Fact]
    public void SetLastIndexedTs_Overwrites()
    {
        using var conn = OpenMigrated();
        var store = new UsageWatermarkStore(conn);

        store.SetLastIndexedTs("claude-code", 100);
        store.SetLastIndexedTs("claude-code", 500);

        Assert.Equal(500L, store.GetLastIndexedTs("claude-code"));
    }

    [Fact]
    public void SetLastRollupAt_SeparateFromLastIndexedTs()
    {
        using var conn = OpenMigrated();
        var store = new UsageWatermarkStore(conn);
        store.SetLastIndexedTs("claude-code", 100);

        store.SetLastRollupAt("claude-code", 999);

        Assert.Equal(100L, store.GetLastIndexedTs("claude-code"));
        Assert.Equal(999L, store.GetLastRollupAt("claude-code"));
    }

    [Fact]
    public void GetLastRollupAt_UnknownHost_ReturnsZero()
    {
        using var conn = OpenMigrated();
        var store = new UsageWatermarkStore(conn);

        Assert.Equal(0L, store.GetLastRollupAt("claude-code"));
    }
}
