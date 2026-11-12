using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Eval;

/// <summary>
/// A session snapshot is created unnamed. A later named request for the same
/// config (eval_snapshot, config set's pre-change:key) reuses that row, so the
/// store must attach the name to it or the name would never resolve.
/// </summary>
public sealed class ConfigSnapshotStoreNamingTests
{
    private static (MsSqliteConnection conn, ConfigSnapshotStore store) NewStore()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new ConfigSnapshotStore(conn));
    }

    [Fact]
    public void ReusedUnnamedRow_GetsTheRequestedName_AndResolvesByIt()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var sessionId = store.CreateSnapshot("{\"a\":1}");
            var namedId = store.CreateSnapshot("{\"a\":1}", "baseline");

            Assert.Equal(sessionId, namedId);
            Assert.Equal(sessionId, store.ResolveRef("baseline"));
            Assert.Equal("baseline", store.GetById(sessionId)!.Name);
        }
    }

    [Fact]
    public void ReusedNamedRow_KeepsItsExistingName()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.CreateSnapshot("{\"a\":1}", "alpha");
            store.CreateSnapshot("{\"a\":1}", "beta");

            Assert.Equal("alpha", store.GetById(id)!.Name);
            Assert.Null(store.ResolveRef("beta"));
        }
    }

    [Fact]
    public void ReusedUnnamedRow_WithNoNameRequested_StaysUnnamed()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.CreateSnapshot("{\"a\":1}");
            store.CreateSnapshot("{\"a\":1}");

            Assert.Null(store.GetById(id)!.Name);
        }
    }
}
