using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="HermesImporter"/>. Uses a real
/// <c>:memory:</c> database with full migrations applied and a hermetic
/// temp-dir fixture per test. The embedder is a deterministic fake so the
/// tests don't depend on the ONNX runtime.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HermesImporterTests : IDisposable
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
        public required string BasePath { get; init; }

        public void Dispose() => Conn.Dispose();
    }

    private string NewTempDir(string tag)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tr-hermes-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return root;
    }

    private Fixture NewFixture()
    {
        var basePath = NewTempDir("base");
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return new Fixture
        {
            Conn = conn,
            Store = new SqliteStore(conn),
            Vec = new VectorSearch(conn),
            Log = new ImportLog(conn),
            BasePath = basePath,
        };
    }

    private HermesImporter NewImporter(Fixture f, FakeEmbedder? e = null) =>
        new(f.Store, e ?? new FakeEmbedder(), f.Vec, f.Log, f.BasePath);

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    // ---------- Detect ----------

    [Fact]
    public void Detect_BasePathExistsWithStateDb_ReturnsTrue()
    {
        using var f = NewFixture();
        File.WriteAllText(Path.Combine(f.BasePath, "state.db"), "");
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_BasePathExistsWithMemoriesDir_ReturnsTrue()
    {
        using var f = NewFixture();
        Directory.CreateDirectory(Path.Combine(f.BasePath, "memories"));
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_BasePathExistsWithConfigYaml_ReturnsTrue()
    {
        using var f = NewFixture();
        File.WriteAllText(Path.Combine(f.BasePath, "config.yaml"), "foo: bar\n");
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_BasePathExistsButNoChildren_ReturnsFalse()
    {
        using var f = NewFixture();
        Assert.False(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_BasePathDoesNotExist_ReturnsFalse()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "tr-hermes-missing-" + Guid.NewGuid().ToString("N"));
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var imp = new HermesImporter(
                new SqliteStore(conn), new FakeEmbedder(),
                new VectorSearch(conn), new ImportLog(conn), bogus);
            Assert.False(imp.Detect());
        }
    }

    // ---------- Scan ----------

    [Fact]
    public void Scan_MemoryFiles_CountsMemoryAndUserMd()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "memories", "MEMORY.md"), "a");
        WriteFile(Path.Combine(f.BasePath, "memories", "USER.md"), "b");
        var r = NewImporter(f).Scan();
        Assert.Equal(2, r.MemoryFiles);
    }

    [Fact]
    public void Scan_KnowledgeFiles_CountsSkillsAndSoul()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "SOUL.md"), "soul");
        WriteFile(Path.Combine(f.BasePath, "skills", "alpha", "SKILL.md"), "a skill");
        WriteFile(Path.Combine(f.BasePath, "skills", "beta", "SKILL.md"), "b skill");
        // Bogus skill dir without SKILL.md should be ignored.
        Directory.CreateDirectory(Path.Combine(f.BasePath, "skills", "gamma"));
        var r = NewImporter(f).Scan();
        // 2 skills + 1 SOUL = 3
        Assert.Equal(3, r.KnowledgeFiles);
    }

    [Fact]
    public void Scan_SessionFiles_OneIfStateDbExists()
    {
        using var f = NewFixture();
        File.WriteAllText(Path.Combine(f.BasePath, "state.db"), "");
        var r = NewImporter(f).Scan();
        Assert.Equal(1, r.SessionFiles);
    }

    [Fact]
    public void Scan_NothingExists_ReturnsZeros()
    {
        using var f = NewFixture();
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        Assert.Equal(0, r.KnowledgeFiles);
        Assert.Equal(0, r.SessionFiles);
    }

    // ---------- ImportMemories ----------

    [Fact]
    public void ImportMemories_MemoryMd_SplitsOnSectionDelimiter()
    {
        using var f = NewFixture();
        var memPath = Path.Combine(f.BasePath, "memories", "MEMORY.md");
        WriteFile(memPath,
            "First memory paragraph.\n§\nSecond memory paragraph.\n§\nThird memory.");

        var result = NewImporter(f).ImportMemories();
        Assert.Equal(3, result.Imported);
        Assert.Empty(result.Errors);

        var warm = f.Store.List(Tier.Warm, ContentType.Memory, null);
        Assert.Equal(3, warm.Count);
        var contents = warm.Select(e => e.Content).ToHashSet();
        Assert.Contains("First memory paragraph.", contents);
        Assert.Contains("Second memory paragraph.", contents);
        Assert.Contains("Third memory.", contents);
        foreach (var e in warm)
        {
            Assert.True(e.SourceTool!.Value.IsHermes);
            Assert.Contains("hermes-memory", e.Tags);
        }
    }

    [Fact]
    public void ImportMemories_UserMd_TaggedHermesUserAndUserProfile()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "memories", "USER.md"),
            "User prefers TDD.");

        NewImporter(f).ImportMemories();
        var warm = f.Store.List(Tier.Warm, ContentType.Memory, null);
        var e = Assert.Single(warm);
        Assert.Contains("hermes-user", e.Tags);
        Assert.Contains("user-profile", e.Tags);
    }

    [Fact]
    public void ImportMemories_DuplicatePiece_Skipped()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "memories", "MEMORY.md"),
            "Paragraph one.\n§\nParagraph two.");

        var imp = NewImporter(f);
        var first = imp.ImportMemories();
        Assert.Equal(2, first.Imported);

        var second = imp.ImportMemories();
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, f.Store.Count(Tier.Warm, ContentType.Memory));
    }

    [Fact]
    public void ImportMemories_EmptyPieces_Skipped()
    {
        using var f = NewFixture();
        // Leading blank, middle blank, trailing blank. Only "stuff" is non-empty.
        WriteFile(Path.Combine(f.BasePath, "memories", "MEMORY.md"),
            "\n§\n\n§\nstuff\n§\n");

        var result = NewImporter(f).ImportMemories();
        Assert.Equal(1, result.Imported);
        Assert.Empty(result.Errors);

        var warm = f.Store.List(Tier.Warm, ContentType.Memory, null);
        var e = Assert.Single(warm);
        Assert.Equal("stuff", e.Content);
    }

    [Fact]
    public void ImportMemories_SummaryIsFirst200Chars()
    {
        using var f = NewFixture();
        var longPiece = new string('x', 400);
        WriteFile(Path.Combine(f.BasePath, "memories", "MEMORY.md"), longPiece);

        NewImporter(f).ImportMemories();
        var e = Assert.Single(f.Store.List(Tier.Warm, ContentType.Memory, null));
        Assert.Equal(200, e.Summary!.Value.Length);
        Assert.Equal(new string('x', 200), e.Summary.Value);
    }

    [Fact]
    public void ImportMemories_NoMemoryFiles_ReturnsEmpty()
    {
        using var f = NewFixture();
        var result = NewImporter(f).ImportMemories();
        Assert.Equal(0, result.Imported);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Errors);
        Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Memory));
    }

    [Fact]
    public void ImportMemories_ProjectArg_PopulatesProjectColumn()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "memories", "MEMORY.md"), "piece one");
        NewImporter(f).ImportMemories(project: "hermes-proj");
        var e = Assert.Single(f.Store.List(Tier.Warm, ContentType.Memory, null));
        Assert.Equal("hermes-proj", e.Project!.Value);
    }

    // ---------- ImportKnowledge ----------

    [Fact]
    public void ImportKnowledge_SoulMd_ImportsToWarmKnowledge()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "SOUL.md"),
            "---\ndescription: the soul\n---\nI am Hermes.");

        var result = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, result.Imported);
        Assert.Empty(result.Errors);

        var warm = f.Store.List(Tier.Warm, ContentType.Knowledge, null);
        var e = Assert.Single(warm);
        Assert.Contains("I am Hermes.", e.Content);
        Assert.Contains("hermes-soul", e.Tags);
        Assert.True(e.SourceTool!.Value.IsHermes);
    }

    [Fact]
    public void ImportKnowledge_Skills_ImportsToColdKnowledge()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "skills", "alpha", "SKILL.md"),
            "alpha skill body");
        WriteFile(Path.Combine(f.BasePath, "skills", "beta", "SKILL.md"),
            "beta skill body");

        var result = NewImporter(f).ImportKnowledge();
        Assert.Equal(2, result.Imported);
        Assert.Empty(result.Errors);

        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        Assert.Equal(2, cold.Count);
        Assert.Contains(cold, e => e.Tags.Contains("alpha"));
        Assert.Contains(cold, e => e.Tags.Contains("beta"));
        foreach (var e in cold)
        {
            Assert.Contains("hermes-skill", e.Tags);
        }
    }

    [Fact]
    public void ImportKnowledge_SoulFrontmatterName_PrependedToTags()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "SOUL.md"),
            "---\nname: hermes_core\ndescription: d\n---\nBody.");

        NewImporter(f).ImportKnowledge();
        var e = Assert.Single(f.Store.List(Tier.Warm, ContentType.Knowledge, null));
        Assert.Equal(new[] { "hermes_core", "hermes-soul" }, e.Tags.ToArray());
    }

    [Fact]
    public void ImportKnowledge_DuplicateSoul_Skipped()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "SOUL.md"), "same body");
        var imp = NewImporter(f);
        Assert.Equal(1, imp.ImportKnowledge().Imported);
        var second = imp.ImportKnowledge();
        Assert.Equal(0, second.Imported);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, f.Store.Count(Tier.Warm, ContentType.Knowledge));
    }

    [Fact]
    public void ImportKnowledge_NoSoulOrSkills_ReturnsEmpty()
    {
        using var f = NewFixture();
        var result = NewImporter(f).ImportKnowledge();
        Assert.Equal(0, result.Imported);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ImportKnowledge_EmbedderThrowsForOneSkill_OtherSkillsStillImported()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "skills", "alpha", "SKILL.md"),
            "alpha skill body");
        WriteFile(Path.Combine(f.BasePath, "skills", "beta", "SKILL.md"),
            "beta skill body");
        WriteFile(Path.Combine(f.BasePath, "skills", "gamma", "SKILL.md"),
            "gamma skill body");

        // Throw on the 2nd embed call (second skill) — the 1st and 3rd still land.
        var emb = new ThrowingEmbedder(throwOnCall: 2);
        var imp = new HermesImporter(f.Store, emb, f.Vec, f.Log, f.BasePath);
        var result = imp.ImportKnowledge();
        // Two skills succeed fully (embed + log); the throwing one still
        // got inserted before embed blew up, so the row exists but isn't
        // logged. The importer records it as an error.
        Assert.Equal(2, result.Imported);
        Assert.Single(result.Errors);

        using var cmd = f.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM import_log WHERE source_tool = 'hermes'";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }

    // ---------- Import log rows ----------

    [Fact]
    public void ImportLog_AfterFullImport_ContainsHermesRows()
    {
        using var f = NewFixture();
        WriteFile(Path.Combine(f.BasePath, "memories", "MEMORY.md"),
            "one\n§\ntwo");
        WriteFile(Path.Combine(f.BasePath, "SOUL.md"), "soul body");
        WriteFile(Path.Combine(f.BasePath, "skills", "alpha", "SKILL.md"),
            "alpha body");

        var imp = NewImporter(f);
        imp.ImportMemories();
        imp.ImportKnowledge();

        using var cmd = f.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM import_log WHERE source_tool = 'hermes'";
        var count = (long)cmd.ExecuteScalar()!;
        // 2 memory pieces + SOUL + alpha = 4
        Assert.Equal(4L, count);
    }
}
