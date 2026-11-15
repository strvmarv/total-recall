using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using NSubstitute;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Git sync milestone 0a: memory imports get hash-derived ids and an
/// interrupted import is repaired by the next run. No Integration trait: CI
/// runs these.
/// </summary>
public sealed class ImportRepairTests : IDisposable
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

    private string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tr-repair-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class Fixture : IDisposable
    {
        public required MsSqliteConnection Conn { get; init; }
        public required SqliteStore Store { get; init; }
        public required VectorSearch Vec { get; init; }
        public required ImportLog Log { get; init; }
        public FakeEmbedder Embedder { get; } = new();
        public void Dispose() => Conn.Dispose();
    }

    private static Fixture NewFixture()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return new Fixture
        {
            Conn = conn,
            Store = new SqliteStore(conn),
            Vec = new VectorSearch(conn),
            Log = new ImportLog(conn),
        };
    }

    private static long Scalar(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string ScalarText(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar())!;
    }

    private static string VecHex(MsSqliteConnection conn, string vecTable)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT embedding FROM {vecTable}";
        return Convert.ToHexString((byte[])cmd.ExecuteScalar()!);
    }

    private static InsertEntryOpts Opts(string content) =>
        new(content, SourceTool: SourceTool.ClaudeCode, EntryType: EntryType.Imported);

    private static MemoryImportResult Call(
        Fixture f, string hash, Tier tier, string content, IStore? store = null) =>
        ImportUtils.InsertOrRepairMemory(
            store ?? f.Store, f.Embedder, f.Vec, f.Log,
            "claude-code", "/p/a.md", hash, tier, Opts(content));

    private static void AssertOneEach(MsSqliteConnection conn, string table, string vecTable)
    {
        Assert.Equal(1, Scalar(conn, $"SELECT count(*) FROM {table}"));
        Assert.Equal(1, Scalar(conn, $"SELECT count(*) FROM {vecTable}"));
        Assert.Equal(1, Scalar(conn, "SELECT count(*) FROM import_log"));
    }

    // ---- InsertOrRepairMemory ----

    [Fact]
    public void New_InsertsEmbedsAndLogs()
    {
        using var f = NewFixture();
        var hash = ImportLog.ContentHash("content");

        var r = Call(f, hash, Tier.Warm, "content");

        Assert.Equal(ImportIds.FromHash(hash), r.EntryId);
        Assert.Equal(Tier.Warm, r.Tier);
        Assert.False(r.Repaired);
        AssertOneEach(f.Conn, "warm_memories", "warm_memories_vec");
        Assert.True(f.Log.IsAlreadyImported(hash));
    }

    [Fact]
    public void Repair_EntryOnly()
    {
        using var f = NewFixture();
        var hash = ImportLog.ContentHash("content");
        f.Store.Insert(Tier.Warm, ContentType.Memory,
            new InsertEntryOpts("content", Id: ImportIds.FromHash(hash)));

        var r = Call(f, hash, Tier.Warm, "content");

        Assert.True(r.Repaired);
        AssertOneEach(f.Conn, "warm_memories", "warm_memories_vec");
    }

    [Fact]
    public void Repair_EntryAndVectorNoLog()
    {
        using var f = NewFixture();
        var hash = ImportLog.ContentHash("content");
        var id = ImportIds.FromHash(hash);
        f.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts("content", Id: id));
        f.Vec.InsertEmbedding(Tier.Warm, ContentType.Memory, id, f.Embedder.Embed("content"));

        var r = Call(f, hash, Tier.Warm, "content");

        Assert.True(r.Repaired);
        AssertOneEach(f.Conn, "warm_memories", "warm_memories_vec");
    }

    [Fact]
    public void Repair_FullyImported_Idempotent()
    {
        using var f = NewFixture();
        var hash = ImportLog.ContentHash("content");

        Call(f, hash, Tier.Warm, "content");
        var second = Call(f, hash, Tier.Warm, "content");

        Assert.True(second.Repaired);
        AssertOneEach(f.Conn, "warm_memories", "warm_memories_vec");
    }

    [Fact]
    public void Repair_EntryMovedToOtherTier_UsesItsCurrentTier()
    {
        using var f = NewFixture();
        var hash = ImportLog.ContentHash("content");
        f.Store.Insert(Tier.Hot, ContentType.Memory,
            new InsertEntryOpts("content", Id: ImportIds.FromHash(hash)));

        var r = Call(f, hash, Tier.Warm, "content");

        Assert.Equal(Tier.Hot, r.Tier);
        Assert.Equal(0, Scalar(f.Conn, "SELECT count(*) FROM warm_memories"));
        Assert.Equal(1, Scalar(f.Conn, "SELECT count(*) FROM hot_memories_vec"));
        Assert.Equal("hot", ScalarText(f.Conn, "SELECT target_tier FROM import_log"));
    }

    [Fact]
    public void Repair_EmbedsTheStoredContent_NotTheImportedText()
    {
        using var f = NewFixture();
        var hash = ImportLog.ContentHash("imported text");
        const string stored = "stored text that has been edited and is a lot longer";
        f.Store.Insert(Tier.Warm, ContentType.Memory,
            new InsertEntryOpts(stored, Id: ImportIds.FromHash(hash)));

        Call(f, hash, Tier.Warm, "imported text");

        Assert.Equal(
            StoreSnapshot.Hex(f.Embedder.Embed(stored)),
            VecHex(f.Conn, "warm_memories_vec"));
    }

    // ---- the concurrent-insert race ----

    private static Entry MakeEntry(string id, string content) =>
        new(
            id, content,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            0L, 0L, 0L, 0, 1.0,
            FSharpOption<string>.None, FSharpOption<string>.None, "", EntryType.Preference, "{}", 0);

    [Fact]
    public void Race_InsertThrowsButEntryAppears_CarriesOn()
    {
        var hash = ImportLog.ContentHash("content");
        var id = ImportIds.FromHash(hash);
        var store = Substitute.For<IStore>();
        var vec = Substitute.For<IVectorSearch>();
        var log = Substitute.For<IImportLog>();
        // Target tier Warm: the lookups before the insert (Warm, Hot, Cold)
        // find nothing; the insert throws; the second lookup (Warm first)
        // finds the entry another importer inserted.
        store.Get(Tier.Warm, ContentType.Memory, id)
            .Returns((Entry?)null, MakeEntry(id, "stored"));
        store.Get(Tier.Hot, ContentType.Memory, id).Returns((Entry?)null);
        store.Get(Tier.Cold, ContentType.Memory, id).Returns((Entry?)null);
        store.Insert(Tier.Warm, ContentType.Memory, Arg.Any<InsertEntryOpts>())
            .Returns(_ => throw new InvalidOperationException("primary key"));

        var r = ImportUtils.InsertOrRepairMemory(
            store, new FakeEmbedder(), vec, log, "claude-code", "/p/a.md", hash,
            Tier.Warm, Opts("content"));

        Assert.True(r.Repaired);
        Assert.Equal(id, r.EntryId);
        store.Received(2).Get(Tier.Warm, ContentType.Memory, id);
        store.Received(1).Get(Tier.Hot, ContentType.Memory, id);
        store.Received(1).Get(Tier.Cold, ContentType.Memory, id);
        vec.Received(1).InsertEmbedding(Tier.Warm, ContentType.Memory, id,
            Arg.Any<ReadOnlyMemory<float>>());
        log.Received(1).LogImport("claude-code", "/p/a.md", hash, id, Tier.Warm, ContentType.Memory);
    }

    [Fact]
    public void Race_InsertThrowsAndNoEntry_Rethrows()
    {
        var hash = ImportLog.ContentHash("content");
        var id = ImportIds.FromHash(hash);
        var store = Substitute.For<IStore>();
        store.Get(Arg.Any<Tier>(), ContentType.Memory, id).Returns((Entry?)null);
        store.Insert(Tier.Warm, ContentType.Memory, Arg.Any<InsertEntryOpts>())
            .Returns(_ => throw new InvalidOperationException("boom"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ImportUtils.InsertOrRepairMemory(
                store, new FakeEmbedder(), Substitute.For<IVectorSearch>(),
                Substitute.For<IImportLog>(), "claude-code", "/p/a.md", hash,
                Tier.Warm, Opts("content")));
        Assert.Equal("boom", ex.Message);
    }

    // ---- ImportMarkdownFile ----

    private string WriteFile(string dir, string name, string text)
    {
        var path = Path.Combine(dir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void ImportMarkdownFile_Memory_UsesDerivedId_AndRerunRepairs()
    {
        using var f = NewFixture();
        const string raw = "---\nname: note\n---\nA memory body.";
        var path = WriteFile(NewTempDir("md"), "note.md", raw);
        var hash = ImportLog.ContentHash(raw);

        var first = ImportUtils.ImportMarkdownFile(
            f.Store, f.Embedder, f.Vec, f.Log, "claude-code", SourceTool.ClaudeCode,
            path, Tier.Warm, ContentType.Memory, Array.Empty<string>(), true);

        Assert.Equal(ImportFileStatus.Imported, first.Status);
        Assert.Equal(ImportIds.FromHash(hash),
            ScalarText(f.Conn, "SELECT id FROM warm_memories"));

        // Simulate a kill after the insert: no vector row, no log row.
        using (var cmd = f.Conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM warm_memories_vec; DELETE FROM import_log;";
            cmd.ExecuteNonQuery();
        }

        var second = ImportUtils.ImportMarkdownFile(
            f.Store, f.Embedder, f.Vec, f.Log, "claude-code", SourceTool.ClaudeCode,
            path, Tier.Warm, ContentType.Memory, Array.Empty<string>(), true);

        Assert.Equal(ImportFileStatus.Imported, second.Status);
        AssertOneEach(f.Conn, "warm_memories", "warm_memories_vec");
    }

    [Fact]
    public void ImportMarkdownFile_Knowledge_KeepsRandomId()
    {
        using var a = NewFixture();
        using var b = NewFixture();
        const string raw = "Some knowledge.";
        var path = WriteFile(NewTempDir("md"), "k.md", raw);

        foreach (var f in new[] { a, b })
        {
            ImportUtils.ImportMarkdownFile(
                f.Store, f.Embedder, f.Vec, f.Log, "claude-code", SourceTool.ClaudeCode,
                path, Tier.Warm, ContentType.Knowledge, Array.Empty<string>(), false,
                parseFrontmatter: false);
        }

        var idA = ScalarText(a.Conn, "SELECT id FROM warm_knowledge");
        var idB = ScalarText(b.Conn, "SELECT id FROM warm_knowledge");
        Assert.NotEqual(ImportIds.FromHash(ImportLog.ContentHash(raw)), idA);
        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public void Legacy_RandomIdWithLogRow_IsSkipped_AndKeepsItsId()
    {
        using var f = NewFixture();
        const string raw = "Legacy memory.";
        var path = WriteFile(NewTempDir("md"), "legacy.md", raw);
        var legacyId = f.Store.Insert(Tier.Warm, ContentType.Memory,
            new InsertEntryOpts(raw, Id: Guid.NewGuid().ToString()));
        f.Log.LogImport("claude-code", path, ImportLog.ContentHash(raw), legacyId,
            Tier.Warm, ContentType.Memory);

        var outcome = ImportUtils.ImportMarkdownFile(
            f.Store, f.Embedder, f.Vec, f.Log, "claude-code", SourceTool.ClaudeCode,
            path, Tier.Warm, ContentType.Memory, Array.Empty<string>(), false,
            parseFrontmatter: false);

        Assert.Equal(ImportFileStatus.Skipped, outcome.Status);
        Assert.Equal(1, Scalar(f.Conn, "SELECT count(*) FROM warm_memories"));
        Assert.Equal(legacyId, ScalarText(f.Conn, "SELECT id FROM warm_memories"));
    }

    // ---- the real importers ----

    [Fact]
    public void ClaudeCode_MemoryFile_DerivedId_ReferenceFile_RandomId()
    {
        using var f = NewFixture();
        var basePath = NewTempDir("claude");
        var memoryDir = Path.Combine(basePath, "projects", "-home-user-myproj", "memory");
        const string memoryRaw =
            "---\nname: user role\ndescription: senior engineer\ntype: user\n---\nUser is a senior Go engineer.";
        const string referenceRaw =
            "---\nname: architecture\ndescription: design\ntype: reference\n---\nArchitecture notes.";
        WriteFile(memoryDir, "user_role.md", memoryRaw);
        WriteFile(memoryDir, "architecture.md", referenceRaw);

        var importer = new ClaudeCodeImporter(f.Store, f.Embedder, f.Vec, f.Log, basePath);
        importer.ImportMemories(project: "myproj");

        Assert.Equal(
            ImportIds.FromHash(ImportLog.ContentHash(memoryRaw)),
            ScalarText(f.Conn, "SELECT id FROM warm_memories"));
        Assert.NotEqual(
            ImportIds.FromHash(ImportLog.ContentHash(referenceRaw)),
            ScalarText(f.Conn, "SELECT id FROM cold_knowledge"));
    }

    private static void AssertNoDerivedIds(MsSqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT target_entry_id, content_hash FROM import_log";
        using var reader = cmd.ExecuteReader();
        var rows = 0;
        while (reader.Read())
        {
            rows++;
            Assert.NotEqual(ImportIds.FromHash(reader.GetString(1)), reader.GetString(0));
        }
        Assert.True(rows > 0, "the importer imported nothing, so the test proves nothing");
    }

    [Fact]
    public void OtherKnowledgeImporters_KeepRandomIds_Copilot()
    {
        using var f = NewFixture();
        var basePath = NewTempDir("copilot");
        WriteFile(Path.Combine(basePath, "session-state", "s1"), "plan.md",
            "---\nname: session-one\n---\nPlan for session one.");

        new CopilotCliImporter(f.Store, f.Embedder, f.Vec, f.Log, basePath).ImportKnowledge();

        AssertNoDerivedIds(f.Conn);
    }

    [Fact]
    public void OtherKnowledgeImporters_KeepRandomIds_OpenCode()
    {
        using var f = NewFixture();
        var dataPath = NewTempDir("ocdata");
        var configPath = NewTempDir("occonfig");
        WriteFile(configPath, "AGENTS.md", "---\nname: global-agents\n---\n# Guidelines\nBe helpful.");

        new OpenCodeImporter(f.Store, f.Embedder, f.Vec, f.Log, dataPath, configPath).ImportKnowledge();

        AssertNoDerivedIds(f.Conn);
    }

    [Fact]
    public void OtherKnowledgeImporters_KeepRandomIds_Cline()
    {
        using var f = NewFixture();
        var dataPath = NewTempDir("clinedata");
        var legacyPath = NewTempDir("clinelegacy");
        var rulesPath = NewTempDir("clinerules");
        var rulesFallback = NewTempDir("clinerulesfb");
        WriteFile(rulesPath, "rule-a.md", "Rule A: be brief.");
        WriteFile(Path.Combine(dataPath, "state"), "taskHistory.json",
            "[ { \"id\": \"task-1\", \"task\": \"Refactor module X\", \"ts\": 1700000000000 } ]");

        new ClineImporter(f.Store, f.Embedder, f.Vec, f.Log,
            dataPath, legacyPath, rulesPath, rulesFallback).ImportKnowledge();

        AssertNoDerivedIds(f.Conn);
    }

    [Fact]
    public void OtherKnowledgeImporters_KeepRandomIds_Cursor()
    {
        using var f = NewFixture();
        var configPath = NewTempDir("cursorcfg");
        var extensionPath = NewTempDir("cursorext");
        var dbDir = Path.Combine(configPath, "User", "globalStorage");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "state.vscdb");
        using (var seed = new MsSqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            seed.Open();
            using var create = seed.CreateCommand();
            create.CommandText =
                "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT);" +
                "INSERT INTO ItemTable (key, value) VALUES ('aicontext.personalContext', 'Global Cursor rules: be concise.');";
            create.ExecuteNonQuery();
        }

        new CursorImporter(f.Store, f.Embedder, f.Vec, f.Log, configPath, extensionPath).ImportKnowledge();

        AssertNoDerivedIds(f.Conn);
    }

    // ---- cortex mode: RoutingStore ----

    [Fact]
    public void RoutingStore_NewImportEnqueuesOneUpsert_RepairEnqueuesNone()
    {
        using var f = NewFixture();
        var queue = new SyncQueue(f.Conn);
        var routing = new RoutingStore(f.Store, Substitute.For<IRemoteBackend>(), queue);

        var hash1 = ImportLog.ContentHash("one");
        Call(f, hash1, Tier.Warm, "one", store: routing);
        var queued = queue.Drain(10);
        Assert.Single(queued);
        Assert.Equal("upsert", queued[0].Operation);
        queue.MarkCompleted(queued[0].Id);

        // An entry inserted through the local store only (as after a kill
        // between the local insert and the enqueue) is repaired without a
        // second upsert being enqueued.
        var hash2 = ImportLog.ContentHash("two");
        f.Store.Insert(Tier.Warm, ContentType.Memory,
            new InsertEntryOpts("two", Id: ImportIds.FromHash(hash2)));
        var repaired = Call(f, hash2, Tier.Warm, "two", store: routing);

        Assert.True(repaired.Repaired);
        Assert.Empty(queue.Drain(10));
    }
}
