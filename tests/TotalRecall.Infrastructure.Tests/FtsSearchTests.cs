using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="FtsSearch"/> against a real
/// <c>:memory:</c> database with the full migration stack applied. The
/// Migration 3 after-insert triggers populate the FTS5 virtual tables
/// automatically, so tests only insert content rows via
/// <see cref="SqliteStore"/> and then query via <see cref="FtsSearch"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FtsSearchTests
{
    private static (MsSqliteConnection conn, SqliteStore store, FtsSearch search) NewFixture()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new SqliteStore(conn), new FtsSearch(conn));
    }

    private static string InsertContent(
        SqliteStore store,
        Tier tier,
        ContentType type,
        string content,
        IReadOnlyList<string>? tags = null) =>
        store.Insert(tier, type, new InsertEntryOpts(content, Tags: tags));

    [Fact]
    public void SearchByFts_SimpleQuery_ReturnsMatchingRows()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            var idA = InsertContent(store, Tier.Hot, ContentType.Memory, "hello world");
            var idB = InsertContent(store, Tier.Hot, ContentType.Memory, "hello there");
            var idC = InsertContent(store, Tier.Hot, ContentType.Memory, "goodbye world");

            var results = search.SearchByFts(
                Tier.Hot, ContentType.Memory, "hello", new FtsSearchOpts(TopK: 10));

            var ids = results.Select(r => r.Id).ToHashSet();
            Assert.Contains(idA, ids);
            Assert.Contains(idB, ids);
            Assert.DoesNotContain(idC, ids);
        }
    }

    [Fact]
    public void SearchByFts_TopKLimit_RespectsK()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            for (var i = 0; i < 5; i++)
                InsertContent(store, Tier.Hot, ContentType.Memory, $"hello entry {i}");

            var results = search.SearchByFts(
                Tier.Hot, ContentType.Memory, "hello", new FtsSearchOpts(TopK: 2));

            Assert.Equal(2, results.Count);
        }
    }

    [Fact]
    public void SearchByFts_NoMatches_ReturnsEmpty()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            InsertContent(store, Tier.Hot, ContentType.Memory, "hello world");

            var results = search.SearchByFts(
                Tier.Hot, ContentType.Memory, "nonexistent", new FtsSearchOpts(TopK: 10));

            Assert.Empty(results);
        }
    }

    [Fact]
    public void SearchByFts_EmptyQuery_ReturnsEmpty()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            InsertContent(store, Tier.Hot, ContentType.Memory, "hello world");

            Assert.Empty(search.SearchByFts(
                Tier.Hot, ContentType.Memory, "", new FtsSearchOpts(TopK: 10)));
            Assert.Empty(search.SearchByFts(
                Tier.Hot, ContentType.Memory, "   \t  ", new FtsSearchOpts(TopK: 10)));
        }
    }

    [Fact]
    public void SearchByFts_SpecialCharsSanitized_DoesNotThrow()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            InsertContent(store, Tier.Hot, ContentType.Memory, "alpha beta gamma");

            // Tokens that would be FTS5 syntax errors if passed raw:
            // leading '-' (NOT), '*' (prefix), '"' (phrase delimiter).
            var ex = Record.Exception(() => search.SearchByFts(
                Tier.Hot, ContentType.Memory,
                "-alpha *beta \"gamma",
                new FtsSearchOpts(TopK: 10)));

            Assert.Null(ex);
        }
    }

    [Fact]
    public void SearchByFts_TagsColumnIsIndexed_FindsByTag()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            // Content has nothing matching "rocket"; only the tags column
            // does. The FTS5 table indexes (content, tags), so this should
            // still return the row.
            var id = InsertContent(
                store, Tier.Hot, ContentType.Memory,
                "unrelated body text",
                tags: new[] { "rocket", "space" });

            var results = search.SearchByFts(
                Tier.Hot, ContentType.Memory, "rocket", new FtsSearchOpts(TopK: 10));

            Assert.Single(results);
            Assert.Equal(id, results[0].Id);
        }
    }

    [Fact]
    public void SearchByFts_TierTypeIsolation_DoesNotBleed()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            InsertContent(store, Tier.Hot, ContentType.Memory, "hello world");

            var results = search.SearchByFts(
                Tier.Hot, ContentType.Knowledge, "hello", new FtsSearchOpts(TopK: 10));

            Assert.Empty(results);
        }
    }

    [Fact]
    public void SearchByFts_ScoreNormalization_InRange0To1()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            InsertContent(store, Tier.Hot, ContentType.Memory, "hello world hello hello");
            InsertContent(store, Tier.Hot, ContentType.Memory, "hello world");
            InsertContent(store, Tier.Hot, ContentType.Memory, "hello there friend");

            var results = search.SearchByFts(
                Tier.Hot, ContentType.Memory, "hello", new FtsSearchOpts(TopK: 10));

            Assert.NotEmpty(results);
            foreach (var r in results)
            {
                Assert.InRange(r.Score, 0.0, 1.0);
            }
        }
    }

    [Fact]
    public void SearchByFts_SingleMatch_ScoreIsOne()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            InsertContent(store, Tier.Hot, ContentType.Memory, "hello world");
            InsertContent(store, Tier.Hot, ContentType.Memory, "nothing here");

            var results = search.SearchByFts(
                Tier.Hot, ContentType.Memory, "hello", new FtsSearchOpts(TopK: 10));

            Assert.Single(results);
            Assert.Equal(1.0, results[0].Score, precision: 10);
        }
    }
}
