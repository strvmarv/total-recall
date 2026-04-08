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
/// Integration tests for <see cref="CopilotCliImporter"/>. Uses a real
/// <c>:memory:</c> database with full migrations applied and a hermetic
/// temp-dir fixture tree per test. The embedder is a deterministic fake.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CopilotCliImporterTests : IDisposable
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
        public required string BasePath { get; init; }

        public void Dispose() => Conn.Dispose();
    }

    private sealed class ThrowingEmbedder : IEmbedder
    {
        private readonly int _throwOnCall;
        private int _calls;
        public ThrowingEmbedder(int throwOnCall = 2) { _throwOnCall = throwOnCall; }
        public float[] Embed(string text)
        {
            _calls++;
            if (_calls == _throwOnCall)
                throw new InvalidOperationException("synthetic embed failure");
            var v = new float[384];
            var len = text?.Length ?? 0;
            for (var i = 0; i < 384; i++)
                v[i] = (float)Math.Sin(len * (i + 1) / 384.0);
            return v;
        }
    }

    private string NewTempDir(string tag)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"tr-co-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        return root;
    }

    /// <summary>
    /// Build a realistic Copilot CLI tree with two sessions:
    /// - session "s1": plan.md + 2 *.jsonl
    /// - session "s2": no plan.md + 1 *.jsonl
    /// </summary>
    private Fixture NewFixture(
        bool withSessionState = true,
        bool withPlanInS1 = true,
        bool withPlanInS2 = false,
        bool withJsonl = true,
        IEmbedder? embedder = null)
    {
        var basePath = NewTempDir("base");

        if (withSessionState)
        {
            var s1 = Path.Combine(basePath, "session-state", "s1");
            var s2 = Path.Combine(basePath, "session-state", "s2");
            Directory.CreateDirectory(s1);
            Directory.CreateDirectory(s2);

            if (withPlanInS1)
            {
                File.WriteAllText(
                    Path.Combine(s1, "plan.md"),
                    "---\nname: session-one\n---\nPlan for session one: refactor module X.");
            }

            if (withPlanInS2)
            {
                File.WriteAllText(
                    Path.Combine(s2, "plan.md"),
                    "Plan for session two: investigate bug Y.");
            }

            if (withJsonl)
            {
                File.WriteAllText(Path.Combine(s1, "events-1.jsonl"), "{}\n");
                File.WriteAllText(Path.Combine(s1, "events-2.jsonl"), "{}\n");
                File.WriteAllText(Path.Combine(s2, "events-1.jsonl"), "{}\n");
            }
        }

        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var store = new SqliteStore(conn);
        var vec = new VectorSearch(conn);
        var log = new ImportLog(conn);

        return new Fixture
        {
            Conn = conn,
            Store = store,
            Vec = vec,
            Log = log,
            Embedder = embedder ?? new FakeEmbedder(),
            BasePath = basePath,
        };
    }

    private CopilotCliImporter NewImporter(Fixture f) =>
        new CopilotCliImporter(f.Store, f.Embedder, f.Vec, f.Log, f.BasePath);

    // ---------- Detect ----------

    [Fact]
    public void Detect_BasePathExists_ReturnsTrue()
    {
        using var f = NewFixture();
        Assert.True(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_NoSessionStateDir_ReturnsFalse()
    {
        using var f = NewFixture(withSessionState: false);
        Assert.False(NewImporter(f).Detect());
    }

    [Fact]
    public void Detect_BasePathDoesNotExist_ReturnsFalse()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "tr-co-missing-" + Guid.NewGuid().ToString("N"));
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var imp = new CopilotCliImporter(
                new SqliteStore(conn), new FakeEmbedder(),
                new VectorSearch(conn), new ImportLog(conn), bogus);
            Assert.False(imp.Detect());
        }
    }

    // ---------- Scan ----------

    [Fact]
    public void Scan_ReportsCorrectFileCounts()
    {
        using var f = NewFixture();
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        Assert.Equal(1, r.KnowledgeFiles); // only s1 has plan.md
        Assert.Equal(3, r.SessionFiles);   // 2 in s1 + 1 in s2
    }

    [Fact]
    public void Scan_NoSessionStateDir_ReturnsZeros()
    {
        using var f = NewFixture(withSessionState: false);
        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
        Assert.Equal(0, r.KnowledgeFiles);
        Assert.Equal(0, r.SessionFiles);
    }

    [Fact]
    public void Scan_AlwaysReturnsZeroMemoryFiles()
    {
        using var f = NewFixture(withPlanInS1: true, withPlanInS2: true, withJsonl: true);
        // Add some bogus nested files to tempt the scanner.
        var weird = Path.Combine(f.BasePath, "session-state", "s1", "memory.md");
        File.WriteAllText(weird, "should not count");

        var r = NewImporter(f).Scan();
        Assert.Equal(0, r.MemoryFiles);
    }

    // ---------- ImportMemories ----------

    [Fact]
    public void ImportMemories_AlwaysReturnsEmpty()
    {
        using var f = NewFixture();
        var r = NewImporter(f).ImportMemories();
        Assert.Equal(0, r.Imported);
        Assert.Equal(0, r.Skipped);
        Assert.Empty(r.Errors);
        Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Memory));
        Assert.Equal(0, f.Store.Count(Tier.Cold, ContentType.Knowledge));
    }

    [Fact]
    public void ImportMemories_WithProjectArg_StillEmpty()
    {
        using var f = NewFixture();
        var r = NewImporter(f).ImportMemories(project: "anything");
        Assert.Equal(0, r.Imported);
        Assert.Empty(r.Errors);
        Assert.Equal(0, f.Store.Count(Tier.Warm, ContentType.Memory));
    }

    // ---------- ImportKnowledge ----------

    [Fact]
    public void ImportKnowledge_PopulatesColdKnowledge()
    {
        using var f = NewFixture(withPlanInS1: true, withPlanInS2: true);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(2, r.Imported);
        Assert.Equal(0, r.Skipped);
        Assert.Empty(r.Errors);

        Assert.Equal(2, f.Store.Count(Tier.Cold, ContentType.Knowledge));
        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        Assert.All(cold, e => Assert.True(e.SourceTool!.Value.IsCopilotCli));
        Assert.All(cold, e => Assert.EndsWith("plan.md", e.Source!.Value));
    }

    [Fact]
    public void ImportKnowledge_NoSessionState_ReturnsEmpty()
    {
        using var f = NewFixture(withSessionState: false);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(0, r.Imported);
        Assert.Equal(0, r.Skipped);
        Assert.Empty(r.Errors);
        Assert.Equal(0, f.Store.Count(Tier.Cold, ContentType.Knowledge));
    }

    [Fact]
    public void ImportKnowledge_PlanMdMissing_SessionSkipped()
    {
        using var f = NewFixture(withPlanInS1: true, withPlanInS2: false);
        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(1, r.Imported);
        Assert.Equal(1, f.Store.Count(Tier.Cold, ContentType.Knowledge));
    }

    [Fact]
    public void ImportKnowledge_DuplicateContent_Skipped()
    {
        using var f = NewFixture(withPlanInS1: true, withPlanInS2: true);
        var imp = NewImporter(f);
        var first = imp.ImportKnowledge();
        Assert.Equal(2, first.Imported);

        var second = imp.ImportKnowledge();
        Assert.Equal(0, second.Imported);
        Assert.Equal(2, second.Skipped);
        Assert.Empty(second.Errors);

        Assert.Equal(2, f.Store.Count(Tier.Cold, ContentType.Knowledge));
    }

    [Fact]
    public void ImportKnowledge_NoTags()
    {
        using var f = NewFixture(withPlanInS1: true, withPlanInS2: true);
        NewImporter(f).ImportKnowledge();
        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        Assert.All(cold, e => Assert.Empty(e.Tags));
    }

    [Fact]
    public void ImportKnowledge_NoFrontmatterStripping()
    {
        // s1 has a plan.md with a YAML frontmatter block; verify content is
        // stored verbatim — frontmatter NOT parsed or stripped.
        using var f = NewFixture(withPlanInS1: true, withPlanInS2: false);
        NewImporter(f).ImportKnowledge();
        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        var e = Assert.Single(cold);
        Assert.StartsWith("---\nname: session-one\n---", e.Content);
        Assert.Contains("Plan for session one", e.Content);
        // And no tags (no frontmatter parsing).
        Assert.Empty(e.Tags);
    }

    [Fact]
    public void ImportKnowledge_SourceTool_IsCopilotCli()
    {
        using var f = NewFixture(withPlanInS1: true);
        NewImporter(f).ImportKnowledge();
        var cold = f.Store.List(Tier.Cold, ContentType.Knowledge, null);
        var e = Assert.Single(cold);
        Assert.True(e.SourceTool!.Value.IsCopilotCli);
    }

    [Fact]
    public void ImportKnowledge_LogImport_RecordsHash()
    {
        using var f = NewFixture(withPlanInS1: true, withPlanInS2: true);
        NewImporter(f).ImportKnowledge();

        using var cmd = f.Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM import_log WHERE source_tool = 'copilot-cli'";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(2L, count);
    }

    [Fact]
    public void ImportKnowledge_EmbedderThrowsForOneFile_OtherFilesStillImported()
    {
        // Three sessions, each with plan.md; ThrowingEmbedder throws on the 2nd call.
        using var f = NewFixture(
            withPlanInS1: true,
            withPlanInS2: true,
            embedder: new ThrowingEmbedder(throwOnCall: 2));

        // Add a third session with plan.md
        var s3 = Path.Combine(f.BasePath, "session-state", "s3");
        Directory.CreateDirectory(s3);
        File.WriteAllText(
            Path.Combine(s3, "plan.md"),
            "Plan for session three: ship the thing.");

        var r = NewImporter(f).ImportKnowledge();
        Assert.Equal(2, r.Imported);
        Assert.Single(r.Errors);
        Assert.Contains("plan.md", r.Errors[0]);
        Assert.Contains("synthetic embed failure", r.Errors[0]);
        // Note: the row for the failing file IS written to the store before
        // the embed call throws — the importer does not roll back the insert.
        // What matters is that result.Imported reflects only the successful
        // end-to-end flows, and subsequent files continue after the failure.
        Assert.Equal(3, f.Store.Count(Tier.Cold, ContentType.Knowledge));
    }
}
