// src/TotalRecall.Infrastructure/Eval/IsolatedBenchmark.cs
//
// Production wiring for the retrieval benchmark. The benchmark measures
// embedder + hybrid-search quality against a KNOWN synthetic corpus, so it
// must run in an isolated, throwaway database — NEVER the user's live store.
//
// Running against the live DB (the pre-3.5.2 behavior) was a double bug:
//   1. Privacy — hybrid search fans out across the whole DB, so per-query
//      TopResult surfaced the user's real memories/KB (internal names, even
//      secret-bearing docs) in the benchmark results shown in the Web UI.
//   2. Correctness — real entries outrank the seeded corpus, so match rates
//      measured the wrong thing.
//
// A fresh temp DB is created, migrated, used, and deleted (with its WAL/SHM
// sidecars) per run. BenchmarkRunner still seeds + tears down its corpus rows,
// which is now redundant but harmless against a throwaway file.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Eval;

/// <summary>
/// Runs <see cref="BenchmarkRunner"/> against an isolated, ephemeral SQLite
/// database so the benchmark never touches — or surfaces — the live store.
/// </summary>
public static class IsolatedBenchmark
{
    /// <summary>
    /// Execute the benchmark against a fresh temp DB and discard it afterward.
    /// Matches the <c>EvalBenchmarkExecutor</c> / CLI executor delegate shape.
    /// </summary>
    public static async Task<BenchmarkResult> RunAsync(BenchmarkOptions opts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var dbPath = NewTempDbPath();
        // Pooling off so Dispose releases the file handle and the temp DB
        // (plus WAL/SHM sidecars) can be deleted immediately afterward.
        var conn = SqliteConnection.Open(dbPath, pooling: false);
        try
        {
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);
            var fts = new FtsSearch(conn);
            var hybrid = new HybridSearch(vec, fts, store);
            var embedder = EmbedderFactory.CreateProduction();
            var runner = new BenchmarkRunner(store, vec, hybrid, embedder);
            return await runner.RunAsync(opts, ct).ConfigureAwait(false);
        }
        finally
        {
            conn.Dispose();
            DeleteDbFiles(dbPath);
        }
    }

    /// <summary>A unique throwaway DB path under the system temp directory.</summary>
    internal static string NewTempDbPath()
        => Path.Combine(Path.GetTempPath(), $"tr-benchmark-{Guid.NewGuid():N}.db");

    /// <summary>Delete a SQLite db file and its <c>-wal</c>/<c>-shm</c> sidecars (best-effort).</summary>
    internal static void DeleteDbFiles(string dbPath)
    {
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
