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
/// Integration tests for <see cref="HierarchicalIndex"/> against a real
/// <c>:memory:</c> SQLite database with the full migration stack applied,
/// real <see cref="SqliteStore"/>, real <see cref="VectorSearch"/>, and the
/// shared <see cref="FakeEmbedder"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HierarchicalIndexTests : IDisposable
{
    private readonly MsSqliteConnection _conn;
    private readonly SqliteStore _store;
    private readonly VectorSearch _vectorSearch;
    private readonly FakeEmbedder _embedder;
    private readonly HierarchicalIndex _index;

    public HierarchicalIndexTests()
    {
        _conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(_conn);
        _store = new SqliteStore(_conn);
        _vectorSearch = new VectorSearch(_conn);
        _embedder = new FakeEmbedder();
        _index = new HierarchicalIndex(_store, _embedder, _vectorSearch, _conn);
    }

    public void Dispose()
    {
        _store.Dispose();
        _conn.Dispose();
    }

    // --- helpers ---------------------------------------------------------

    private Entry GetRowById(string id)
    {
        var e = _store.Get(Tier.Cold, ContentType.Knowledge, id);
        Assert.NotNull(e);
        return e!;
    }

    private int CountVecRows(string id)
    {
        // The vec0 virtual table is "cold_knowledge_vec" with an "id" column
        // (rowid) joined to the content table by entry id. We just need to
        // know whether an embedding exists for the entry; do an indirect
        // count via the content rowid.
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM cold_knowledge_vec " +
            "WHERE rowid = (SELECT rowid FROM cold_knowledge WHERE id = $id)";
        cmd.Parameters.AddWithValue("$id", id);
        var r = cmd.ExecuteScalar();
        return r is long l ? (int)l : 0;
    }

    private static ChunkInput MakeChunk(string content, string? name = null) =>
        new(content, HeadingPath: new[] { "Top", "Sub" }, Name: name, Kind: "section");

    // --- CreateCollection ------------------------------------------------

    [Fact]
    public void CreateCollection_InsertsRowAndReturnsId()
    {
        var id = _index.CreateCollection(new CreateCollectionOpts("Docs", "/path/docs"));
        Assert.False(string.IsNullOrEmpty(id));
        var row = GetRowById(id);
        Assert.Equal(id, row.Id);
    }

    [Fact]
    public void CreateCollection_ContentIsCollectionPrefix()
    {
        var id = _index.CreateCollection(new CreateCollectionOpts("MyCollection", "/path/x"));
        var row = GetRowById(id);
        Assert.Equal("Collection: MyCollection", row.Content);
    }

    [Fact]
    public void CreateCollection_MetadataHasCollectionType()
    {
        var id = _index.CreateCollection(new CreateCollectionOpts("Alpha", "/p"));
        var row = GetRowById(id);
        Assert.Contains("\"type\":\"collection\"", row.MetadataJson);
        Assert.Contains("\"name\":\"Alpha\"", row.MetadataJson);
        Assert.Contains("\"source_path\":\"/p\"", row.MetadataJson);
    }

    [Fact]
    public void CreateCollection_EmbeddingInserted()
    {
        var id = _index.CreateCollection(new CreateCollectionOpts("Beta", "/p"));
        Assert.Equal(1, CountVecRows(id));
    }

    // --- AddDocumentToCollection ----------------------------------------

    [Fact]
    public void AddDocumentToCollection_CreatesDocAndChunkRows()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var chunks = new List<ChunkInput>
        {
            MakeChunk("alpha"),
            MakeChunk("bravo"),
            MakeChunk("charlie"),
        };
        var docId = _index.AddDocumentToCollection(
            new AddDocumentOpts(collId, "/p/file.md", chunks));

        var docRow = GetRowById(docId);
        Assert.Contains("\"type\":\"document\"", docRow.MetadataJson);

        var chunkRows = _index.GetDocumentChunks(docId);
        Assert.Equal(3, chunkRows.Count);
    }

    [Fact]
    public void AddDocumentToCollection_DocContentIsFirst500CharsOfJoined()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var bigA = new string('a', 300);
        var bigB = new string('b', 300);
        var chunks = new List<ChunkInput>
        {
            new(bigA),
            new(bigB),
        };
        var docId = _index.AddDocumentToCollection(
            new AddDocumentOpts(collId, "/p/file.md", chunks));

        // joined = 300 a's + "\n\n" + 300 b's = 602 chars; first 500 chars
        // = 300 a's + "\n\n" + 198 b's.
        var docRow = GetRowById(docId);
        Assert.Equal(500, docRow.Content.Length);
        Assert.Equal(new string('a', 300) + "\n\n" + new string('b', 198), docRow.Content);
    }

    [Fact]
    public void AddDocumentToCollection_DocContentShorterThan500_NotTruncated()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var chunks = new List<ChunkInput> { new("hi"), new("there") };
        var docId = _index.AddDocumentToCollection(
            new AddDocumentOpts(collId, "/p/file.md", chunks));
        var docRow = GetRowById(docId);
        Assert.Equal("hi\n\nthere", docRow.Content);
    }

    [Fact]
    public void AddDocumentToCollection_ChunksHaveParentIdSetToDocId()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var docId = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/file.md",
            new List<ChunkInput> { MakeChunk("a"), MakeChunk("b") }));

        var chunks = _index.GetDocumentChunks(docId);
        Assert.All(chunks, c =>
        {
            Assert.True(Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(c.ParentId));
            Assert.Equal(docId, c.ParentId.Value);
        });
    }

    [Fact]
    public void AddDocumentToCollection_AllRowsHaveCollectionId()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var docId = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/file.md",
            new List<ChunkInput> { MakeChunk("a"), MakeChunk("b") }));

        var docRow = GetRowById(docId);
        Assert.Equal(collId, docRow.CollectionId.Value);
        var chunks = _index.GetDocumentChunks(docId);
        Assert.All(chunks, c => Assert.Equal(collId, c.CollectionId.Value));
    }

    [Fact]
    public void AddDocumentToCollection_DocMetadataHasChunkCount()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var docId = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/file.md",
            new List<ChunkInput> { MakeChunk("a"), MakeChunk("b"), MakeChunk("c") }));
        var docRow = GetRowById(docId);
        Assert.Contains("\"chunk_count\":3", docRow.MetadataJson);
        Assert.Contains("\"source_path\":\"/p/file.md\"", docRow.MetadataJson);
    }

    [Fact]
    public void AddDocumentToCollection_ChunkMetadataIncludesHeadingPathNameKind()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var docId = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/file.md",
            new List<ChunkInput>
            {
                new(
                    "alpha",
                    HeadingPath: new[] { "H1", "H2" },
                    Name: "myFunc",
                    Kind: "function"),
            }));
        var chunks = _index.GetDocumentChunks(docId);
        var meta = chunks[0].MetadataJson;
        Assert.Contains("\"type\":\"chunk\"", meta);
        Assert.Contains("\"heading_path\":[\"H1\",\"H2\"]", meta);
        Assert.Contains("\"name\":\"myFunc\"", meta);
        Assert.Contains("\"kind\":\"function\"", meta);
    }

    [Fact]
    public void AddDocumentToCollection_ChunkMetadataOmitsNullFields()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var docId = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/file.md",
            new List<ChunkInput> { new("alpha") }));
        var chunks = _index.GetDocumentChunks(docId);
        var meta = chunks[0].MetadataJson;
        Assert.Equal("{\"type\":\"chunk\"}", meta);
    }

    [Fact]
    public void AddDocumentToCollection_EmbeddingsInsertedForDocAndChunks()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var docId = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/file.md",
            new List<ChunkInput> { MakeChunk("a"), MakeChunk("b"), MakeChunk("c") }));

        // Document embedding present.
        Assert.Equal(1, CountVecRows(docId));
        // Each chunk embedding present.
        var chunks = _index.GetDocumentChunks(docId);
        Assert.All(chunks, c => Assert.Equal(1, CountVecRows(c.Id)));
    }

    // --- GetCollection ---------------------------------------------------

    [Fact]
    public void GetCollection_ExistingId_ReturnsEntryWithName()
    {
        var id = _index.CreateCollection(new CreateCollectionOpts("Gamma", "/p"));
        var coll = _index.GetCollection(id);
        Assert.NotNull(coll);
        Assert.Equal(id, coll!.Entry.Id);
        Assert.Equal("Gamma", coll.Name);
    }

    [Fact]
    public void GetCollection_NonExistentId_ReturnsNull()
    {
        Assert.Null(_index.GetCollection("does-not-exist"));
    }

    [Fact]
    public void GetCollection_DocumentRowId_ReturnsNull()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var docId = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/file.md",
            new List<ChunkInput> { MakeChunk("a") }));
        Assert.Null(_index.GetCollection(docId));
    }

    // --- ListCollections -------------------------------------------------

    [Fact]
    public void ListCollections_ReturnsAllCollectionTypeRows()
    {
        var a = _index.CreateCollection(new CreateCollectionOpts("A", "/a"));
        var b = _index.CreateCollection(new CreateCollectionOpts("B", "/b"));
        var c = _index.CreateCollection(new CreateCollectionOpts("C", "/c"));
        var list = _index.ListCollections();
        var ids = list.Select(x => x.Entry.Id).ToHashSet();
        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
        Assert.Contains(c, ids);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void ListCollections_DoesNotReturnDocumentsOrChunks()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("OnlyMe", "/p"));
        _ = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/file.md",
            new List<ChunkInput> { MakeChunk("a"), MakeChunk("b") }));
        var list = _index.ListCollections();
        Assert.Single(list);
        Assert.Equal(collId, list[0].Entry.Id);
        Assert.Equal("OnlyMe", list[0].Name);
    }

    // --- GetDocumentChunks ----------------------------------------------

    [Fact]
    public void GetDocumentChunks_ReturnsChunksForDoc()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var docId = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/file.md",
            new List<ChunkInput>
            {
                new("one"),
                new("two"),
                new("three"),
            }));
        var chunks = _index.GetDocumentChunks(docId);
        Assert.Equal(3, chunks.Count);
        var contents = chunks.Select(c => c.Content).ToHashSet();
        Assert.Contains("one", contents);
        Assert.Contains("two", contents);
        Assert.Contains("three", contents);
    }

    [Fact]
    public void GetDocumentChunks_NonExistentDocId_ReturnsEmpty()
    {
        Assert.Empty(_index.GetDocumentChunks("nope"));
    }

    [Fact]
    public void GetDocumentChunks_DoesNotReturnSiblingDocsChunks()
    {
        var collId = _index.CreateCollection(new CreateCollectionOpts("C", "/p"));
        var doc1 = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/a.md",
            new List<ChunkInput> { new("a1"), new("a2") }));
        var doc2 = _index.AddDocumentToCollection(new AddDocumentOpts(
            collId, "/p/b.md",
            new List<ChunkInput> { new("b1"), new("b2"), new("b3") }));

        var doc1Chunks = _index.GetDocumentChunks(doc1);
        var doc2Chunks = _index.GetDocumentChunks(doc2);
        Assert.Equal(2, doc1Chunks.Count);
        Assert.Equal(3, doc2Chunks.Count);
        Assert.All(doc1Chunks, c => Assert.Equal(doc1, c.ParentId.Value));
        Assert.All(doc2Chunks, c => Assert.Equal(doc2, c.ParentId.Value));
    }
}
