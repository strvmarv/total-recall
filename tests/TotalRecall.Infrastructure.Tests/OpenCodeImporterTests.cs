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
public sealed class OpenCodeImporterTests : IDisposable
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
        public required string DataPath { get; init; }
        public required string ConfigPath { get; init; }
        public List<string> ProjectPaths { get; init; } = new();

        public void Dispose() => Conn.Dispose();
    }

    private string NewTempDir(string tag)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tr-oc-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return root;
    }

    private Fixture NewFixture(
        bool withConfig = true,
        bool withData = true,
        bool withGlobalAgentsMd = true,
        bool withDb = false,
        int projectCount = 0,
        bool withProjectAgentsMd = false,
        bool withAgentDir = false,
        bool withCommandDir = false,
        IEmbedder? embedder = null)
    {
        var dataPath = NewTempDir("data");
        var configPath = NewTempDir("config");

        if (!withData) { Directory.Delete(dataPath, true); _tempDirs.Remove(dataPath); }
        if (!withConfig) { Directory.Delete(configPath, true); _tempDirs.Remove(configPath); }

        if (withConfig && withGlobalAgentsMd)
        {
            File.WriteAllText(
                Path.Combine(configPath, "AGENTS.md"),
                "---\nname: global-agents\n---\n# Global agent guidelines\nBe helpful.");
        }

        var projects = new List<string>();
        if (withDb && withData)
        {
            var dbPath = Path.Combine(dataPath, "opencode.db");
            using var seedConn = new MsSqliteConnection($"Data Source={dbPath}");
            seedConn.Open();
            using (var create = seedConn.CreateCommand())
            {
                create.CommandText = "CREATE TABLE project (worktree TEXT)";
                create.ExecuteNonQuery();
            }
            for (var i = 0; i < projectCount; i++)
            {
                var p = NewTempDir($"proj{i}");
                projects.Add(p);
                using var ins = seedConn.CreateCommand();
                ins.CommandText = "INSERT INTO project (worktree) VALUES ($w)";
                ins.Parameters.AddWithValue("$w", p);
                ins.ExecuteNonQuery();

                // Always create .opencode/ so the importer descends into the project.
                Directory.CreateDirectory(Path.Combine(p, ".opencode"));

                if (withAgentDir)
                {
                    var agentDir = Path.Combine(p, ".opencode", "agent");
                    Directory.CreateDirectory(agentDir);
                    File.WriteAllText(
                        Path.Combine(agentDir, $"helper{i}.md"),
                        $"---\nname: helper-{i}\ndescription: project {i} agent\n---\nAgent body {i}");
                }
                if (withCommandDir)
                {
                    var cmdDir = Path.Combine(p, ".opencode", "command");
                    Directory.CreateDirectory(cmdDir);
                    File.WriteAllText(
                        Path.Combine(cmdDir, $"do-thing{i}.md"),
                        $"# Command {i}\nDo thing {i}");
                }
                if (withProjectAgentsMd)
                {
                    File.WriteAllText(
                        Path.Combine(p, "AGENTS.md"),
                        $"# Project {i} agents\nProject-scoped guidance {i}.");
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
            DataPath = dataPath,
            ConfigPath = configPath,
            ProjectPaths = projects,
        };
    }

    private OpenCodeImporter NewImporter(Fixture f) =>
        new OpenCodeImporter(f.Store, f.Embedder, f.Vec, f.Log, f.DataPath, f.ConfigPath);

    // ---------- Detect ----------

    [Fact]
    public void Detect_BothPathsExist_ReturnsTrue()
    {
        using var f = NewFixture();
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_OnlyDataPath_ReturnsTrue()
    {
        using var f = NewFixture(withConfig: false);
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_OnlyConfigPath_ReturnsTrue()
    {
        using var f = NewFixture(withData: false);
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_NeitherPath_ReturnsFalse()
    {
        using var f = NewFixture(withConfig: false, withData: false);
        Assert.False(NewImporter(f).Detect());
    }

    // ---------- Scan ----------

    [Fact]
    public void Scan_GlobalAgentsMdAndDb_CountsBoth()
    {
        using var f = NewFixture(withGlobalAgentsMd: true, withDb: true, projectCount: 2);
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        Assert.Equal(1, r.KnowledgeFiles); // only the global AGENTS.md
        Assert.Equal(1, r.SessionFiles);   // sentinel "1" for the DB
    }

    [Fact]
    public void Scan_NoData_ReturnsZeros()
    {
        using var f = NewFixture(withConfig: false, withData: false, withGlobalAgentsMd: false);
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        Assert.Equal(0, r.KnowledgeFiles);
        Assert.Equal(0, r.SessionFiles);
    }

    [Fact]
    public void Scan_AlwaysZeroMemoryFiles()
    {
        using var f = NewFixture(withDb: true, projectCount: 1, withAgentDir: true);
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
    }

    // ---------- ImportMemories ----------

    [Fact]
    public void ImportMemories_AlwaysReturnsEmpty()
    {
        using var f = NewFixture(withDb: true, projectCount: 1, withAgentDir: true);
        var r = NewImporter(f).ImportMemories();
        Assert.Equal(0, r.Imported);
        Assert.Equal(0, r.Skipped);
        Assert.Empty(r.Errors);
        Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Memory));
    }

    [Fact]
    public void ImportMemories_WithProjectArg_StillEmpty()
    {
        using var f = NewFixture();
        var r = NewImporter(f).ImportMemories("anything");
        Assert.Equal(0, r.Imported);
        Assert.Empty(r.Errors);
    }

    // ---------- ImportKnowledge ----------

    [Fact]
    public void ImportKnowledge_GlobalAgentsMd_GoesToWarm()
    {
        using var f = NewFixture(withGlobalAgentsMd: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported);
        Assert.Empty(r.Errors);
        Assert.Equal(1, f.Store.Count(Tier.Warm, ContentType.Knowledge));
        Assert.Equal(0, f.Store.Count(Tier.Cold, ContentType.Knowledge));

        var warm = f.Store.List(Tier.Warm, ContentType.Knowledge, null);
        var e = Assert.Single(warm);
        Assert.True(e.SourceTool!.Value.IsOpencode);
        Assert.Contains("agents-md", e.Tags);
        Assert.Contains("global", e.Tags);
        // Frontmatter is parsed and stripped from content for the global file.
        Assert.DoesNotContain("---", e.Content);
        Assert.Contains("Be helpful", e.Content);
    }

    [Fact]
    public void ImportKnowledge_NoGlobalAgentsMd_NoWarmEntry()
    {
        using var f = NewFixture(withGlobalAgentsMd: false);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(0, r.Imported);
        Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Knowledge));
    }

    [Fact]
    public void ImportKnowledge_PerProjectAgentFiles_GoToCold()
    {
        using var f = NewFixture(
            withGlobalAgentsMd: false,
            withDb: true,
            projectCount: 2,
            withAgentDir: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(2, r.Imported);
        Assert.Empty(r.Errors);
        Assert.Equal(2, f.Store.Count(Tier.Cold, ContentType.Knowledge));

        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        Assert.All(cold, e =>
        {
            Assert.True(e.SourceTool!.Value.IsOpencode);
            var tags = e.Tags.ToArray();
            // frontmatter name is PREPENDED, so it must be at index 0
            Assert.Equal("opencode-agent", tags[tags.Length - 1]);
            Assert.StartsWith("helper-", tags[0]);
        });
    }

    [Fact]
    public void ImportKnowledge_PerProjectCommandFiles_GoToColdWithCommandTag()
    {
        using var f = NewFixture(
            withGlobalAgentsMd: false,
            withDb: true,
            projectCount: 1,
            withCommandDir: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported);
        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        var e = Assert.Single(cold);
        Assert.Contains("opencode-command", e.Tags);
        // No frontmatter on the command files used in the fixture
        Assert.DoesNotContain(e.Tags, t => t.StartsWith("helper-"));
    }

    [Fact]
    public void ImportKnowledge_PerProjectAgentsMd_UsesProjectTag()
    {
        using var f = NewFixture(
            withGlobalAgentsMd: false,
            withDb: true,
            projectCount: 1,
            withProjectAgentsMd: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported);
        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        var e = Assert.Single(cold);
        Assert.Contains("agents-md", e.Tags);
        Assert.Contains("project", e.Tags);
        Assert.DoesNotContain("global", e.Tags);
    }

    [Fact]
    public void ImportKnowledge_DbMissing_ProjectContentSkipped()
    {
        using var f = NewFixture(withGlobalAgentsMd: true, withDb: false);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported); // only global
    }

    [Fact]
    public void ImportKnowledge_DuplicateContent_Skipped()
    {
        using var f = NewFixture(
            withGlobalAgentsMd: true,
            withDb: true,
            projectCount: 1,
            withAgentDir: true);
        var imp = NewImporter(f);
        var first = imp.ImportKnowledge();
        Assert.Equal(2, first.Imported);

        var second = imp.ImportKnowledge();
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.Skipped);
        Assert.Empty(second.Errors);
    }

    [Fact]
    public void ImportKnowledge_LogImport_RecordsAllRows()
    {
        using var f = NewFixture(
            withGlobalAgentsMd: true,
            withDb: true,
            projectCount: 1,
            withAgentDir: true,
            withProjectAgentsMd: true);
        NewImporter(f).ImportKnowledge();

        using var cmd = f.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM import_log WHERE source_tool = 'opencode'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(3L, count);
    }

    [Fact]
    public void ImportKnowledge_EmbedderThrowsForOneFile_OtherFilesStillImported()
    {
        // 1 global + 2 agent files; throw on 2nd embed call
        using var f = NewFixture(
            withGlobalAgentsMd: true,
            withDb: true,
            projectCount: 2,
            withAgentDir: true,
            embedder: new ThrowingEmbedder(throwOnCall: 2));
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(2, r.Imported);
        Assert.Single(r.Errors);
        Assert.Contains("synthetic embed failure", r.Errors[0]);
    }
}
