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

[Trait("Category", "Integration")]
public sealed class CursorImporterTests : IDisposable
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
        public required IEmbedder Embedder { get; init; }
        public required string ConfigPath { get; init; }
        public required string ExtensionPath { get; init; }
        public List<string> ProjectPaths { get; init; } = new();

        public void Dispose() => Conn.Dispose();
    }

    private string NewTempDir(string tag)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tr-cu-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return root;
    }

    private Fixture NewFixture(
        bool withConfig = true,
        bool withExtension = true,
        bool withGlobalDb = false,
        string? globalRulesContent = "Global Cursor rules: be concise.",
        int projectCount = 0,
        bool withCursorRules = false,
        bool withMdcRules = false,
        IEmbedder? embedder = null)
    {
        var configPath = NewTempDir("config");
        var extensionPath = NewTempDir("ext");

        if (!withConfig) { Directory.Delete(configPath, true); _tempDirs.Remove(configPath); }
        if (!withExtension) { Directory.Delete(extensionPath, true); _tempDirs.Remove(extensionPath); }

        if (withConfig && withGlobalDb)
        {
            var dbDir = Path.Combine(configPath, "User", "globalStorage");
            Directory.CreateDirectory(dbDir);
            var dbPath = Path.Combine(dbDir, "state.vscdb");
            using var seedConn = new MsSqliteConnection($"Data Source={dbPath}");
            seedConn.Open();
            using (var create = seedConn.CreateCommand())
            {
                create.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT)";
                create.ExecuteNonQuery();
            }
            if (globalRulesContent is not null)
            {
                using var ins = seedConn.CreateCommand();
                ins.CommandText = "INSERT INTO ItemTable (key, value) VALUES ('aicontext.personalContext', $v)";
                ins.Parameters.AddWithValue("$v", globalRulesContent);
                ins.ExecuteNonQuery();
            }
        }

        var projects = new List<string>();
        if (withConfig && projectCount > 0)
        {
            var wsRoot = Path.Combine(configPath, "User", "workspaceStorage");
            Directory.CreateDirectory(wsRoot);
            for (var i = 0; i < projectCount; i++)
            {
                var p = NewTempDir($"proj{i}");
                projects.Add(p);

                var wsDir = Path.Combine(wsRoot, $"ws{i}");
                Directory.CreateDirectory(wsDir);
                var url = new Uri(p).AbsoluteUri; // file:///path/...
                File.WriteAllText(
                    Path.Combine(wsDir, "workspace.json"),
                    "{\"folder\":\"" + url + "\"}");

                if (withCursorRules)
                {
                    File.WriteAllText(
                        Path.Combine(p, ".cursorrules"),
                        $"Legacy cursor rules for project {i}");
                }
                if (withMdcRules)
                {
                    var rulesDir = Path.Combine(p, ".cursor", "rules");
                    Directory.CreateDirectory(rulesDir);
                    File.WriteAllText(
                        Path.Combine(rulesDir, $"rule{i}.mdc"),
                        $"---\nname: rule-{i}\ndescription: project {i} mdc rule\n---\nMDC rule body for project {i}.");
                }
            }
        }

        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        return new Fixture
        {
            Conn = conn,
            Store = new SqliteStore(conn),
            Vec = new VectorSearch(conn),
            Log = new ImportLog(conn),
            Embedder = embedder ?? new FakeEmbedder(),
            ConfigPath = configPath,
            ExtensionPath = extensionPath,
            ProjectPaths = projects,
        };
    }

    private CursorImporter NewImporter(Fixture f) =>
        new CursorImporter(f.Store, f.Embedder, f.Vec, f.Log, f.ConfigPath, f.ExtensionPath);

    // ---------- Detect ----------

    [Fact]
    public void Detect_BothPaths_ReturnsTrue()
    {
        using var f = NewFixture();
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_OnlyConfig_ReturnsTrue()
    {
        using var f = NewFixture(withExtension: false);
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_OnlyExtension_ReturnsTrue()
    {
        using var f = NewFixture(withConfig: false);
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_NeitherPath_ReturnsFalse()
    {
        using var f = NewFixture(withConfig: false, withExtension: false);
        Assert.False(NewImporter(f).Detect());
    }

    // ---------- Scan ----------

    [Fact]
    public void Scan_GlobalDbAndProjectRules_Counted()
    {
        using var f = NewFixture(
            withGlobalDb: true,
            projectCount: 2,
            withCursorRules: true,
            withMdcRules: true);
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        // 1 global + 2 .cursorrules + 2 .mdc files
        Assert.Equal(5, r.KnowledgeFiles);
        Assert.Equal(0, r.SessionFiles);
    }

    [Fact]
    public void Scan_NoConfig_ReturnsZeros()
    {
        using var f = NewFixture(withConfig: false, withExtension: true);
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        Assert.Equal(0, r.KnowledgeFiles);
        Assert.Equal(0, r.SessionFiles);
    }

    [Fact]
    public void Scan_AlwaysZeroSessionsAndMemories()
    {
        using var f = NewFixture(
            withGlobalDb: true,
            projectCount: 1,
            withCursorRules: true);
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        Assert.Equal(0, r.SessionFiles);
    }

    // ---------- ImportMemories ----------

    [Fact]
    public void ImportMemories_AlwaysReturnsEmpty()
    {
        using var f = NewFixture(withGlobalDb: true);
        var r = NewImporter(f).ImportMemories();
        Assert.Equal(0, r.Imported);
        Assert.Empty(r.Errors);
    }

    // ---------- ImportKnowledge ----------

    [Fact]
    public void ImportKnowledge_GlobalRules_GoesToWarmFromVscdb()
    {
        using var f = NewFixture(withGlobalDb: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported);
        Assert.Empty(r.Errors);

        var warm = f.Store.List(Tier.Warm, ContentType.Knowledge, null);
        var e = Assert.Single(warm);
        Assert.True(e.SourceTool!.Value.IsCursor);
        Assert.Contains("global-rules", e.Tags);
        Assert.Equal("Global Cursor rules: be concise.", e.Content);
        // The source column is the .vscdb path itself.
        Assert.EndsWith("state.vscdb", e.Source!.Value);
    }

    [Fact]
    public void ImportKnowledge_GlobalDbMissing_NoWarmEntry()
    {
        using var f = NewFixture(withGlobalDb: false);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(0, r.Imported);
        Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Knowledge));
    }

    [Fact]
    public void ImportKnowledge_GlobalDbWithoutKey_NoWarmEntry()
    {
        using var f = NewFixture(withGlobalDb: true, globalRulesContent: null);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(0, r.Imported);
        Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Knowledge));
    }

    [Fact]
    public void ImportKnowledge_LegacyCursorRules_GoesToCold()
    {
        using var f = NewFixture(projectCount: 2, withCursorRules: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(2, r.Imported);
        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        Assert.Equal(2, cold.Count);
        Assert.All(cold, e =>
        {
            Assert.True(e.SourceTool!.Value.IsCursor);
            Assert.Contains("cursorrules", e.Tags);
            Assert.Contains("legacy", e.Tags);
        });
    }

    [Fact]
    public void ImportKnowledge_MdcRules_GoesToColdWithFrontmatter()
    {
        using var f = NewFixture(projectCount: 1, withMdcRules: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported);
        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        var e = Assert.Single(cold);
        var tags = e.Tags.ToArray();
        // frontmatter name PREPENDED before "cursor-rule"
        Assert.Equal("rule-0", tags[0]);
        Assert.Equal("cursor-rule", tags[tags.Length - 1]);
        // Frontmatter description becomes summary
        Assert.Equal("project 0 mdc rule", e.Summary!.Value);
        // Frontmatter is stripped
        Assert.DoesNotContain("---", e.Content);
        Assert.Contains("MDC rule body", e.Content);
    }

    [Fact]
    public void ImportKnowledge_DuplicateContent_Skipped()
    {
        using var f = NewFixture(
            withGlobalDb: true,
            projectCount: 1,
            withCursorRules: true);
        var imp = NewImporter(f);
        var first = imp.ImportKnowledge();
        Assert.Equal(2, first.Imported);
        var second = imp.ImportKnowledge();
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.Skipped);
        Assert.Empty(second.Errors);
    }

    [Fact]
    public void ImportKnowledge_DedupesProjectPaths()
    {
        // Create two workspace.json entries pointing to the same project.
        using var f = NewFixture(projectCount: 1, withCursorRules: true);
        var wsRoot = Path.Combine(f.ConfigPath, "User", "workspaceStorage");
        var dupDir = Path.Combine(wsRoot, "ws-dup");
        Directory.CreateDirectory(dupDir);
        var url = new Uri(f.ProjectPaths[0]).AbsoluteUri;
        File.WriteAllText(
            Path.Combine(dupDir, "workspace.json"),
            "{\"folder\":\"" + url + "\"}");

        var r = NewImporter(f).ImportKnowledge();
        // Even though the project appears twice in workspaceStorage, the
        // .cursorrules file should be imported exactly once.
        Assert.Equal(1, r.Imported);
        Assert.Equal(1, f.Store.Count(Tier.Cold, ContentType.Knowledge));
    }

    [Fact]
    public void ImportKnowledge_LogImport_RecordsAllRows()
    {
        using var f = NewFixture(
            withGlobalDb: true,
            projectCount: 1,
            withCursorRules: true,
            withMdcRules: true);
        NewImporter(f).ImportKnowledge();

        using var cmd = f.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM import_log WHERE source_tool = 'cursor'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(3L, count);
    }

    [Fact]
    public void ImportKnowledge_EmbedderThrowsForOneFile_OtherFilesStillImported()
    {
        // 1 global + 2 cursorrules; throw on 2nd embed call
        using var f = NewFixture(
            withGlobalDb: true,
            projectCount: 2,
            withCursorRules: true,
            embedder: new ThrowingEmbedder(throwOnCall: 2));
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(2, r.Imported);
        Assert.Single(r.Errors);
        Assert.Contains("synthetic embed failure", r.Errors[0]);
    }

    [Fact]
    public void ImportKnowledge_MalformedWorkspaceJson_Skipped()
    {
        using var f = NewFixture(projectCount: 1, withCursorRules: true);
        var wsRoot = Path.Combine(f.ConfigPath, "User", "workspaceStorage");
        var badDir = Path.Combine(wsRoot, "ws-bad");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "workspace.json"), "{not valid json");

        var r = NewImporter(f).ImportKnowledge();
        // The good project's .cursorrules still imports
        Assert.Equal(1, r.Imported);
        Assert.Empty(r.Errors);
    }
}
