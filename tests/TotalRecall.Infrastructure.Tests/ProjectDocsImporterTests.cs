using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="ProjectDocsImporter"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProjectDocsImporterTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly SqliteStore _store;
    private readonly VectorSearch _vec;
    private readonly FakeEmbedder _embedder;
    private readonly HierarchicalIndex _index;
    private readonly IngestValidator _validator;
    private readonly FileIngester _ingester;
    private readonly ImportLog _importLog;
    private readonly List<string> _tempDirs = new();

    public ProjectDocsImporterTests()
    {
        _conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(_conn);
        _store = new SqliteStore(_conn);
        _vec = new VectorSearch(_conn);
        _embedder = new FakeEmbedder();
        _index = new HierarchicalIndex(_store, _embedder, _vec);
        _validator = new IngestValidator(_embedder, _vec, _store);
        _ingester = new FileIngester(_index, _validator);
        _importLog = new ImportLog(_conn);
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

    private string NewCwd(string tag)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tr-pdocs-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return root;
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private ProjectDocsImporter NewImporter(string cwd) =>
        new(_ingester, _index, _importLog, cwd);

    // --- Detect ---------------------------------------------------------

    [Fact]
    public void Detect_NoDocFilesOrDirs_False()
    {
        var cwd = NewCwd("nodocs");
        Assert.False(NewImporter(cwd).Detect());
    }

    [Fact]
    public void Detect_HasReadme_True()
    {
        var cwd = NewCwd("readme");
        WriteFile(Path.Combine(cwd, "README.md"), "# R\n");
        Assert.True(NewImporter(cwd).Detect());
    }

    [Fact]
    public void Detect_HasDocsDir_True()
    {
        var cwd = NewCwd("docsdir");
        Directory.CreateDirectory(Path.Combine(cwd, "docs"));
        Assert.True(NewImporter(cwd).Detect());
    }

    // --- Scan -----------------------------------------------------------

    [Fact]
    public void Scan_CountsAllDocFilesAndDirContents()
    {
        var cwd = NewCwd("scan");
        WriteFile(Path.Combine(cwd, "README.md"), "# R\n");
        WriteFile(Path.Combine(cwd, "CLAUDE.md"), "# C\n");
        WriteFile(Path.Combine(cwd, "docs", "a.md"), "# A\n");
        WriteFile(Path.Combine(cwd, "docs", "sub", "b.md"), "# B\n");
        WriteFile(Path.Combine(cwd, "docs", "img.png"), "binary");

        var scan = NewImporter(cwd).Scan();

        Assert.Equal(0, scan.MemoryFiles);
        Assert.Equal(0, scan.SessionFiles);
        Assert.Equal(4, scan.KnowledgeFiles); // README, CLAUDE, a.md, b.md
    }

    // --- ImportMemories -------------------------------------------------

    [Fact]
    public void ImportMemories_AlwaysEmpty()
    {
        var cwd = NewCwd("mem");
        WriteFile(Path.Combine(cwd, "README.md"), "# R\n");
        var result = NewImporter(cwd).ImportMemories();
        Assert.Equal(0, result.Imported);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Errors);
    }

    // --- ImportKnowledge ------------------------------------------------

    [Fact]
    public void ImportKnowledge_NoFiles_ReturnsEmpty()
    {
        var cwd = NewCwd("none");
        var result = NewImporter(cwd).ImportKnowledge();
        Assert.Equal(0, result.Imported);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void ImportKnowledge_TopLevelDocs_Imported()
    {
        var cwd = NewCwd("top");
        WriteFile(Path.Combine(cwd, "README.md"), "# Project\n\nIntro text.\n");
        WriteFile(Path.Combine(cwd, "AGENTS.md"), "# Agents\n\nInfo here.\n");

        var result = NewImporter(cwd).ImportKnowledge();
        Assert.Equal(2, result.Imported);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ImportKnowledge_DocsDirRecursivelyIncluded()
    {
        var cwd = NewCwd("docsrec");
        WriteFile(Path.Combine(cwd, "docs", "intro.md"), "# Intro\n\nalpha\n");
        WriteFile(Path.Combine(cwd, "docs", "advanced", "topic.md"), "# Topic\n\nbeta\n");
        WriteFile(Path.Combine(cwd, "doc", "other.md"), "# Other\n\ngamma\n");

        var result = NewImporter(cwd).ImportKnowledge();
        Assert.Equal(3, result.Imported);
    }

    [Fact]
    public void ImportKnowledge_CreatesCollectionNamedAfterCwd()
    {
        var cwd = NewCwd("coll");
        var baseName = Path.GetFileName(cwd);
        WriteFile(Path.Combine(cwd, "README.md"), "# R\n\nbody\n");

        NewImporter(cwd).ImportKnowledge();

        var collections = _index.ListCollections();
        Assert.Contains(collections, c => c.Name == $"{baseName}-project-docs");
    }

    [Fact]
    public void ImportKnowledge_ReusesExistingCollection()
    {
        var cwd = NewCwd("reuse");
        var baseName = Path.GetFileName(cwd);
        var collName = $"{baseName}-project-docs";
        var preId = _index.CreateCollection(new CreateCollectionOpts(collName, cwd));

        WriteFile(Path.Combine(cwd, "README.md"), "# R\n\nbody\n");
        NewImporter(cwd).ImportKnowledge();

        var list = _index.ListCollections().Where(c => c.Name == collName).ToList();
        Assert.Single(list);
        Assert.Equal(preId, list[0].Entry.Id);
    }

    [Fact]
    public void ImportKnowledge_DuplicateContent_Skipped()
    {
        var cwd = NewCwd("dup");
        WriteFile(Path.Combine(cwd, "README.md"), "# R\n\nsome body\n");

        var first = NewImporter(cwd).ImportKnowledge();
        Assert.Equal(1, first.Imported);

        var second = NewImporter(cwd).ImportKnowledge();
        Assert.Equal(0, second.Imported);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public void ImportKnowledge_EmptyFile_Skipped()
    {
        var cwd = NewCwd("empty");
        WriteFile(Path.Combine(cwd, "README.md"), "   \n\n");

        var result = NewImporter(cwd).ImportKnowledge();
        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void ImportKnowledge_LogImport_RecordsHashes()
    {
        var cwd = NewCwd("log");
        WriteFile(Path.Combine(cwd, "README.md"), "# Unique Readme Content\n\nbody one\n");
        WriteFile(Path.Combine(cwd, "AGENTS.md"), "# Unique Agents Content\n\nbody two\n");

        NewImporter(cwd).ImportKnowledge();

        // Both file hashes should now be recorded in import_log.
        var hash1 = ImportLog.ContentHash(
            File.ReadAllText(Path.Combine(cwd, "README.md")).Trim());
        var hash2 = ImportLog.ContentHash(
            File.ReadAllText(Path.Combine(cwd, "AGENTS.md")).Trim());
        Assert.True(_importLog.IsAlreadyImported(hash1));
        Assert.True(_importLog.IsAlreadyImported(hash2));
    }

    [Fact]
    public void Name_IsProjectDocs()
    {
        var cwd = NewCwd("name");
        Assert.Equal("project-docs", NewImporter(cwd).Name);
    }
}
