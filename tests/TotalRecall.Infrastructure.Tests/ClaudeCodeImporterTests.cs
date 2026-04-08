using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="ClaudeCodeImporter"/>. Uses a real
/// <c>:memory:</c> database with full migrations applied and a hermetic
/// temp-dir fixture tree per test. The embedder is a deterministic fake
/// so the tests don't depend on the ONNX runtime (covered elsewhere).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClaudeCodeImporterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public required MsSqliteConnection Conn { get; init; }
        public required SqliteStore Store { get; init; }
        public required VectorSearch Vec { get; init; }
        public required ImportLog Log { get; init; }
        public required FakeEmbedder Embedder { get; init; }
        public required string BasePath { get; init; }

        public void Dispose() => Conn.Dispose();
    }

    private string NewTempDir(string tag)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tr-cc-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return root;
    }

    /// <summary>
    /// Create a complete fixture: in-memory DB with migrations, a temp
    /// base dir containing a realistic Claude Code tree, and a fake
    /// embedder. Layout matches the TS test fixtures.
    /// </summary>
    private Fixture NewFixture(
        bool withTopLevelClaudeMd = true,
        bool withPerProjectClaudeMd = true,
        bool withSessionJsonl = true,
        bool withMemoryFiles = true)
    {
        var basePath = NewTempDir("base");
        var projectDir = Path.Combine(basePath, "projects", "-home-user-myproj");
        Directory.CreateDirectory(projectDir);

        if (withTopLevelClaudeMd)
        {
            File.WriteAllText(
                Path.Combine(basePath, "CLAUDE.md"),
                "---\nname: top-level\ndescription: top knowledge\ntype: reference\n---\nTop-level project-wide context and standards.");
        }

        if (withPerProjectClaudeMd)
        {
            File.WriteAllText(
                Path.Combine(projectDir, "CLAUDE.md"),
                "Per-project doc; scanned but not imported.");
        }

        if (withSessionJsonl)
        {
            File.WriteAllText(
                Path.Combine(projectDir, "session-abc.jsonl"),
                "{\"role\":\"user\"}\n");
        }

        if (withMemoryFiles)
        {
            var memoryDir = Path.Combine(projectDir, "memory");
            Directory.CreateDirectory(memoryDir);

            File.WriteAllText(
                Path.Combine(memoryDir, "MEMORY.md"),
                "Index file; excluded from imports.");

            File.WriteAllText(
                Path.Combine(memoryDir, "user_role.md"),
                "---\nname: user role\ndescription: senior engineer\ntype: user\n---\nUser is a senior Go engineer working on data pipelines.");

            File.WriteAllText(
                Path.Combine(memoryDir, "feedback_testing.md"),
                "---\nname: feedback testing\ndescription: prefers TDD\ntype: feedback\n---\nPrefers table-driven tests and tight feedback loops.");

            File.WriteAllText(
                Path.Combine(memoryDir, "architecture.md"),
                "---\nname: architecture\ndescription: system design\ntype: reference\n---\nArchitecture: F# Core + C# Infrastructure + AOT host.");
        }

        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var store = new SqliteStore(conn);
        var vec = new VectorSearch(conn);
        var log = new ImportLog(conn);
        var emb = new FakeEmbedder();

        return new Fixture
        {
            Conn = conn,
            Store = store,
            Vec = vec,
            Log = log,
            Embedder = emb,
            BasePath = basePath,
        };
    }

    private ClaudeCodeImporter NewImporter(Fixture f) =>
        new ClaudeCodeImporter(f.Store, f.Embedder, f.Vec, f.Log, f.BasePath);

    // ---------- Detect ----------

    [Fact]
    public void Detect_BasePathExists_ReturnsTrue()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            Assert.True(NewImporter(f).Detect());
        }
    }

    [Fact]
    public void Detect_NoProjectsDir_ReturnsFalse()
    {
        var basePath = NewTempDir("noproj");
        // basePath exists but no /projects dir
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var imp = new ClaudeCodeImporter(
                new SqliteStore(conn), new FakeEmbedder(),
                new VectorSearch(conn), new ImportLog(conn), basePath);
            Assert.False(imp.Detect());
        }
    }

    [Fact]
    public void Detect_BasePathDoesNotExist_ReturnsFalse()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "tr-cc-missing-" + Guid.NewGuid().ToString("N"));
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var imp = new ClaudeCodeImporter(
                new SqliteStore(conn), new FakeEmbedder(),
                new VectorSearch(conn), new ImportLog(conn), bogus);
            Assert.False(imp.Detect());
        }
    }

    // ---------- Scan ----------

    [Fact]
    public void Scan_ReportsCorrectFileCounts()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            var result = NewImporter(f).Scan();
            // user_role + feedback_testing + architecture = 3 (MEMORY.md excluded)
            Assert.Equal(3, result.MemoryFiles);
            Assert.Equal(1, result.KnowledgeFiles); // per-project CLAUDE.md
            Assert.Equal(1, result.SessionFiles);
        }
    }

    [Fact]
    public void Scan_NoProjectsDir_ReturnsZeros()
    {
        var basePath = NewTempDir("nop-scan");
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var imp = new ClaudeCodeImporter(
                new SqliteStore(conn), new FakeEmbedder(),
                new VectorSearch(conn), new ImportLog(conn), basePath);
            var result = imp.Scan();
            Assert.Equal(0, result.MemoryFiles);
            Assert.Equal(0, result.KnowledgeFiles);
            Assert.Equal(0, result.SessionFiles);
        }
    }

    // ---------- ImportMemories ----------

    [Fact]
    public void ImportMemories_PopulatesWarmMemoryAndColdKnowledge()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            var result = NewImporter(f).ImportMemories(project: "myproj");
            Assert.Equal(3, result.Imported);
            Assert.Equal(0, result.Skipped);
            Assert.Empty(result.Errors);

            // user_role + feedback_testing → warm/memory (2)
            Assert.Equal(2, f.Store.Count(Tier.Warm, ContentType.Memory));
            // architecture → cold/knowledge (1)
            Assert.Equal(1, f.Store.Count(Tier.Cold, ContentType.Knowledge));
        }
    }

    [Fact]
    public void ImportMemories_FrontmatterTypeReference_RoutesToCold()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            NewImporter(f).ImportMemories();

            var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
            var e = Assert.Single(cold);
            Assert.Contains("Architecture", e.Content);
            Assert.True(e.SourceTool!.Value.IsClaudeCode);
        }
    }

    [Fact]
    public void ImportMemories_NameInFrontmatter_BecomesTag()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            NewImporter(f).ImportMemories();
            var warm = f.Store.List(Tier.Warm, ContentType.Memory, null);
            var userRole = warm.FirstOrDefault(e => e.Content.Contains("Go engineer"));
            Assert.NotNull(userRole);
            Assert.Equal(new[] { "user role" }, userRole!.Tags.ToArray());
        }
    }

    [Fact]
    public void ImportMemories_NoNameInFrontmatter_EmptyTags()
    {
        var f = NewFixture(withMemoryFiles: false);
        using (f.Conn)
        {
            // custom file without a name field
            var memoryDir = Path.Combine(f.BasePath, "projects", "-home-user-myproj", "memory");
            Directory.CreateDirectory(memoryDir);
            File.WriteAllText(
                Path.Combine(memoryDir, "unnamed.md"),
                "---\ndescription: no name here\ntype: user\n---\nBody without a name tag.");

            NewImporter(f).ImportMemories();
            var warm = f.Store.List(Tier.Warm, ContentType.Memory, null);
            var entry = Assert.Single(warm);
            Assert.Empty(entry.Tags);
        }
    }

    [Fact]
    public void ImportMemories_DuplicateContent_Skipped()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            var imp = NewImporter(f);
            var first = imp.ImportMemories();
            Assert.Equal(3, first.Imported);

            var warmBefore = f.Store.Count(Tier.Warm, ContentType.Memory);
            var coldBefore = f.Store.Count(Tier.Cold, ContentType.Knowledge);

            var second = imp.ImportMemories();
            Assert.Equal(0, second.Imported);
            Assert.Equal(3, second.Skipped);
            Assert.Empty(second.Errors);

            Assert.Equal(warmBefore, f.Store.Count(Tier.Warm, ContentType.Memory));
            Assert.Equal(coldBefore, f.Store.Count(Tier.Cold, ContentType.Knowledge));
        }
    }

    [Fact]
    public void ImportMemories_NonMarkdownFiles_Ignored()
    {
        var f = NewFixture(withMemoryFiles: false);
        using (f.Conn)
        {
            var memoryDir = Path.Combine(f.BasePath, "projects", "-home-user-myproj", "memory");
            Directory.CreateDirectory(memoryDir);
            File.WriteAllText(Path.Combine(memoryDir, "notes"), "no extension");
            File.WriteAllText(Path.Combine(memoryDir, "notes.txt"), "wrong extension");

            var result = NewImporter(f).ImportMemories();
            Assert.Equal(0, result.Imported);
            Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Memory));
            Assert.Equal(0, f.Store.Count(Tier.Cold, ContentType.Knowledge));
        }
    }

    [Fact]
    public void ImportMemories_MEMORY_md_Excluded()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            NewImporter(f).ImportMemories();
            var warm = f.Store.List(Tier.Warm, ContentType.Memory, null);
            var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
            foreach (var e in warm.Concat(cold))
            {
                Assert.DoesNotContain("Index file", e.Content);
                Assert.False(e.Source!.Value.EndsWith("MEMORY.md"));
            }
        }
    }

    [Fact]
    public void ImportMemories_ProjectArg_PopulatesProjectColumn()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            NewImporter(f).ImportMemories(project: "myproj");
            var warm = f.Store.List(Tier.Warm, ContentType.Memory, null);
            Assert.NotEmpty(warm);
            foreach (var e in warm)
            {
                Assert.Equal("myproj", e.Project!.Value);
            }
        }
    }

    [Fact]
    public void ImportMemories_NoProjectArg_NullProject()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            NewImporter(f).ImportMemories();
            var warm = f.Store.List(Tier.Warm, ContentType.Memory, null);
            Assert.NotEmpty(warm);
            foreach (var e in warm)
            {
                Assert.True(Microsoft.FSharp.Core.FSharpOption<string>.get_IsNone(e.Project));
            }
        }
    }

    [Fact]
    public void ImportMemories_NoProjectsDir_ReturnsEmpty()
    {
        var basePath = NewTempDir("noproj-im");
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var imp = new ClaudeCodeImporter(
                new SqliteStore(conn), new FakeEmbedder(),
                new VectorSearch(conn), new ImportLog(conn), basePath);
            var result = imp.ImportMemories();
            Assert.Equal(0, result.Imported);
            Assert.Equal(0, result.Skipped);
            Assert.Empty(result.Errors);
        }
    }

    // ---------- ImportKnowledge ----------

    [Fact]
    public void ImportKnowledge_TopLevelClaudeMd_ImportsToWarmKnowledge()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            var result = NewImporter(f).ImportKnowledge();
            Assert.Equal(1, result.Imported);
            Assert.Equal(0, result.Skipped);
            Assert.Empty(result.Errors);

            var warm = f.Store.List(Tier.Warm, ContentType.Knowledge, null);
            var e = Assert.Single(warm);
            Assert.Contains("Top-level", e.Content);
            Assert.Equal(new[] { "pinned" }, e.Tags.ToArray());
            Assert.True(e.SourceTool!.Value.IsClaudeCode);
        }
    }

    [Fact]
    public void ImportKnowledge_DuplicateContent_Skipped()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            var imp = NewImporter(f);
            var first = imp.ImportKnowledge();
            Assert.Equal(1, first.Imported);

            var second = imp.ImportKnowledge();
            Assert.Equal(0, second.Imported);
            Assert.Equal(1, second.Skipped);
            Assert.Equal(1, f.Store.Count(Tier.Warm, ContentType.Knowledge));
        }
    }

    [Fact]
    public void ImportKnowledge_NoTopLevelFile_ReturnsEmpty()
    {
        var f = NewFixture(withTopLevelClaudeMd: false);
        using (f.Conn)
        {
            var result = NewImporter(f).ImportKnowledge();
            Assert.Equal(0, result.Imported);
            Assert.Equal(0, result.Skipped);
            Assert.Empty(result.Errors);
            Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Knowledge));
        }
    }

    [Fact]
    public void ImportKnowledge_PerProjectClaudeMd_NotImported()
    {
        // Only a per-project CLAUDE.md exists; top-level one does NOT.
        var f = NewFixture(withTopLevelClaudeMd: false);
        using (f.Conn)
        {
            var result = NewImporter(f).ImportKnowledge();
            Assert.Equal(0, result.Imported);
            // Nothing should have landed in warm/knowledge.
            Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Knowledge));
        }
    }

    // ---------- Import log rows ----------

    [Fact]
    public void ImportLog_AfterSuccessfulImport_ContainsHashes()
    {
        var f = NewFixture();
        using (f.Conn)
        {
            NewImporter(f).ImportMemories();
            NewImporter(f).ImportKnowledge();

            using var cmd = f.Conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM import_log WHERE source_tool = 'claude-code'";
            var count = (long)cmd.ExecuteScalar()!;
            // 3 memory files + 1 top-level CLAUDE.md = 4 rows.
            Assert.Equal(4L, count);
        }
    }
}
