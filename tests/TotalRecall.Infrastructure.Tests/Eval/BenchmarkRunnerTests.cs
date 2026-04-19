using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

public sealed class BenchmarkRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public BenchmarkRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    // ----- fakes -----

    private sealed class FakeStore : IStore
    {
        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
        public int InsertCalls;
        public int DeleteCalls;

        public string Insert(Tier tier, ContentType type, InsertEntryOpts opts)
        {
            InsertCalls++;
            var id = "e" + Entries.Count.ToString();
            var entry = new Entry(
                id,
                opts.Content,
                FSharpOption<string>.None,
                FSharpOption<string>.None,
                FSharpOption<SourceTool>.None,
                FSharpOption<string>.None,
                ListModule.OfArray(opts.Tags?.ToArray() ?? Array.Empty<string>()),
                0L, 0L, 0L, 0,
                1.0,
                FSharpOption<string>.None,
                FSharpOption<string>.None,
                "",
                opts.EntryType ?? EntryType.Preference,
                opts.MetadataJson ?? "{}");
            Entries[id] = entry;
            return id;
        }

        public string InsertWithEmbedding(Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding)
            => Insert(tier, type, opts);

        public Entry? Get(Tier tier, ContentType type, string id)
            => Entries.TryGetValue(id, out var e) ? e : null;

        public long? GetInternalKey(Tier tier, ContentType type, string id)
            => Entries.ContainsKey(id) ? 1L : null;

        public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) { }

        public void Delete(Tier tier, ContentType type, string id)
        {
            DeleteCalls++;
            Entries.Remove(id);
        }

        public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
            => Entries.Values.ToList();

        public int Count(Tier tier, ContentType type) => Entries.Count;
        public int CountKnowledgeCollections() => 0;
        public IReadOnlyList<Entry> ListByMetadata(Tier tier, ContentType type, IReadOnlyDictionary<string, string> metadataFilter, ListEntriesOpts? opts = null)
            => Array.Empty<Entry>();
        public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id) { }
    }

    private sealed class FakeVectorSearch : IVectorSearch
    {
        public int InsertCalls;
        public int DeleteCalls;
        public void InsertEmbedding(Tier tier, ContentType type, string entryId, ReadOnlyMemory<float> embedding) => InsertCalls++;
        public void DeleteEmbedding(Tier tier, ContentType type, long rowid) => DeleteCalls++;
        public IReadOnlyList<VectorSearchResult> SearchByVector(Tier tier, ContentType type, ReadOnlyMemory<float> queryVec, VectorSearchOpts opts)
            => Array.Empty<VectorSearchResult>();
        public IReadOnlyList<VectorSearchResult> SearchMultipleTiers(IReadOnlyList<(Tier Tier, ContentType Type)> targets, ReadOnlyMemory<float> queryVec, VectorSearchOpts opts)
            => Array.Empty<VectorSearchResult>();
    }

    private sealed class FakeHybridSearch : IHybridSearch
    {
        private readonly FakeStore _store;
        public FakeHybridSearch(FakeStore store) { _store = store; }

        public IReadOnlyList<SearchResult> Search(
            IReadOnlyList<(Tier Tier, ContentType Type)> tiers,
            string query,
            ReadOnlyMemory<float> queryEmbedding,
            HybridSearchOpts opts)
        {
            // Return any entry whose content contains a token from the query.
            var hits = new List<SearchResult>();
            int rank = 1;
            foreach (var e in _store.Entries.Values)
            {
                if (e.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || query.Split(' ').Any(t => t.Length > 2 && e.Content.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    hits.Add(new SearchResult(e, Tier.Warm, ContentType.Memory, 0.9 - 0.1 * rank, rank));
                    rank++;
                    if (rank > opts.TopK) break;
                }
            }
            return hits;
        }
    }

    [Fact]
    public async Task RunAsync_SeedsCorpusAndCleansUp()
    {
        var corpus = Path.Combine(_tempDir, "corpus.jsonl");
        File.WriteAllText(corpus, string.Join("\n", new[]
        {
            "{\"content\":\"pnpm is the preferred package manager\",\"type\":\"correction\",\"tags\":[\"tooling\"]}",
            "{\"content\":\"vitest is the test runner\",\"type\":\"preference\",\"tags\":[\"testing\"]}",
            "{\"content\":\"sqlite-vec extension provides vector search\",\"type\":\"decision\",\"tags\":[\"db\"]}",
        }));

        var bench = Path.Combine(_tempDir, "bench.jsonl");
        File.WriteAllText(bench, string.Join("\n", new[]
        {
            "{\"query\":\"package manager\",\"expected_content_contains\":\"pnpm\",\"expected_tier\":\"warm\"}",
            "{\"query\":\"test runner\",\"expected_content_contains\":\"vitest\",\"expected_tier\":\"warm\"}",
            "{\"query\":\"vector search extension\",\"expected_content_contains\":\"sqlite-vec\",\"expected_tier\":\"warm\",\"expected_absent\":\"pgvector\"}",
        }));

        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var hybrid = new FakeHybridSearch(store);
        var embedder = new FakeEmbedder();

        var runner = new BenchmarkRunner(store, vec, hybrid, embedder);
        var result = await runner.RunAsync(
            new BenchmarkOptions(corpus, bench),
            CancellationToken.None);

        Assert.Equal(3, result.TotalQueries);
        Assert.Equal(3, result.Details.Count);
        // After teardown, every seeded entry is gone.
        Assert.Empty(store.Entries);
        Assert.Equal(3, store.InsertCalls);
        Assert.Equal(3, store.DeleteCalls);
        Assert.Equal(3, vec.InsertCalls);
        Assert.Equal(3, vec.DeleteCalls);
        // Should match all three by content substring.
        Assert.True(result.ExactMatchRate > 0.0);
    }

    [Fact]
    public async Task RunAsync_NegativeAssertion_PassesWhenAbsent()
    {
        var corpus = Path.Combine(_tempDir, "corpus2.jsonl");
        File.WriteAllText(corpus,
            "{\"content\":\"sqlite-vec is the chosen extension\",\"type\":\"decision\",\"tags\":[]}\n");
        var bench = Path.Combine(_tempDir, "bench2.jsonl");
        File.WriteAllText(bench,
            "{\"query\":\"vector\",\"expected_content_contains\":\"sqlite-vec\",\"expected_tier\":\"warm\",\"expected_absent\":\"pgvector\"}\n");

        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var hybrid = new FakeHybridSearch(store);
        var embedder = new FakeEmbedder();

        var runner = new BenchmarkRunner(store, vec, hybrid, embedder);
        var result = await runner.RunAsync(
            new BenchmarkOptions(corpus, bench),
            CancellationToken.None);

        Assert.Equal(1, result.TotalQueries);
        Assert.True(result.Details[0].HasNegativeAssertion);
        Assert.True(result.Details[0].NegativePass);
        Assert.Equal(1.0, result.NegativePassRate);
    }

    [Fact]
    public async Task RunAsync_MissingCorpusFile_Throws()
    {
        var runner = new BenchmarkRunner(new FakeStore(), new FakeVectorSearch(), new FakeHybridSearch(new FakeStore()), new FakeEmbedder());
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            runner.RunAsync(new BenchmarkOptions("/nope/corpus.jsonl", "/nope/bench.jsonl"), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_RealOnnxAndRealCorpus_SmokesEndToEnd()
    {
        // Plan 5.3b review cleanup: gate on model presence so CI without the
        // bundled ONNX artifact silently skips. Logs the skip for CI traces
        // but reports the test as passed.
        var repoRoot = TryFindRepoRoot();
        if (repoRoot is null)
        {
            Console.WriteLine("skipping: model not found");
            return;
        }
        var corpusPath = Path.Combine(repoRoot, "eval", "corpus", "memories.jsonl");
        var benchPath = Path.Combine(repoRoot, "eval", "benchmarks", "smoke.jsonl");
        if (!File.Exists(corpusPath) || !File.Exists(benchPath))
        {
            Console.WriteLine("skipping: model not found");
            return;
        }

        var dbPath = Path.Combine(_tempDir, "real.db");
        var conn = SqliteConnection.Open(dbPath);
        try
        {
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);
            var fts = new FtsSearch(conn);
            var hybrid = new HybridSearch(vec, fts, store);

            var bundledModelsDir = Path.Combine(repoRoot, "models");
            var registry = ModelRegistry.LoadFromFile(Path.Combine(bundledModelsDir, "registry.json"));
            var userDir = Path.Combine(_tempDir, "models");
            Directory.CreateDirectory(userDir);
            var manager = new ModelManager(registry, bundledModelsDir, userDir);
            using var embedder = new OnnxEmbedder(manager, "all-MiniLM-L6-v2");

            var runner = new BenchmarkRunner(store, vec, hybrid, embedder);
            var result = await runner.RunAsync(
                new BenchmarkOptions(corpusPath, benchPath),
                CancellationToken.None);

            Assert.True(result.TotalQueries > 0);
            Assert.Equal(result.TotalQueries, result.Details.Count);
        }
        finally
        {
            conn.Dispose();
        }
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

    [Fact]
    public void ParseQueries_SupportsOptionalExpectedAbsent()
    {
        var path = Path.Combine(_tempDir, "q.jsonl");
        File.WriteAllText(path,
            "{\"query\":\"a\",\"expected_content_contains\":\"x\",\"expected_tier\":\"warm\"}\n" +
            "{\"query\":\"b\",\"expected_content_contains\":\"y\",\"expected_tier\":\"hot\",\"expected_absent\":\"z\"}\n");
        var rows = BenchmarkRunner.ParseQueries(path);
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].ExpectedAbsent);
        Assert.Equal("z", rows[1].ExpectedAbsent);
    }
}
