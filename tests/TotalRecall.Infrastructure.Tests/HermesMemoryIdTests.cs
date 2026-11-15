using System;
using System.Collections.Generic;
using System.IO;
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
/// Git sync milestone 0a: the Hermes memory-section import uses hash-derived
/// ids and the shared repair-on-retry helper. No Integration trait.
/// </summary>
public sealed class HermesMemoryIdTests : IDisposable
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

    private Fixture NewFixture(string? memoryMd = null, string? userMd = null, string? soulMd = null)
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"tr-hermesid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(basePath, "memories"));
        _tempDirs.Add(basePath);
        if (memoryMd is not null) File.WriteAllText(Path.Combine(basePath, "memories", "MEMORY.md"), memoryMd);
        if (userMd is not null) File.WriteAllText(Path.Combine(basePath, "memories", "USER.md"), userMd);
        if (soulMd is not null) File.WriteAllText(Path.Combine(basePath, "SOUL.md"), soulMd);

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

    private static HermesImporter NewImporter(Fixture f, IEmbedder? embedder = null) =>
        new(f.Store, embedder ?? new FakeEmbedder(), f.Vec, f.Log, f.BasePath);

    private static long Scalar(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<string> Ids(MsSqliteConnection conn, string table)
    {
        var ids = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM {table} ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    private static string DerivedId(string piece) =>
        ImportIds.FromHash(ImportLog.ContentHash(piece));

    [Fact]
    public void TwoFreshStores_SamePieces_SameIds()
    {
        const string memory = "First piece.\n§\nSecond piece.";
        using var a = NewFixture(memory);
        using var b = NewFixture(memory);

        NewImporter(a).ImportMemories();
        NewImporter(b).ImportMemories();

        var ids = Ids(a.Conn, "warm_memories");
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids, Ids(b.Conn, "warm_memories"));
        Assert.Contains(DerivedId("First piece."), ids);
        Assert.Contains(DerivedId("Second piece."), ids);
    }

    [Fact]
    public void SameTextInMemoryAndUser_IsOneMemory_AndDifferentTextHasDifferentId()
    {
        using var f = NewFixture(
            memoryMd: "Shared paragraph.\n§\nOnly in memory.",
            userMd: "Shared paragraph.\n§\nOnly in user.");

        var r = NewImporter(f).ImportMemories();

        // Global hash dedupe: the repeated paragraph is skipped the second time.
        Assert.Equal(3, Scalar(f.Conn, "SELECT count(*) FROM warm_memories"));
        Assert.Equal(1, r.Skipped);
        Assert.Equal(3, new HashSet<string>(Ids(f.Conn, "warm_memories")).Count);
    }

    [Fact]
    public void HalfImportedPiece_IsRepaired_AndRestImported()
    {
        using var f = NewFixture("Piece one.\n§\nPiece two.");
        // A kill after the insert of piece one: entry present, no vector, no log row.
        f.Store.Insert(Tier.Warm, ContentType.Memory,
            new InsertEntryOpts("Piece one.", Id: DerivedId("Piece one.")));

        var r = NewImporter(f).ImportMemories();

        Assert.Equal(2, r.Imported);
        Assert.Empty(r.Errors);
        Assert.Equal(2, Scalar(f.Conn, "SELECT count(*) FROM warm_memories"));
        Assert.Equal(2, Scalar(f.Conn, "SELECT count(*) FROM warm_memories_vec"));
        Assert.Equal(2, Scalar(f.Conn, "SELECT count(*) FROM import_log"));
    }

    [Fact]
    public void EmbedFailureThenRerun_NoDuplicates()
    {
        using var f = NewFixture("Piece one.\n§\nPiece two.");

        // First run: the embedder fails on the first call, after piece one was inserted.
        var first = NewImporter(f, new ThrowingEmbedder(throwOnCall: 1)).ImportMemories();
        Assert.NotEmpty(first.Errors);
        // The half-imported state: piece one inserted, no vector, no log row.
        Assert.Equal(1, Scalar(f.Conn, "SELECT count(*) FROM warm_memories"));
        Assert.Equal(0, Scalar(f.Conn, "SELECT count(*) FROM warm_memories_vec"));
        Assert.Equal(0, Scalar(f.Conn, "SELECT count(*) FROM import_log"));

        var second = NewImporter(f).ImportMemories();

        Assert.Empty(second.Errors);
        Assert.Equal(2, second.Imported);
        Assert.Equal(2, Scalar(f.Conn, "SELECT count(*) FROM warm_memories"));
        Assert.Equal(2, Scalar(f.Conn, "SELECT count(*) FROM warm_memories_vec"));
        Assert.Equal(2, Scalar(f.Conn, "SELECT count(*) FROM import_log"));
    }

    [Fact]
    public void HermesKnowledge_KeepsRandomId()
    {
        const string soul = "I am the soul file.";
        using var f = NewFixture(soulMd: soul);

        NewImporter(f).ImportKnowledge();

        var id = Ids(f.Conn, "warm_knowledge");
        Assert.Single(id);
        Assert.NotEqual(DerivedId(soul), id[0]);
    }
}
