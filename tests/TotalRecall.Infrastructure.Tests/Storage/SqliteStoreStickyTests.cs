// tests/TotalRecall.Infrastructure.Tests/Storage/SqliteStoreStickyTests.cs
//
// Task 5 (tier model v2) — SqliteStore sticky get/set + sticky-filtered List.
// Integration: real :memory: SQLite with the full migration stack (migration 17
// adds the `sticky` column to hot_memories/hot_knowledge).

using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Storage;

[Trait("Category", "Integration")]
public sealed class SqliteStoreStickyTests
{
    private static (SqliteStore store, Microsoft.Data.Sqlite.SqliteConnection conn) NewStore()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (new SqliteStore(conn), conn);
    }

    [Fact]
    public void SetSticky_MarksHotRowSticky()
    {
        var (store, conn) = NewStore();
        using (conn)
        {
            var id = store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("keep me"));

            Assert.False(store.IsSticky(ContentType.Memory, id));

            store.SetSticky(ContentType.Memory, id, true);

            Assert.True(store.IsSticky(ContentType.Memory, id));
            var sticky = store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { StickyOnly = true });
            Assert.Single(sticky);
            Assert.Equal(id, sticky[0].Id);
        }
    }

    [Fact]
    public void ExcludeSticky_OmitsStickyRows_StickyOnly_ReturnsOnlySticky()
    {
        var (store, conn) = NewStore();
        using (conn)
        {
            var plain = store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("plain"));
            var pinned = store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("pinned"));
            store.SetSticky(ContentType.Memory, pinned, true);

            var nonSticky = store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { ExcludeSticky = true });
            Assert.Single(nonSticky);
            Assert.Equal(plain, nonSticky[0].Id);

            var onlySticky = store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { StickyOnly = true });
            Assert.Single(onlySticky);
            Assert.Equal(pinned, onlySticky[0].Id);
        }
    }

    [Fact]
    public void SetSticky_False_ClearsSticky_EntryStaysInHot()
    {
        var (store, conn) = NewStore();
        using (conn)
        {
            var id = store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("was pinned"));
            store.SetSticky(ContentType.Memory, id, true);
            store.SetSticky(ContentType.Memory, id, false);

            Assert.False(store.IsSticky(ContentType.Memory, id));
            Assert.Equal(1, store.Count(Tier.Hot, ContentType.Memory));
            Assert.Empty(store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { StickyOnly = true }));
        }
    }
}
