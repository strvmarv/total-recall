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

    // Timestamps are milliseconds, so two snapshots created back to back can share
    // one. "Latest" and a name lookup must then pick the one inserted last, not an
    // arbitrary row.
    [Fact]
    public void SameMillisecondSnapshots_LatestAndByNameResolveToTheLastInserted()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            void Insert(string id, string? name, string cfg)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO config_snapshots (id, name, timestamp, config) VALUES ($id, $n, 1000, $c)";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", (object?)name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$c", cfg);
                cmd.ExecuteNonQuery();
            }

            // Ids chosen so that id order would pick the FIRST row: only insertion
            // order picks the second.
            Insert("zzz-first", "baseline", "{\"a\":1}");
            Insert("aaa-second", "baseline", "{\"a\":2}");

            Assert.Equal("aaa-second", store.GetLatest()!.Id);
            Assert.Equal("aaa-second", store.ResolveRef("latest"));
            Assert.Equal("aaa-second", store.GetByName("baseline")!.Id);
        }
    }
}
