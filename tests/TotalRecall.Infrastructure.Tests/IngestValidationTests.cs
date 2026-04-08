using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="IngestValidator"/> against a real
/// <c>:memory:</c> DB with full migrations, real <see cref="VectorSearch"/>,
/// and the deterministic <see cref="FakeEmbedder"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IngestValidationTests : IDisposable
{
    private readonly MsSqliteConnection _conn;
    private readonly SqliteStore _store;
    private readonly VectorSearch _vec;
    private readonly FakeEmbedder _embedder;
    private readonly HierarchicalIndex _index;
    private readonly IngestValidator _validator;

    public IngestValidationTests()
    {
        _conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(_conn);
        _store = new SqliteStore(_conn);
        _vec = new VectorSearch(_conn);
        _embedder = new FakeEmbedder();
        _index = new HierarchicalIndex(_store, _embedder, _vec, _conn);
        _validator = new IngestValidator(_embedder, _vec, _conn);
    }

    public void Dispose()
    {
        _store.Dispose();
        _conn.Dispose();
    }

    // --- SelectProbeIndices ---------------------------------------------

    [Fact]
    public void SelectProbeIndices_Empty_ReturnsEmpty()
    {
        Assert.Empty(IngestValidator.SelectProbeIndices(0));
    }

    [Fact]
    public void SelectProbeIndices_One_ReturnsSingleZero()
    {
        Assert.Equal(new[] { 0 }, IngestValidator.SelectProbeIndices(1));
    }

    [Fact]
    public void SelectProbeIndices_Three_ReturnsAll()
    {
        Assert.Equal(new[] { 0, 1, 2 }, IngestValidator.SelectProbeIndices(3));
    }

    [Fact]
    public void SelectProbeIndices_FourPlus_ReturnsThirds()
    {
        Assert.Equal(new[] { 0, 3, 6 }, IngestValidator.SelectProbeIndices(9));
        Assert.Equal(new[] { 0, 2, 5 }, IngestValidator.SelectProbeIndices(8));
    }

    // --- ValidateChunks -------------------------------------------------

    [Fact]
    public void ValidateChunks_AllChunksPresentInCollection_Passes()
    {
        // With the FakeEmbedder, each unique text length maps to a distinct
        // vector. Use chunks of different lengths so each probe retrieves
        // itself first with score ~1.0.
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var chunks = new List<ChunkInput>
        {
            new("alpha"),
            new("beta beta"),
            new("gamma gamma gamma"),
            new("delta delta delta delta"),
            new("epsilon epsilon epsilon epsilon epsilon"),
        };
        _index.AddDocumentToCollection(new AddDocumentOpts(collId, "/p/f.md", chunks));

        var result = _validator.ValidateChunks(chunks.Select(c => c.Content).ToArray(), collId);
        Assert.True(result.Passed);
        Assert.All(result.Probes, p => Assert.True(p.Passed));
    }

    [Fact]
    public void ValidateChunks_NoMatchingCollection_Fails()
    {
        // Insert content under collection A, validate against an unrelated
        // collection id. Every probe should get an empty scoped set and
        // fall to bestScore = 0.
        var collA = _index.CreateCollection(new CreateCollectionOpts("A", "/a"));
        var chunks = new List<ChunkInput>
        {
            new("foo"),
            new("bar bar"),
            new("baz baz baz"),
        };
        _index.AddDocumentToCollection(new AddDocumentOpts(collA, "/a/f.md", chunks));

        var result = _validator.ValidateChunks(
            chunks.Select(c => c.Content).ToArray(),
            "nonexistent-collection-id");

        Assert.False(result.Passed);
        Assert.All(result.Probes, p =>
        {
            Assert.Equal(0.0, p.Score);
            Assert.False(p.Passed);
        });
    }

    [Fact]
    public void ValidateChunks_LessThanThreeChunks_ProbesAll()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var chunks = new List<ChunkInput>
        {
            new("one"),
            new("two two"),
        };
        _index.AddDocumentToCollection(new AddDocumentOpts(collId, "/p/f.md", chunks));

        var result = _validator.ValidateChunks(
            chunks.Select(c => c.Content).ToArray(), collId);

        Assert.Equal(2, result.Probes.Count);
        Assert.Equal(new[] { 0, 1 }, result.Probes.Select(p => p.ChunkIndex).ToArray());
    }

    [Fact]
    public void ValidateChunks_EmptyChunkList_PassesWithNoProbes()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var result = _validator.ValidateChunks(Array.Empty<string>(), collId);
        // No probes → `Probes.All` is vacuously true.
        Assert.True(result.Passed);
        Assert.Empty(result.Probes);
    }

    [Fact]
    public void ValidateChunks_ProbeIndicesAreThirdsForManyChunks()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var chunks = new List<ChunkInput>();
        for (var i = 0; i < 9; i++)
            chunks.Add(new(new string('x', i + 1)));
        _index.AddDocumentToCollection(new AddDocumentOpts(collId, "/p/f.md", chunks));

        var result = _validator.ValidateChunks(
            chunks.Select(c => c.Content).ToArray(), collId);

        // 9 chunks: [0, 3, 6]
        Assert.Equal(new[] { 0, 3, 6 }, result.Probes.Select(p => p.ChunkIndex).ToArray());
    }
}
