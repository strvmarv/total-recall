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
public sealed class ClineImporterTests : IDisposable
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
        public required string LegacyPath { get; init; }
        public required string GlobalRulesPath { get; init; }
        public required string GlobalRulesFallback { get; init; }

        public void Dispose() => Conn.Dispose();
    }

    private string NewTempDir(string tag)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tr-cl-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return root;
    }

    private Fixture NewFixture(
        bool withData = true,
        bool withLegacy = false,
        bool withState = true,
        string? historyJson = null,
        bool withMcpSettings = false,
        bool withGlobalRules = false,
        bool withFallbackRules = false,
        IEmbedder? embedder = null)
    {
        var dataPath = NewTempDir("data");
        var legacyPath = NewTempDir("legacy");
        var globalRulesPath = NewTempDir("rules");
        var globalRulesFallback = NewTempDir("rulesfb");

        if (!withData) { Directory.Delete(dataPath, true); _tempDirs.Remove(dataPath); }
        if (!withLegacy) { Directory.Delete(legacyPath, true); _tempDirs.Remove(legacyPath); }
        if (!withGlobalRules) { Directory.Delete(globalRulesPath, true); _tempDirs.Remove(globalRulesPath); }
        if (!withFallbackRules) { Directory.Delete(globalRulesFallback, true); _tempDirs.Remove(globalRulesFallback); }

        if (withGlobalRules)
        {
            File.WriteAllText(Path.Combine(globalRulesPath, "rule-a.md"), "Rule A: be brief.");
            File.WriteAllText(Path.Combine(globalRulesPath, "rule-b.txt"), "Rule B: cite sources.");
            File.WriteAllText(Path.Combine(globalRulesPath, "ignored.json"), "{ \"x\": 1 }");
        }
        if (withFallbackRules)
        {
            File.WriteAllText(Path.Combine(globalRulesFallback, "fallback.md"), "Fallback rule: stay calm.");
        }

        if (withData && withState)
        {
            var stateDir = Path.Combine(dataPath, "state");
            Directory.CreateDirectory(stateDir);
            if (historyJson is not null)
            {
                File.WriteAllText(Path.Combine(stateDir, "taskHistory.json"), historyJson);
            }
        }

        if (withData && withMcpSettings)
        {
            var settingsDir = Path.Combine(dataPath, "settings");
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(Path.Combine(settingsDir, "cline_mcp_settings.json"), "{}");
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
            LegacyPath = legacyPath,
            GlobalRulesPath = globalRulesPath,
            GlobalRulesFallback = globalRulesFallback,
        };
    }

    private ClineImporter NewImporter(Fixture f) =>
        new ClineImporter(
            f.Store, f.Embedder, f.Vec, f.Log,
            f.DataPath, f.LegacyPath, f.GlobalRulesPath, f.GlobalRulesFallback);

    private const string TwoTaskHistoryJson = @"[
        { ""id"": ""task-1"", ""task"": ""Refactor module X"", ""ts"": 1700000000000, ""totalCost"": 0.0123, ""modelId"": ""claude-opus-4"" },
        { ""id"": ""task-2"", ""task"": ""Investigate bug Y"" }
    ]";

    // ---------- Detect ----------

    [Fact]
    public void Detect_DataPath_ReturnsTrue()
    {
        using var f = NewFixture();
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_LegacyOnly_ReturnsTrue()
    {
        using var f = NewFixture(withData: false, withLegacy: true);
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_NoData_ReturnsFalse()
    {
        using var f = NewFixture(withData: false, withLegacy: false);
        Assert.False(NewImporter(f).Detect());
    }

    // ---------- Scan ----------

    [Fact]
    public void Scan_GlobalRulesAndHistory_Counted()
    {
        using var f = NewFixture(
            withGlobalRules: true,
            withFallbackRules: true,
            historyJson: TwoTaskHistoryJson,
            withMcpSettings: true);
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        // 2 from globalRules (md+txt) + 1 from fallback (md) + 1 mcp settings
        Assert.Equal(4, r.KnowledgeFiles);
        Assert.Equal(2, r.SessionFiles);
    }

    [Fact]
    public void Scan_NoData_ReturnsZeros()
    {
        using var f = NewFixture(withData: false, withLegacy: false);
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        Assert.Equal(0, r.KnowledgeFiles);
        Assert.Equal(0, r.SessionFiles);
    }

    [Fact]
    public void Scan_MalformedHistory_SessionsZero()
    {
        using var f = NewFixture(historyJson: "{not valid json");
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.SessionFiles);
    }

    // ---------- ImportMemories ----------

    [Fact]
    public void ImportMemories_AlwaysReturnsEmpty()
    {
        using var f = NewFixture(withGlobalRules: true);
        var r = NewImporter(f).ImportMemories();
        Assert.Equal(0, r.Imported);
        Assert.Empty(r.Errors);
    }

    // ---------- ImportKnowledge: global rules ----------

    [Fact]
    public void ImportKnowledge_GlobalRules_GoToWarmRaw()
    {
        using var f = NewFixture(withGlobalRules: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(2, r.Imported); // .md + .txt; .json ignored
        Assert.Empty(r.Errors);

        var warm = f.Store.List(Tier.Warm, ContentType.Knowledge, null);
        Assert.Equal(2, warm.Count);
        Assert.All(warm, e =>
        {
            Assert.True(e.SourceTool!.Value.IsCline);
            Assert.Contains("cline-rule", e.Tags);
            Assert.Contains("global", e.Tags);
        });
    }

    [Fact]
    public void ImportKnowledge_GlobalRules_NoFrontmatterParsing()
    {
        using var f = NewFixture(withGlobalRules: false);
        // Manually create a rule file with frontmatter — content must be raw.
        Directory.CreateDirectory(f.GlobalRulesPath);
        _tempDirs.Add(f.GlobalRulesPath);
        File.WriteAllText(
            Path.Combine(f.GlobalRulesPath, "frontmatter-rule.md"),
            "---\nname: should-not-parse\n---\nRaw body.");
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported);
        var e = Assert.Single(f.Store.List(Tier.Warm, ContentType.Knowledge, null));
        // The frontmatter is INSIDE content (raw), and the tag list does not
        // contain "should-not-parse" (which would indicate frontmatter parsing).
        Assert.StartsWith("---", e.Content);
        Assert.DoesNotContain(e.Tags, t => t == "should-not-parse");
    }

    [Fact]
    public void ImportKnowledge_FallbackGlobalRules_AlsoImported()
    {
        using var f = NewFixture(withFallbackRules: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported);
        var warm = f.Store.List(Tier.Warm, ContentType.Knowledge, null);
        var e = Assert.Single(warm);
        Assert.Equal("Fallback rule: stay calm.", e.Content);
    }

    // ---------- ImportKnowledge: task summaries ----------

    [Fact]
    public void ImportKnowledge_TaskSummaries_GoToCold()
    {
        using var f = NewFixture(historyJson: TwoTaskHistoryJson);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(2, r.Imported);
        Assert.Equal(2, f.Store.Count(Tier.Cold, ContentType.Knowledge));

        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        Assert.All(cold, e =>
        {
            Assert.True(e.SourceTool!.Value.IsCline);
            Assert.Contains("cline-task", e.Tags);
            Assert.StartsWith("cline:task:", e.Source!.Value);
        });

        // The task with metadata should contain Model + Cost + Date lines.
        var rich = cold.Single(e => e.Source!.Value == "cline:task:task-1");
        Assert.Contains("Task: Refactor module X", rich.Content);
        Assert.Contains("Model: claude-opus-4", rich.Content);
        Assert.Contains("Cost: $0.0123", rich.Content);
        Assert.Contains("Date: ", rich.Content);
        // Summary is task[:200]
        Assert.Equal("Refactor module X", rich.Summary!.Value);
    }

    [Fact]
    public void ImportKnowledge_TaskWithoutMetadata_HasOnlyTaskLine()
    {
        using var f = NewFixture(historyJson: TwoTaskHistoryJson);
        NewImporter(f).ImportKnowledge();
        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        var bare = cold.Single(e => e.Source!.Value == "cline:task:task-2");
        Assert.Equal("Task: Investigate bug Y", bare.Content);
        Assert.DoesNotContain("Model:", bare.Content);
        Assert.DoesNotContain("Cost:", bare.Content);
        Assert.DoesNotContain("Date:", bare.Content);
    }

    [Fact]
    public void ImportKnowledge_TaskHistoryLogImport_PathIsTaskId()
    {
        using var f = NewFixture(historyJson: TwoTaskHistoryJson);
        NewImporter(f).ImportKnowledge();

        using var cmd = f.Conn.CreateCommand();
        cmd.CommandText = "SELECT source_path FROM import_log WHERE source_tool = 'cline' ORDER BY source_path";
        using var reader = cmd.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        Assert.Equal(new[] { "task:task-1", "task:task-2" }, paths);
    }

    [Fact]
    public void ImportKnowledge_NoHistoryFile_NoColdEntries()
    {
        using var f = NewFixture(withState: true, historyJson: null);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(0, r.Imported);
        Assert.Equal(0, f.Store.Count(Tier.Cold, ContentType.Knowledge));
    }

    [Fact]
    public void ImportKnowledge_MalformedHistory_NoColdEntries()
    {
        using var f = NewFixture(historyJson: "{not valid json");
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(0, r.Imported);
    }

    [Fact]
    public void ImportKnowledge_TaskMissingIdOrTask_Skipped()
    {
        using var f = NewFixture(historyJson:
            "[ { \"task\": \"no-id\" }, { \"id\": \"no-task\" }, { \"id\": \"good\", \"task\": \"valid\" } ]");
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported);
    }

    [Fact]
    public void ImportKnowledge_DuplicateContent_Skipped()
    {
        using var f = NewFixture(
            withGlobalRules: true,
            historyJson: TwoTaskHistoryJson);
        var imp = NewImporter(f);
        var first = imp.ImportKnowledge();
        Assert.Equal(4, first.Imported); // 2 rules + 2 tasks

        var second = imp.ImportKnowledge();
        Assert.Equal(0, second.Imported);
        Assert.Equal(4, second.Skipped);
    }

    [Fact]
    public void ImportKnowledge_EmbedderThrowsForOneFile_OtherFilesStillImported()
    {
        using var f = NewFixture(
            withGlobalRules: true, // 2 files (md + txt)
            historyJson: TwoTaskHistoryJson, // 2 tasks
            embedder: new ThrowingEmbedder(throwOnCall: 2));
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(3, r.Imported); // 4 calls total, one fails
        Assert.Single(r.Errors);
        Assert.Contains("synthetic embed failure", r.Errors[0]);
    }
}
