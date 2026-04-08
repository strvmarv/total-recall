using System;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Eval;

[Trait("Category", "Integration")]
public sealed class ConfigSnapshotStoreTests
{
    private static (MsSqliteConnection conn, ConfigSnapshotStore store) NewStore()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new ConfigSnapshotStore(conn));
    }

    [Fact]
    public void CreateSnapshot_Inserts_ThenDedupsOnIdenticalJson()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id1 = store.CreateSnapshot("{\"a\":1}", "alpha");
            var id2 = store.CreateSnapshot("{\"a\":1}", "beta");
            Assert.Equal(id1, id2);
        }
    }

    [Fact]
    public void CreateSnapshot_DifferentJson_InsertsNew()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id1 = store.CreateSnapshot("{\"a\":1}", "alpha");
            var id2 = store.CreateSnapshot("{\"a\":2}", "beta");
            Assert.NotEqual(id1, id2);
        }
    }

    [Fact]
    public void ResolveRef_ById_Latest_ByName()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id1 = store.CreateSnapshot("{\"a\":1}", "alpha");
            System.Threading.Thread.Sleep(5);
            var id2 = store.CreateSnapshot("{\"a\":2}", "alpha");

            Assert.Equal(id1, store.ResolveRef(id1));
            Assert.Equal(id2, store.ResolveRef("latest"));
            Assert.Equal(id2, store.ResolveRef("alpha"));
            Assert.Null(store.ResolveRef("nope"));
        }
    }

    [Fact]
    public void ListRecent_ReturnsDescByTimestamp()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            store.CreateSnapshot("{\"a\":1}", "n1");
            System.Threading.Thread.Sleep(5);
            store.CreateSnapshot("{\"a\":2}", "n2");
            System.Threading.Thread.Sleep(5);
            store.CreateSnapshot("{\"a\":3}", "n3");

            var rows = store.ListRecent(10);
            Assert.Equal(3, rows.Count);
            Assert.Equal("n3", rows[0].Name);
            Assert.Equal("n1", rows[2].Name);
        }
    }
}
