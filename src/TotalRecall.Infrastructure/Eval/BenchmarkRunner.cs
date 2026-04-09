// src/TotalRecall.Infrastructure/Eval/BenchmarkRunner.cs
//
// Plan 5 Task 5.3a — port of src-ts/eval/benchmark-runner.ts.
//
// Seeds a corpus of memories into the warm tier (using the same
// SqliteStore + VectorSearch insert pattern as MemoryStoreHandler), runs
// each benchmark query through HybridSearch with topK=3, tallies match
// statistics, then deletes the seeded rows.
//
// JSONL parsing uses JsonDocument (line-by-line) rather than a source-gen
// JsonSerializerContext: the per-line shapes are tiny (3-4 string fields,
// one optional negative-assertion field) and avoiding source gen here keeps
// us from polluting the Server-side JsonContext.cs and dragging in
// reflection-based deserializers. The reads happen once per run, so the
// performance cost is negligible.
//
// This runner intentionally does NOT log retrieval events — the benchmark
// is a measurement harness, not a session.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Eval;

/// <summary>Inputs for one benchmark run.</summary>
public sealed record BenchmarkOptions(string CorpusPath, string BenchmarkPath);

/// <summary>Per-query trace recorded by <see cref="BenchmarkRunner.RunAsync"/>.</summary>
public sealed record BenchmarkDetail(
    string Query,
    string ExpectedContains,
    string? TopResult,
    double TopScore,
    bool Matched,
    bool FuzzyMatched,
    bool HasNegativeAssertion,
    bool NegativePass);

/// <summary>Aggregate result returned by <see cref="BenchmarkRunner.RunAsync"/>.</summary>
public sealed record BenchmarkResult(
    int TotalQueries,
    double ExactMatchRate,
    double FuzzyMatchRate,
    double TierRoutingRate,
    double NegativePassRate,
    double AvgLatencyMs,
    IReadOnlyList<BenchmarkDetail> Details);

/// <summary>
/// Orchestrator for the retrieval-quality benchmark suite. See file header
/// for design notes.
/// </summary>
public sealed class BenchmarkRunner
{
    private readonly ISqliteStore _store;
    private readonly IVectorSearch _vectorSearch;
    private readonly IHybridSearch _hybridSearch;
    private readonly IEmbedder _embedder;

    public BenchmarkRunner(
        ISqliteStore store,
        IVectorSearch vectorSearch,
        IHybridSearch hybridSearch,
        IEmbedder embedder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vectorSearch = vectorSearch ?? throw new ArgumentNullException(nameof(vectorSearch));
        _hybridSearch = hybridSearch ?? throw new ArgumentNullException(nameof(hybridSearch));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    /// <summary>
    /// Run the benchmark. Seeds the corpus into warm/memory, executes each
    /// query, and tears the seeded rows down before returning.
    /// </summary>
    public Task<BenchmarkResult> RunAsync(BenchmarkOptions opts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (!File.Exists(opts.CorpusPath))
            throw new FileNotFoundException(
                $"benchmark corpus not found: {opts.CorpusPath}", opts.CorpusPath);
        if (!File.Exists(opts.BenchmarkPath))
            throw new FileNotFoundException(
                $"benchmark queries not found: {opts.BenchmarkPath}", opts.BenchmarkPath);

        var corpus = ParseCorpus(opts.CorpusPath);
        var queries = ParseQueries(opts.BenchmarkPath);

        var seeded = new List<string>(corpus.Count);
        try
        {
            // ---- seed corpus into warm/memory ----
            foreach (var entry in corpus)
            {
                ct.ThrowIfCancellationRequested();
                var vector = _embedder.Embed(entry.Content);
                var metadataJson = $"{{\"entry_type\":\"{EscapeForJson(entry.Type)}\"}}";
                var id = _store.Insert(
                    Tier.Warm,
                    ContentType.Memory,
                    new InsertEntryOpts(
                        Content: entry.Content,
                        Tags: entry.Tags,
                        MetadataJson: metadataJson));
                _vectorSearch.InsertEmbedding(Tier.Warm, ContentType.Memory, id, vector);
                seeded.Add(id);
            }

            // ---- run each query ----
            var details = new List<BenchmarkDetail>(queries.Count);
            int exactMatches = 0;
            int fuzzyMatches = 0;
            int tierMatches = 0;
            double totalLatencyMs = 0;

            var tiers = new (Tier, ContentType)[] { (Tier.Warm, ContentType.Memory) };

            foreach (var bq in queries)
            {
                ct.ThrowIfCancellationRequested();
                var qVec = _embedder.Embed(bq.Query);

                var sw = Stopwatch.StartNew();
                var results = _hybridSearch.Search(
                    tiers,
                    bq.Query,
                    qVec,
                    new HybridSearchOpts(TopK: 3));
                sw.Stop();
                var latencyMs = sw.Elapsed.TotalMilliseconds;
                totalLatencyMs += latencyMs;

                var hasTop = results.Count > 0;
                string? topContent = hasTop ? results[0].Entry.Content : null;
                double topScore = hasTop ? results[0].Score : 0.0;
                Tier? topTier = hasTop ? results[0].Tier : null;

                bool matched = topContent is not null
                    && topContent.Contains(bq.ExpectedContentContains, StringComparison.Ordinal);

                bool fuzzyMatched = matched;
                if (!fuzzyMatched)
                {
                    for (int i = 1; i < results.Count; i++)
                    {
                        if (results[i].Entry.Content.Contains(
                                bq.ExpectedContentContains, StringComparison.Ordinal))
                        {
                            fuzzyMatched = true;
                            break;
                        }
                    }
                }

                bool tierRouted = topTier is not null
                    && string.Equals(TierName(topTier), bq.ExpectedTier, StringComparison.Ordinal);

                bool negativePass = true;
                if (!string.IsNullOrEmpty(bq.ExpectedAbsent) && topContent is not null)
                {
                    negativePass = !topContent.Contains(
                        bq.ExpectedAbsent, StringComparison.OrdinalIgnoreCase);
                }

                if (matched) exactMatches++;
                if (fuzzyMatched) fuzzyMatches++;
                if (tierRouted) tierMatches++;

                details.Add(new BenchmarkDetail(
                    Query: bq.Query,
                    ExpectedContains: bq.ExpectedContentContains,
                    TopResult: topContent,
                    TopScore: topScore,
                    Matched: matched,
                    FuzzyMatched: fuzzyMatched,
                    HasNegativeAssertion: !string.IsNullOrEmpty(bq.ExpectedAbsent),
                    NegativePass: negativePass));
            }

            int total = queries.Count;
            int negativeCount = 0;
            int negativePassCount = 0;
            foreach (var d in details)
            {
                if (d.HasNegativeAssertion)
                {
                    negativeCount++;
                    if (d.NegativePass) negativePassCount++;
                }
            }

            var result = new BenchmarkResult(
                TotalQueries: total,
                ExactMatchRate: total > 0 ? (double)exactMatches / total : 0,
                FuzzyMatchRate: total > 0 ? (double)fuzzyMatches / total : 0,
                TierRoutingRate: total > 0 ? (double)tierMatches / total : 0,
                NegativePassRate: negativeCount > 0
                    ? (double)negativePassCount / negativeCount
                    : 1.0,
                AvgLatencyMs: total > 0 ? totalLatencyMs / total : 0,
                Details: details);

            return Task.FromResult(result);
        }
        finally
        {
            // ---- cleanup seeded rows ----
            foreach (var id in seeded)
            {
                try
                {
                    var rowid = _store.GetRowid(Tier.Warm, ContentType.Memory, id);
                    if (rowid is not null)
                        _vectorSearch.DeleteEmbedding(Tier.Warm, ContentType.Memory, rowid.Value);
                    _store.Delete(Tier.Warm, ContentType.Memory, id);
                }
                catch
                {
                    // best-effort cleanup; don't mask the original exception
                }
            }
        }
    }

    // ---------- JSONL parsing ----------

    internal sealed record CorpusEntry(string Content, string Type, IReadOnlyList<string> Tags);

    internal sealed record QueryEntry(
        string Query,
        string ExpectedContentContains,
        string ExpectedTier,
        string? ExpectedAbsent);

    internal static IReadOnlyList<CorpusEntry> ParseCorpus(string path)
    {
        var list = new List<CorpusEntry>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var content = root.GetProperty("content").GetString() ?? "";
            var type = root.TryGetProperty("type", out var typeEl)
                ? (typeEl.GetString() ?? "imported")
                : "imported";
            var tags = new List<string>();
            if (root.TryGetProperty("tags", out var tagsEl)
                && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                {
                    var s = t.GetString();
                    if (s is not null) tags.Add(s);
                }
            }
            list.Add(new CorpusEntry(content, type, tags));
        }
        return list;
    }

    internal static IReadOnlyList<QueryEntry> ParseQueries(string path)
    {
        var list = new List<QueryEntry>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var query = root.GetProperty("query").GetString() ?? "";
            var expected = root.GetProperty("expected_content_contains").GetString() ?? "";
            var tier = root.TryGetProperty("expected_tier", out var tierEl)
                ? (tierEl.GetString() ?? "warm")
                : "warm";
            string? absent = null;
            if (root.TryGetProperty("expected_absent", out var absEl)
                && absEl.ValueKind == JsonValueKind.String)
            {
                absent = absEl.GetString();
            }
            list.Add(new QueryEntry(query, expected, tier, absent));
        }
        return list;
    }

    private static string TierName(Tier t) =>
        t.IsHot ? "hot" : t.IsWarm ? "warm" : "cold";

    // The corpus type field is drawn from a closed set of TS EntryType values
    // which never contain JSON-special characters, so we only need a minimal
    // backslash/quote escape for safety.
    private static string EscapeForJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
