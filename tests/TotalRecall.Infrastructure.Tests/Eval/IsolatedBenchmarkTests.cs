using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

/// <summary>
/// The retrieval benchmark must run against a throwaway, isolated database —
/// NOT the user's live store. Searching the live DB both pollutes the metrics
/// and leaks real memory/KB content into the benchmark results.
/// </summary>
public sealed class IsolatedBenchmarkTests
{
    [Fact]
    public void NewTempDbPath_IsRootedUniqueTempDb_NotTheLiveDb()
    {
        var a = IsolatedBenchmark.NewTempDbPath();
        var b = IsolatedBenchmark.NewTempDbPath();

        Assert.True(Path.IsPathRooted(a));
        Assert.EndsWith(".db", a, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(a), StringComparison.Ordinal);
        Assert.NotEqual(a, b); // unique per call
        Assert.NotEqual(
            Path.GetFullPath(ConfigLoader.GetDbPath()),
            Path.GetFullPath(a)); // never the live DB
    }

    [Fact]
    public void DeleteDbFiles_RemovesDbAndWalShmSidecars()
    {
        var baseName = Path.Combine(Path.GetTempPath(), "tr-iso-test-" + Guid.NewGuid().ToString("N") + ".db");
        var paths = new[] { baseName, baseName + "-wal", baseName + "-shm" };
        foreach (var p in paths) File.WriteAllText(p, "x");

        IsolatedBenchmark.DeleteDbFiles(baseName);

        foreach (var p in paths) Assert.False(File.Exists(p), $"should be deleted: {p}");
    }

    // The smoke test at session_start reuses the session's already-loaded
    // embedder: the overload that takes one must use it, must not dispose it
    // (the session keeps using it), and must still run in a throwaway DB.
    private sealed class TrackingEmbedder : IEmbedder, IDisposable
    {
        private readonly FakeEmbedder _inner = new();
        public int EmbedCalls;
        public bool Disposed;
        public EmbedderDescriptor Descriptor => _inner.Descriptor;
        public float[] Embed(string text) { EmbedCalls++; return _inner.Embed(text); }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task RunAsync_WithSuppliedEmbedder_UsesItLeavesItUsableAndCleansUpTempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tr-iso-supplied-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var corpus = Path.Combine(dir, "corpus.jsonl");
            var bench = Path.Combine(dir, "bench.jsonl");
            File.WriteAllText(corpus, string.Join("\n", new[]
            {
                "{\"content\":\"pnpm is the preferred package manager\",\"type\":\"correction\",\"tags\":[]}",
                "{\"content\":\"vitest is the test runner\",\"type\":\"preference\",\"tags\":[]}",
            }));
            File.WriteAllText(bench, string.Join("\n", new[]
            {
                "{\"query\":\"package manager\",\"expected_content_contains\":\"pnpm\",\"expected_tier\":\"warm\"}",
                "{\"query\":\"test runner\",\"expected_content_contains\":\"vitest\",\"expected_tier\":\"warm\"}",
            }));

            var embedder = new TrackingEmbedder();
            var tempBefore = TempBenchmarkDbCount();

            var result = await IsolatedBenchmark.RunAsync(
                new BenchmarkOptions(corpus, bench), embedder, CancellationToken.None);

            Assert.Equal(2, result.TotalQueries);
            Assert.True(embedder.EmbedCalls >= 4, "2 corpus entries + 2 queries go through the supplied embedder");
            Assert.False(embedder.Disposed, "the session's embedder must stay alive");
            Assert.Equal(384, embedder.Embed("still usable").Length);

            // Only the seeded corpus is searchable, and the throwaway DB is gone.
            var corpusContents = BenchmarkRunner.ParseCorpus(corpus).Select(c => c.Content).ToHashSet();
            foreach (var d in result.Details)
                if (d.TopResult is not null) Assert.Contains(d.TopResult, corpusContents);
            Assert.Equal(tempBefore, TempBenchmarkDbCount());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_WithSuppliedEmbedder_WhenTheRunnerThrows_StillCleansUpTempDb()
    {
        var tempBefore = TempBenchmarkDbCount();

        await Assert.ThrowsAsync<FileNotFoundException>(() => IsolatedBenchmark.RunAsync(
            new BenchmarkOptions("/nope/corpus.jsonl", "/nope/bench.jsonl"), new TrackingEmbedder(), CancellationToken.None));

        Assert.Equal(tempBefore, TempBenchmarkDbCount());
    }

    // Integration: actually runs the benchmark end-to-end against the bundled
    // corpus and proves results contain ONLY corpus content (nothing from any
    // other database), and the temp DB is cleaned up. Skips when the model /
    // corpus aren't present (mirrors BenchmarkRunnerTests).
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_OnlySurfacesCorpusContent_AndCleansUpTempDb()
    {
        var repoRoot = TryFindRepoRoot();
        if (repoRoot is null) { Console.WriteLine("skipping: model not found"); return; }

        var corpusPath = Path.Combine(repoRoot, "eval", "corpus", "memories.jsonl");
        var benchmarkPath = Path.Combine(repoRoot, "eval", "benchmarks", "smoke.jsonl");
        if (!File.Exists(corpusPath) || !File.Exists(benchmarkPath))
        {
            Console.WriteLine("skipping: corpus/benchmark not found");
            return;
        }

        var tempBefore = TempBenchmarkDbCount();

        var result = await IsolatedBenchmark.RunAsync(
            new BenchmarkOptions(corpusPath, benchmarkPath), CancellationToken.None);

        Assert.True(result.TotalQueries > 0);

        // Every surfaced top result must be one of the seeded corpus entries —
        // proving no live/foreign rows were searchable in the isolated DB.
        var corpus = BenchmarkRunner.ParseCorpus(corpusPath).Select(c => c.Content).ToHashSet();
        foreach (var d in result.Details)
        {
            if (d.TopResult is not null)
                Assert.Contains(d.TopResult, corpus);
        }

        // No leftover tr-benchmark-*.db files (cleanup ran).
        Assert.Equal(tempBefore, TempBenchmarkDbCount());
    }

    private static int TempBenchmarkDbCount()
    {
        try { return Directory.GetFiles(Path.GetTempPath(), "tr-benchmark-*.db").Length; }
        catch { return 0; }
    }

    private static string? TryFindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "models", "registry.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
