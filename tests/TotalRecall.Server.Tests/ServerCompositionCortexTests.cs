// tests/TotalRecall.Server.Tests/ServerCompositionCortexTests.cs
//
// Task 14 — verify cortex storage mode wiring in ServerComposition.
// Plus: cortex now runs the embedder-fingerprint migration on its LOCAL vec0
// index at open (mirrors the sqlite path) — proven without a live cortex server.

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

public sealed class ServerCompositionCortexTests
{
    [Fact]
    public void OpenCortex_CreatesRoutingStore()
    {
        var handles = ServerComposition.OpenCortexForTest(
            sqliteDbPath: ":memory:",
            cortexUrl: "https://cortex.test",
            cortexPat: "tr_test123");

        Assert.NotNull(handles.Store);
        Assert.IsType<RoutingStore>(handles.Store);
    }

    /// <summary>
    /// Proves the embedder-fingerprint migration is wired into the cortex
    /// composition path (OpenCortexCore), not just sqlite/postgres. We seed a
    /// LOCAL DB that is populated but stamped with a DIFFERENT model, configure
    /// on_model_change="block", then open cortex pointing at that DB. If the
    /// migration is wired, opening throws the mismatch exception (the block path
    /// reads only the embedder Descriptor + _meta, so it needs no live cortex
    /// server and no ONNX model file). A regression that drops the migration
    /// call would open cleanly and this test would fail.
    /// </summary>
    [Fact]
    public void OpenCortex_PopulatedLocalDb_StampedDifferentModel_Block_Throws()
    {
        var home = Path.Combine(Path.GetTempPath(), $"tr-cortex-mig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        var dbPath = Path.Combine(home, "local.db");

        var prevHome = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
        var prevDbPath = Environment.GetEnvironmentVariable("TOTAL_RECALL_DB_PATH");
        try
        {
            // Force on_model_change="block" via a user config under TOTAL_RECALL_HOME.
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", home);
            Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", dbPath);
            File.WriteAllText(
                Path.Combine(home, "config.toml"),
                "[embedding]\non_model_change = \"block\"\n");

            // Seed a populated local index stamped with a DIFFERENT (prior) model.
            var oldE = new FixedDescriptorEmbedder("local", "old-model", "", 384);
            using (var conn = SqliteConnection.Open(dbPath))
            {
                MigrationRunner.RunMigrations(conn);
                var store = new SqliteStore(conn);
                store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                    new InsertEntryOpts("seed"), oldE.Embed("seed"));
                EmbedderFingerprint.Restamp(store, oldE);
            }
            MsSqliteConnection.ClearAllPools();

            // Opening cortex must run EnsureCompatibleSqlite on the local index and,
            // under block + mismatch, throw.
            Assert.Throws<EmbedderFingerprintMismatchException>(
                () => ServerComposition.OpenCortexForTest(dbPath, "https://cortex.test", "tr_test123"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", prevHome);
            Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", prevDbPath);
            MsSqliteConnection.ClearAllPools();
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>IEmbedder with a fully controllable descriptor; Embed returns a
    /// normalized constant vector so the seeded row is valid for the vec0 table.</summary>
    private sealed class FixedDescriptorEmbedder : IEmbedder
    {
        public FixedDescriptorEmbedder(string provider, string model, string revision, int dims)
            => Descriptor = new EmbedderDescriptor(provider, model, revision, dims);

        public EmbedderDescriptor Descriptor { get; }

        public float[] Embed(string text)
        {
            var a = new float[Descriptor.Dimensions];
            for (var i = 0; i < a.Length; i++) a[i] = 1.0f;
            var n = (float)Math.Sqrt(a.Length);
            for (var i = 0; i < a.Length; i++) a[i] /= n;
            return a;
        }
    }
}
