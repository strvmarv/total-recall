using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Eval;

public sealed class SessionConfigSnapshotTests
{
    private static (MsSqliteConnection conn, ConfigSnapshotStore store) NewStore()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new ConfigSnapshotStore(conn));
    }

    [Fact]
    public void Resolve_CreatesTheSnapshot_AndCachesTheId()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var holder = new SessionConfigSnapshot(store, "{\"a\":1}");

            var id = holder.Resolve();

            Assert.NotNull(id);
            Assert.Equal(id, holder.Resolve());
            Assert.Single(store.ListRecent(10));
            Assert.Equal("{\"a\":1}", store.GetById(id!)!.ConfigJson);
        }
    }

    [Fact]
    public void Resolve_UnchangedConfigInANewSession_ReusesTheExistingRow()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var first = new SessionConfigSnapshot(store, "{\"a\":1}").Resolve();
            var second = new SessionConfigSnapshot(store, "{\"a\":1}").Resolve();

            Assert.Equal(first, second);
            Assert.Single(store.ListRecent(10));
        }
    }

    [Fact]
    public void Resolve_ChangedConfig_CreatesANewRow()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var first = new SessionConfigSnapshot(store, "{\"a\":1}").Resolve();
            var second = new SessionConfigSnapshot(store, "{\"a\":2}").Resolve();

            Assert.NotEqual(first, second);
            Assert.Equal(2, store.ListRecent(10).Count);
        }
    }

    [Fact]
    public void Resolve_WhenTheStoreThrows_ReturnsNullAndRetriesNextCall()
    {
        var store = new FlakyStore();
        var errors = new List<string>();
        var holder = new SessionConfigSnapshot(store, "{\"a\":1}", errors.Add);

        Assert.Null(holder.Resolve());
        Assert.Single(errors);

        store.Fail = false;
        Assert.Equal("snap-1", holder.Resolve());
        Assert.Equal("snap-1", holder.Resolve());
        Assert.Equal(2, store.Calls);
    }

    private sealed class FlakyStore : IConfigSnapshotStore
    {
        public bool Fail { get; set; } = true;
        public int Calls { get; private set; }

        public string CreateSnapshot(string configJson, string? name = null)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("database is locked");
            return "snap-1";
        }

        public ConfigSnapshotRow? GetLatest() => null;
        public ConfigSnapshotRow? GetById(string id) => null;
        public ConfigSnapshotRow? GetByName(string name) => null;
        public IReadOnlyList<ConfigSnapshotRow> ListRecent(int limit) => Array.Empty<ConfigSnapshotRow>();
        public string? ResolveRef(string nameOrId) => null;
    }
}
