using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Migration;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Migration;

public sealed class TsDataMigratorTests : IDisposable
{
    private readonly string _tempDir;

    public TsDataMigratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            MsSqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    // --- helpers -----------------------------------------------------------

    private string NewTempDbPath(string label) =>
        Path.Combine(_tempDir, $"{label}-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Build a fresh source DB that stands in for a "TS-format" database.
    /// Since the TS and .NET schemas are byte-identical, we can use the .NET
    /// MigrationRunner to create it, then seed rows via SqliteStore and
    /// raw SQL for telemetry.
    /// </summary>
    private static void SeedSourceDb(
        string path,
        IEmbedder embedder,
        IReadOnlyList<(Tier Tier, ContentType Type, string Content)> entries,
        bool seedTelemetry)
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(path);
        MigrationRunner.RunMigrations(conn);

        var store = new SqliteStore(conn);
        var vec = new Search.VectorSearch(conn);

        foreach (var (tier, type, content) in entries)
        {
            var id = store.Insert(tier, type,
                new InsertEntryOpts(Content: content, Project: "test"));
            var e = embedder.Embed(content);
            vec.InsertEmbedding(tier, type, id, e);
        }

        if (seedTelemetry)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO retrieval_events
  (id, timestamp, session_id, query_text, query_source, query_embedding,
   results, result_count, top_score, config_snapshot_id, tiers_searched)
VALUES
  ('re1', 1000, 's1', 'q', 'mcp', NULL, '[]', 0, 0.9, 'cfg1', '[""hot""]'),
  ('re2', 2000, 's2', 'q2', 'mcp', NULL, '[]', 0, 0.5, 'cfg1', '[""warm""]')";
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO compaction_log
  (id, timestamp, source_tier, reason, config_snapshot_id)
VALUES
  ('c1', 3000, 'hot', 'decay', 'cfg1')";
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO import_log
  (id, timestamp, source_tool, source_path, content_hash,
   target_entry_id, target_tier, target_type)
VALUES
  ('il1', 4000, 'cursor', '/tmp/x.md', 'hash1', 'e1', 'cold', 'knowledge')";
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO benchmark_candidates
  (id, query_text, top_score, first_seen, last_seen)
VALUES
  ('bc1', 'what is x', 0.91, 1000, 2000)";
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static int CountRows(MsSqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // --- tests -------------------------------------------------------------

    [Fact]
    public async Task MissingSource_ReturnsFailure()
    {
        var migrator = new TsDataMigrator(new FakeEmbedder());
        var result = await migrator.MigrateAsync(
            Path.Combine(_tempDir, "nope.db"),
            Path.Combine(_tempDir, "target.db"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("source", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TargetAlreadyExists_ReturnsFailure()
    {
        var source = NewTempDbPath("src");
        var target = NewTempDbPath("tgt");
        SeedSourceDb(source, new FakeEmbedder(),
            Array.Empty<(Tier, ContentType, string)>(), seedTelemetry: false);
        File.WriteAllText(target, "occupied");

        var migrator = new TsDataMigrator(new FakeEmbedder());
        var result = await migrator.MigrateAsync(source, target, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("target", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptySource_SucceedsWithZeroEntries()
    {
        var source = NewTempDbPath("src");
        var target = NewTempDbPath("tgt");
        SeedSourceDb(source, new FakeEmbedder(),
            Array.Empty<(Tier, ContentType, string)>(), seedTelemetry: false);
        MsSqliteConnection.ClearAllPools();

        var migrator = new TsDataMigrator(new FakeEmbedder());
        var result = await migrator.MigrateAsync(source, target, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.EntriesMigrated);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task HappyPath_CopiesContentAndRebuildsVec()
    {
        var source = NewTempDbPath("src");
        var target = NewTempDbPath("tgt");
        var embedder = new FakeEmbedder();
        var entries = new List<(Tier, ContentType, string)>
        {
            (Tier.Hot,  ContentType.Memory,    "alpha memory entry"),
            (Tier.Hot,  ContentType.Memory,    "beta memory entry longer"),
            (Tier.Warm, ContentType.Memory,    "warm tier memory"),
            (Tier.Cold, ContentType.Knowledge, "cold knowledge fact"),
        };
        SeedSourceDb(source, embedder, entries, seedTelemetry: false);
        MsSqliteConnection.ClearAllPools();

        var migrator = new TsDataMigrator(embedder);
        var result = await migrator.MigrateAsync(source, target, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(4, result.EntriesMigrated);

        using var tconn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(target);
        Assert.Equal(2, CountRows(tconn, "hot_memories"));
        Assert.Equal(1, CountRows(tconn, "warm_memories"));
        Assert.Equal(0, CountRows(tconn, "cold_memories"));
        Assert.Equal(0, CountRows(tconn, "hot_knowledge"));
        Assert.Equal(0, CountRows(tconn, "warm_knowledge"));
        Assert.Equal(1, CountRows(tconn, "cold_knowledge"));

        Assert.Equal(2, CountRows(tconn, "hot_memories_vec"));
        Assert.Equal(1, CountRows(tconn, "warm_memories_vec"));
        Assert.Equal(1, CountRows(tconn, "cold_knowledge_vec"));

        // Content, metadata, project preserved verbatim.
        using (var cmd = tconn.CreateCommand())
        {
            cmd.CommandText = "SELECT content, project FROM hot_memories ORDER BY content";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("alpha memory entry", reader.GetString(0));
            Assert.Equal("test", reader.GetString(1));
            Assert.True(reader.Read());
            Assert.Equal("beta memory entry longer", reader.GetString(0));
        }
    }

    [Fact]
    public async Task TelemetryTables_CopiedVerbatim()
    {
        var source = NewTempDbPath("src");
        var target = NewTempDbPath("tgt");
        var embedder = new FakeEmbedder();
        var entries = new List<(Tier, ContentType, string)>
        {
            (Tier.Hot, ContentType.Memory, "hi"),
        };
        SeedSourceDb(source, embedder, entries, seedTelemetry: true);
        MsSqliteConnection.ClearAllPools();

        var migrator = new TsDataMigrator(embedder);
        var result = await migrator.MigrateAsync(source, target, CancellationToken.None);
        Assert.True(result.Success, result.ErrorMessage);

        using var tconn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(target);
        Assert.Equal(2, CountRows(tconn, "retrieval_events"));
        Assert.Equal(1, CountRows(tconn, "compaction_log"));
        Assert.Equal(1, CountRows(tconn, "import_log"));
        Assert.Equal(1, CountRows(tconn, "benchmark_candidates"));

        // Spot-check one retrieval_events row.
        using var cmd = tconn.CreateCommand();
        cmd.CommandText = "SELECT query_text, top_score FROM retrieval_events WHERE id='re1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("q", reader.GetString(0));
        Assert.Equal(0.9, reader.GetDouble(1), precision: 5);
    }

    /// <summary>
    /// Integration: use the real <see cref="OnnxEmbedder"/> and confirm a
    /// small migration produces non-zero, normalized vectors.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Integration_RealEmbedder_ProducesNonZeroVectors()
    {
        var repoRoot = FindRepoRoot();
        var bundledModelsDir = Path.Combine(repoRoot, "models");
        var registry = ModelRegistry.LoadFromFile(Path.Combine(bundledModelsDir, "registry.json"));
        var userDir = Path.Combine(_tempDir, "user-models");
        Directory.CreateDirectory(userDir);
        var manager = new ModelManager(registry, bundledModelsDir, userDir);
        using var realEmbedder = new OnnxEmbedder(manager, "all-MiniLM-L6-v2");

        // Seed with a fake embedder (fast) — the re-embedding pass happens
        // during migration using the real embedder.
        var source = NewTempDbPath("src");
        var target = NewTempDbPath("tgt");
        var seedEmbedder = new FakeEmbedder();
        SeedSourceDb(source, seedEmbedder,
            new[]
            {
                (Tier.Hot, ContentType.Memory, "the quick brown fox"),
                (Tier.Hot, ContentType.Memory, "sqlite-vec is fast"),
                (Tier.Cold, ContentType.Knowledge, "knowledge base entry"),
            },
            seedTelemetry: false);
        MsSqliteConnection.ClearAllPools();

        var migrator = new TsDataMigrator(realEmbedder);
        var result = await migrator.MigrateAsync(source, target, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.EntriesMigrated);

        using var tconn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(target);
        Assert.Equal(2, CountRows(tconn, "hot_memories_vec"));
        Assert.Equal(1, CountRows(tconn, "cold_knowledge_vec"));

        // vec0 embeddings are 1536 bytes (384 floats * 4) — confirm one row
        // has a plausible payload.
        using var cmd = tconn.CreateCommand();
        cmd.CommandText = "SELECT embedding FROM hot_memories_vec LIMIT 1";
        var blob = (byte[])cmd.ExecuteScalar()!;
        Assert.Equal(384 * 4, blob.Length);
        // And not all-zero.
        Assert.Contains(blob, b => b != 0);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "models", "registry.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repository root from " + AppContext.BaseDirectory);
    }
}
