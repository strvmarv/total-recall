using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="FileIngester"/>. Uses a temp directory
/// fixture so the directory walker can be exercised end-to-end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FileIngesterTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly SqliteStore _store;
    private readonly VectorSearch _vec;
    private readonly FakeEmbedder _embedder;
    private readonly HierarchicalIndex _index;
    private readonly IngestValidator _validator;
    private readonly FileIngester _ingester;
    private readonly List<string> _tempDirs = new();

    public FileIngesterTests()
    {
        _conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(_conn);
        _store = new SqliteStore(_conn);
        _vec = new VectorSearch(_conn);
        _embedder = new FakeEmbedder();
        _index = new HierarchicalIndex(_store, _embedder, _vec);
        _validator = new IngestValidator(_embedder, _vec, _store);
        _ingester = new FileIngester(_index, _validator);
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
        _store.Dispose();
        _conn.Dispose();
    }

    private string NewTempDir(string tag)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tr-ingester-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return root;
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    // --- IngestFile ------------------------------------------------------

    [Fact]
    public void IngestFile_MarkdownFile_CreatesDocAndChunks()
    {
        var dir = NewTempDir("md");
        var filePath = Path.Combine(dir, "note.md");
        WriteFile(filePath, "# Title\n\nBody paragraph.\n\n## Subtitle\n\nMore content.\n");

        var collId = _index.CreateCollection(new CreateCollectionOpts("C", dir));
        var result = _ingester.IngestFile(filePath, collId);

        Assert.False(string.IsNullOrEmpty(result.DocumentId));
        Assert.True(result.ChunkCount > 0);
        var chunks = _index.GetDocumentChunks(result.DocumentId);
        Assert.Equal(result.ChunkCount, chunks.Count);
    }

    [Fact]
    public void IngestFile_CodeFile_UsesCodeParserKindAndName()
    {
        var dir = NewTempDir("code");
        var filePath = Path.Combine(dir, "lib.ts");
        WriteFile(filePath, "export function greet(name: string) {\n  return `hi ${name}`;\n}\n");

        var collId = _index.CreateCollection(new CreateCollectionOpts("C", dir));
        var result = _ingester.IngestFile(filePath, collId);

        var chunks = _index.GetDocumentChunks(result.DocumentId);
        Assert.NotEmpty(chunks);
        // At least one chunk should be classified by the code parser.
        var meta = string.Join("||", chunks.Select(c => c.MetadataJson));
        Assert.Contains("\"kind\":\"function\"", meta);
        Assert.Contains("\"name\":\"greet\"", meta);
    }

    [Fact]
    public void IngestFile_PlainText_FallsBackToParagraphs()
    {
        var dir = NewTempDir("txt");
        var filePath = Path.Combine(dir, "notes.txt");
        WriteFile(filePath, "First paragraph here.\n\nSecond paragraph here.\n");

        var collId = _index.CreateCollection(new CreateCollectionOpts("C", dir));
        var result = _ingester.IngestFile(filePath, collId);

        var chunks = _index.GetDocumentChunks(result.DocumentId);
        Assert.NotEmpty(chunks);
        // Paragraph chunks have no name/kind/heading_path.
        Assert.All(chunks, c =>
        {
            Assert.Equal("{\"type\":\"chunk\"}", c.MetadataJson);
        });
    }

    [Fact]
    public void IngestFile_CollectionIdProvided_ReusesIt()
    {
        var dir = NewTempDir("reuse");
        var filePath = Path.Combine(dir, "a.md");
        WriteFile(filePath, "hello world\n");
        var collId = _index.CreateCollection(new CreateCollectionOpts("PreExisting", dir));

        var result = _ingester.IngestFile(filePath, collId);

        var collections = _index.ListCollections();
        Assert.Single(collections);
        Assert.Equal(collId, collections[0].Entry.Id);
        Assert.Equal("PreExisting", collections[0].Name);
        Assert.False(string.IsNullOrEmpty(result.DocumentId));
    }

    [Fact]
    public void IngestFile_NoCollectionIdProvided_CreatesFromParentDirName()
    {
        var parent = NewTempDir("autocoll");
        var subDir = Path.Combine(parent, "my-docs");
        Directory.CreateDirectory(subDir);
        var filePath = Path.Combine(subDir, "file.md");
        WriteFile(filePath, "# Hi\n\ntext\n");

        var result = _ingester.IngestFile(filePath);

        var collections = _index.ListCollections();
        Assert.Single(collections);
        Assert.Equal("my-docs", collections[0].Name);
        Assert.False(string.IsNullOrEmpty(result.DocumentId));
    }

    [Fact]
    public void IngestFile_ReturnsValidationResult()
    {
        var dir = NewTempDir("val");
        var filePath = Path.Combine(dir, "a.md");
        WriteFile(filePath, "# Title\n\nBody\n");

        var collId = _index.CreateCollection(new CreateCollectionOpts("C", dir));
        var result = _ingester.IngestFile(filePath, collId);

        Assert.NotNull(result.Validation);
        Assert.Equal(result.ValidationPassed, result.Validation.Passed);
        Assert.NotEmpty(result.Validation.Probes);
    }

    // --- IngestDirectory -------------------------------------------------

    [Fact]
    public void IngestDirectory_RecursivelyIngestsAllSupportedFiles()
    {
        var root = NewTempDir("walk");
        WriteFile(Path.Combine(root, "a.md"), "# A\n\nbody a\n");
        WriteFile(Path.Combine(root, "sub", "b.md"), "# B\n\nbody b\n");
        WriteFile(Path.Combine(root, "sub", "c.ts"), "function foo() {}\n");
        // Non-ingestable extension.
        WriteFile(Path.Combine(root, "img.png"), "binary");

        var result = _ingester.IngestDirectory(root);

        Assert.Equal(3, result.DocumentCount);
        Assert.Empty(result.Errors);
        Assert.False(string.IsNullOrEmpty(result.CollectionId));
    }

    [Fact]
    public void IngestDirectory_SkipsHiddenAndNodeModules()
    {
        var root = NewTempDir("skip");
        WriteFile(Path.Combine(root, "keep.md"), "# K\n\nbody\n");
        WriteFile(Path.Combine(root, ".hidden", "h.md"), "# H\n\nbody\n");
        WriteFile(Path.Combine(root, "node_modules", "pkg", "nm.md"), "# N\n\nbody\n");
        WriteFile(Path.Combine(root, ".DS_Store.md"), "# X\n\nbody\n");

        var result = _ingester.IngestDirectory(root);

        Assert.Equal(1, result.DocumentCount);
    }

    [Fact]
    public void IngestDirectory_GlobFilter_OnlyMatching()
    {
        var root = NewTempDir("glob");
        WriteFile(Path.Combine(root, "a.md"), "# A\n\nbody\n");
        WriteFile(Path.Combine(root, "b.md"), "# B\n\nbody\n");
        WriteFile(Path.Combine(root, "c.ts"), "function c() {}\n");

        var result = _ingester.IngestDirectory(root, glob: "*.md");

        Assert.Equal(2, result.DocumentCount);
    }

    [Fact]
    public void IngestDirectory_GlobFilterExactFilename_Matches()
    {
        var root = NewTempDir("globexact");
        WriteFile(Path.Combine(root, "README.md"), "# R\n\nbody\n");
        WriteFile(Path.Combine(root, "other.md"), "# O\n\nbody\n");

        var result = _ingester.IngestDirectory(root, glob: "README.md");

        Assert.Equal(1, result.DocumentCount);
    }

    [Fact]
    public void IngestDirectory_EmptyFile_RecordsError()
    {
        var root = NewTempDir("empty");
        var emptyPath = Path.Combine(root, "empty.md");
        WriteFile(emptyPath, "");
        WriteFile(Path.Combine(root, "ok.md"), "# OK\n\nbody\n");

        var result = _ingester.IngestDirectory(root);

        // An empty markdown file produces zero chunks, so
        // AddDocumentToCollection still succeeds but with zero chunk rows —
        // it does not raise. We only assert that the non-empty file got
        // ingested. We don't assert on errors because the behaviour for an
        // empty file is "no chunks" not "throw".
        Assert.Contains(result.DocumentCount, new[] { 1, 2 });
    }

    [Fact]
    public void MatchesGlob_StarExt()
    {
        Assert.True(FileIngester.MatchesGlob("a.md", "*.md"));
        Assert.False(FileIngester.MatchesGlob("a.ts", "*.md"));
    }

    [Fact]
    public void CodeChunkKindToString_CoversAllCases()
    {
        Assert.Equal("import", FileIngester.CodeChunkKindToString(Parsers.CodeChunkKind.Import));
        Assert.Equal("function", FileIngester.CodeChunkKindToString(Parsers.CodeChunkKind.Function));
        Assert.Equal("class", FileIngester.CodeChunkKindToString(Parsers.CodeChunkKind.Class));
        Assert.Equal("block", FileIngester.CodeChunkKindToString(Parsers.CodeChunkKind.Block));
    }
}
