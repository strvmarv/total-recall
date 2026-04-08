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
/// Integration tests for <see cref="VectorSearch"/> against a real
/// <c>:memory:</c> database with the full migration stack applied and
/// sqlite-vec loaded via <see cref="SqliteConnection.Open"/>.
///
/// Migration 1 creates vec0 tables with the default distance metric
/// (L2-squared). For unit vectors: identical → distance 0 → score 1;
/// orthogonal unit vectors → distance 2 → score -1. Assertions focus on
/// ordering rather than exact score values.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VectorSearchTests
{
    private const int Dim = 384;

    private static (MsSqliteConnection conn, SqliteStore store, VectorSearch search) NewFixture()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new SqliteStore(conn), new VectorSearch(conn));
    }

    private static float[] UnitE(int index)
    {
        var v = new float[Dim];
        v[index] = 1f;
        return v;
    }

    private static string InsertContent(SqliteStore store, Tier tier, ContentType type, string content = "x") =>
        store.Insert(tier, type, new InsertEntryOpts(content));

    [Fact]
    public void InsertEmbedding_UnknownEntry_Throws()
    {
        var (conn, _, search) = NewFixture();
        using (conn)
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                search.InsertEmbedding(
                    Tier.Hot, ContentType.Memory,
                    "does-not-exist",
                    UnitE(0)));
            Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void InsertEmbedding_And_SearchByVector_RoundTrip()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            var id0 = InsertContent(store, Tier.Hot, ContentType.Memory, "e0");
            var id1 = InsertContent(store, Tier.Hot, ContentType.Memory, "e1");
            var id2 = InsertContent(store, Tier.Hot, ContentType.Memory, "e2");

            search.InsertEmbedding(Tier.Hot, ContentType.Memory, id0, UnitE(0));
            search.InsertEmbedding(Tier.Hot, ContentType.Memory, id1, UnitE(1));
            search.InsertEmbedding(Tier.Hot, ContentType.Memory, id2, UnitE(2));

            var results = search.SearchByVector(
                Tier.Hot, ContentType.Memory,
                UnitE(0),
                new VectorSearchOpts(TopK: 3));

            Assert.Equal(3, results.Count);
            // The identical vector should come first with score == 1.
            Assert.Equal(id0, results[0].Id);
            Assert.Equal(1.0, results[0].Score, precision: 5);
            // The remaining two are both orthogonal to the query; their
            // scores should be strictly less than the identical match.
            Assert.True(results[1].Score < results[0].Score);
            Assert.True(results[2].Score < results[0].Score);
        }
    }

    [Fact]
    public void SearchByVector_TopKLimit_RespectsK()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            for (var i = 0; i < 5; i++)
            {
                var id = InsertContent(store, Tier.Hot, ContentType.Memory, $"e{i}");
                search.InsertEmbedding(Tier.Hot, ContentType.Memory, id, UnitE(i));
            }

            var results = search.SearchByVector(
                Tier.Hot, ContentType.Memory,
                UnitE(0),
                new VectorSearchOpts(TopK: 2));

            Assert.Equal(2, results.Count);
        }
    }

    [Fact]
    public void SearchByVector_MinScoreFilter_ExcludesBelowThreshold()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            var id0 = InsertContent(store, Tier.Hot, ContentType.Memory, "exact");
            var id1 = InsertContent(store, Tier.Hot, ContentType.Memory, "orth");
            search.InsertEmbedding(Tier.Hot, ContentType.Memory, id0, UnitE(0));
            search.InsertEmbedding(Tier.Hot, ContentType.Memory, id1, UnitE(1));

            // minScore = 0.5 — exact match (score 1.0) passes; orthogonal
            // (score -1.0) is filtered out.
            var results = search.SearchByVector(
                Tier.Hot, ContentType.Memory,
                UnitE(0),
                new VectorSearchOpts(TopK: 5, MinScore: 0.5));

            Assert.Single(results);
            Assert.Equal(id0, results[0].Id);
        }
    }

    [Fact]
    public void DeleteEmbedding_ExistingEntry_RemovesRow()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            var id0 = InsertContent(store, Tier.Hot, ContentType.Memory, "keep");
            var id1 = InsertContent(store, Tier.Hot, ContentType.Memory, "drop");
            search.InsertEmbedding(Tier.Hot, ContentType.Memory, id0, UnitE(0));
            search.InsertEmbedding(Tier.Hot, ContentType.Memory, id1, UnitE(1));

            search.DeleteEmbedding(Tier.Hot, ContentType.Memory, id1);

            var results = search.SearchByVector(
                Tier.Hot, ContentType.Memory,
                UnitE(0),
                new VectorSearchOpts(TopK: 5));

            Assert.Single(results);
            Assert.Equal(id0, results[0].Id);
        }
    }

    [Fact]
    public void DeleteEmbedding_UnknownEntry_NoOp()
    {
        var (conn, _, search) = NewFixture();
        using (conn)
        {
            // Must not throw.
            search.DeleteEmbedding(Tier.Hot, ContentType.Memory, "nope");
        }
    }

    [Fact]
    public void SearchByVector_TierTypeIsolation_DoesNotBleed()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            var id = InsertContent(store, Tier.Hot, ContentType.Memory, "mem");
            search.InsertEmbedding(Tier.Hot, ContentType.Memory, id, UnitE(0));

            // Query the knowledge table in the same tier; should return nothing.
            var results = search.SearchByVector(
                Tier.Hot, ContentType.Knowledge,
                UnitE(0),
                new VectorSearchOpts(TopK: 5));

            Assert.Empty(results);
        }
    }

    [Fact]
    public void SearchMultipleTiers_MergesAndTruncates()
    {
        var (conn, store, search) = NewFixture();
        using (conn)
        {
            // Hot: identical vector lives here (best score).
            var hotId0 = InsertContent(store, Tier.Hot, ContentType.Memory, "h0");
            var hotId1 = InsertContent(store, Tier.Hot, ContentType.Memory, "h1");
            search.InsertEmbedding(Tier.Hot, ContentType.Memory, hotId0, UnitE(0));
            search.InsertEmbedding(Tier.Hot, ContentType.Memory, hotId1, UnitE(5));

            // Warm: two more orthogonal vectors.
            var warmId0 = InsertContent(store, Tier.Warm, ContentType.Memory, "w0");
            var warmId1 = InsertContent(store, Tier.Warm, ContentType.Memory, "w1");
            search.InsertEmbedding(Tier.Warm, ContentType.Memory, warmId0, UnitE(10));
            search.InsertEmbedding(Tier.Warm, ContentType.Memory, warmId1, UnitE(15));

            var results = search.SearchMultipleTiers(
                new[]
                {
                    (Tier.Hot, ContentType.Memory),
                    (Tier.Warm, ContentType.Memory),
                },
                UnitE(0),
                new VectorSearchOpts(TopK: 3));

            Assert.Equal(3, results.Count);
            // Identical vector wins.
            Assert.Equal(hotId0, results[0].Id);
            // Scores must be sorted descending.
            Assert.True(results[0].Score >= results[1].Score);
            Assert.True(results[1].Score >= results[2].Score);
        }
    }
}
