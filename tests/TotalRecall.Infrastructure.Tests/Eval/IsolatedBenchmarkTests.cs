using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
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
