using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

[Trait("Category", "Integration")]
public sealed class SqliteStoreSourceFilterTests
{
    [Fact]
    public void List_SourceFilter_ReturnsOnlyMatchingRows()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var store = new SqliteStore(conn);

        store.Insert(Tier.Cold, ContentType.Knowledge,
            new InsertEntryOpts(Content: "doc a", Source: "/src/a.md"));
        store.Insert(Tier.Cold, ContentType.Knowledge,
            new InsertEntryOpts(Content: "doc b", Source: "/src/b.md"));

        var results = store.List(Tier.Cold, ContentType.Knowledge,
            new ListEntriesOpts { Source = "/src/a.md" });

        Assert.Single(results);
        Assert.Equal("doc a", results[0].Content);
    }
}
