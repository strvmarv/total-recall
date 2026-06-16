// src/TotalRecall.Server/Handlers/InsightsHandler.cs
//
// Insights MCP tool — entry-level analysis of the local memory store. Returns
// actionable findings (near-duplicate clusters, pin-promotion candidates,
// retrieval gaps, a recall-vs-threshold curve) plus a self-explaining health
// score whose four sub-scores sum to the total.
//
// Args (all optional):
//   { days? (7), nearDupThreshold? (0.85), pinAccessThreshold? (8), limit? (200) }
//
// Like the other engine-analysis handlers (EvalReport/EvalBenchmark/EvalGrow),
// the production path self-bootstraps a short-lived local SQLite connection +
// VectorSearch + ONNX embedder via an IInsightsContext seam; tests inject a
// context backed by an in-memory SqliteStore + VectorSearch + a deterministic
// embedder so near-duplicate grouping can be exercised without the ONNX model.
//
// Near-duplicate embedding access: we RE-EMBED each live-set entry's content
// via the same embedder used to store it (deterministic → identical vector),
// rather than reading the raw vec0 row by rowid. This is the simpler correct
// option: it needs no per-tier vec0 SQL and flows cleanly through the IEmbedder
// seam the rest of the handler already depends on.
//
// v1 limitation — near-duplicate detection is SAME-TIER: each live-set entry is
// only compared against other entries in its own tier (the vec0 search is scoped
// to one tier table). Identical content split across different tiers (e.g. a hot
// copy and a warm copy) will NOT cluster together in v1.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// Resources the <see cref="InsightsHandler"/> analyzes. The production
/// implementation owns a short-lived SQLite connection (disposed via
/// <see cref="IDisposable"/>); tests inject an in-memory-backed context.
/// </summary>
public interface IInsightsContext : IDisposable
{
    IStore Store { get; }
    IVectorSearch Vectors { get; }
    IEmbedder Embedder { get; }

    /// <summary>Configured similarity threshold (Warm tier) — the curve's "current".</summary>
    double SimilarityThreshold { get; }

    /// <summary>
    /// Configured pinned-tier max content chars (<c>tiers.pinned.max_content_chars</c>,
    /// default 500). Pin candidates whose content exceeds this cap cannot be
    /// pinned (<c>memory_pin</c> rejects them), so they are filtered out.
    /// </summary>
    int PinnedMaxContentChars { get; }

    /// <summary>
    /// Retrieval events in the last <paramref name="days"/> window, optionally
    /// scoped to a single <paramref name="source"/> (<c>query_source</c>). Pass
    /// <c>null</c> for all sources. Quality/health metrics request the
    /// "assistant" source (assistant feedback drives retrieval quality), while
    /// gap detection reads all sources.
    /// </summary>
    IReadOnlyList<RetrievalEventRow> GetEvents(int days, string? source = null);

    /// <summary>Pending benchmark candidates (frequent low-score queries).</summary>
    IReadOnlyList<CandidateRow> ListPendingCandidates();

    /// <summary>Total KB chunks (knowledge entries across all tiers).</summary>
    int TotalKnowledgeChunks();
}

/// <summary>Test/composition seam.</summary>
public delegate IInsightsContext InsightsContextProvider();

public sealed class InsightsHandler : IToolHandler
{
    // Curve thresholds, fixed per the contract.
    private static readonly double[] CurveThresholds = { 0.5, 0.6, 0.7, 0.8, 0.9 };

    // Curated entry types that count toward the capture sub-score.
    private static readonly EntryType[] CuratedTypes =
        { EntryType.Correction, EntryType.Preference, EntryType.Decision };

    private const int PreviewMaxChars = 120;
    private const int RecentCaptureWindow = 30;
    private const int MaxPinCandidates = 10;
    private const int MaxRetrievalGaps = 5;
    // Per-entry vec0 over-sample cap when grouping near-duplicates: ask for up to
    // this many same-tier neighbors per entry (capped further by the live set).
    private const int NearDupTopK = 50;
    // Grace window before an un-acted retrieval resolves to a miss (60 min),
    // matching eval_report's default.
    private const long GraceWindowMs = 60L * 60_000;

    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "days": {"type":"integer","description":"Window in days for retrieval-gap + threshold-curve analysis (default 7)"},
            "nearDupThreshold": {"type":"number","description":"Min similarity (app score scale) to cluster near-duplicates (default 0.85)"},
            "pinAccessThreshold": {"type":"integer","description":"Min access_count for a pin-promotion candidate (default 8)"},
            "limit": {"type":"integer","description":"Cap on live-set memories scanned for near-duplicates (default 200)"}
          }
        }
        """).RootElement.Clone();

    private readonly InsightsContextProvider? _provider;

    public InsightsHandler() { _provider = null; }

    /// <summary>Test/composition seam.</summary>
    public InsightsHandler(InsightsContextProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Name => "insights";
    public string Description =>
        "Entry-level analysis of the memory store: near-duplicate clusters, pin candidates, retrieval gaps, threshold curve, and a self-explaining health score. Near-duplicate detection is same-tier in v1: identical content split across different tiers won't cluster together.";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        int days = 7;
        double nearDupThreshold = 0.85;
        int pinAccessThreshold = 8;
        int limit = 200;

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;
            days = ReadPositiveInt(args, "days", days);
            pinAccessThreshold = ReadPositiveInt(args, "pinAccessThreshold", pinAccessThreshold);
            limit = ReadPositiveInt(args, "limit", limit);
            if (args.TryGetProperty("nearDupThreshold", out var nEl))
            {
                if (nEl.ValueKind != JsonValueKind.Number)
                    throw new ArgumentException("nearDupThreshold must be a number");
                nearDupThreshold = nEl.GetDouble();
                if (nearDupThreshold is < 0.0 or > 1.0)
                    throw new ArgumentException("nearDupThreshold must be between 0 and 1");
            }
        }

        ct.ThrowIfCancellationRequested();

        using var context = (_provider ?? BuildProductionProvider())();

        var nearDuplicates = ComputeNearDuplicates(context, nearDupThreshold, limit, ct);
        var pinCandidates = ComputePinCandidates(context, pinAccessThreshold);
        // Gap detection spans ALL sources (a weak query is a gap regardless of who
        // issued it). Retrieval-quality metrics (threshold curve + health) are
        // driven by ASSISTANT feedback, so they read the assistant source only —
        // consistent with eval_report.
        var gapEvents = context.GetEvents(days, source: null);
        var qualityEvents = context.GetEvents(days, source: "assistant");
        var retrievalGaps = ComputeRetrievalGaps(context, gapEvents);
        var thresholdCurve = ComputeThresholdCurve(qualityEvents, context.SimilarityThreshold);
        var breakdown = ComputeHealthBreakdown(context, qualityEvents);
        var healthScore = Math.Clamp(
            breakdown.Retrieval.Score + breakdown.Capture.Score + breakdown.Pinned.Score + breakdown.Kb.Score,
            0, 100);

        var dto = new InsightsResultDto(
            HealthScore: healthScore,
            HealthBreakdown: breakdown,
            NearDuplicates: nearDuplicates,
            PinCandidates: pinCandidates,
            RetrievalGaps: retrievalGaps,
            ThresholdCurve: thresholdCurve);

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.InsightsResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
        });
    }

    // ---- near-duplicates ---------------------------------------------------

    private static NearDuplicateGroupDto[] ComputeNearDuplicates(
        IInsightsContext ctx, double threshold, int limit, CancellationToken ct)
    {
        // Live set = hot + warm + pinned memory tiers (skip cold archive),
        // capped at `limit` total entries scanned. Push the cap down to SQL via
        // a running remainder so we never materialize more rows than we'll keep.
        var liveTiers = new[] { Tier.Hot, Tier.Warm, Tier.Pinned };
        var entries = new List<(Tier Tier, Entry Entry)>();
        int remaining = limit;
        foreach (var tier in liveTiers)
        {
            if (remaining <= 0) break;
            foreach (var e in ctx.Store.List(tier, ContentType.Memory,
                new ListEntriesOpts { Limit = remaining }))
            {
                entries.Add((tier, e));
                remaining--;
            }
        }

        if (entries.Count < 2) return Array.Empty<NearDuplicateGroupDto>();

        // Index for union-find + per-entry lookup.
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++) indexById[entries[i].Entry.Id] = i;

        var uf = new UnionFind(entries.Count);
        // Best pairwise score observed between any two members that got unioned.
        var pairScore = new Dictionary<(int, int), double>();

        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (tier, entry) = entries[i];
            var vec = ctx.Embedder.Embed(entry.Content);
            // Same-tier/type search. Oversample generously; the live set is capped.
            var topK = Math.Min(entries.Count, NearDupTopK);
            var hits = ctx.Vectors.SearchByVector(
                tier, ContentType.Memory,
                vec.AsMemory(),
                new VectorSearchOpts(topK, MinScore: threshold));

            foreach (var hit in hits)
            {
                if (hit.Id == entry.Id) continue;            // skip self
                if (!indexById.TryGetValue(hit.Id, out var j)) continue; // outside live set
                if (hit.Score < threshold) continue;
                uf.Union(i, j);
                var key = i < j ? (i, j) : (j, i);
                if (!pairScore.TryGetValue(key, out var existing) || hit.Score > existing)
                    pairScore[key] = hit.Score;
            }
        }

        // Collect clusters of size >= 2.
        var clusters = new Dictionary<int, List<int>>();
        for (int i = 0; i < entries.Count; i++)
        {
            var root = uf.Find(i);
            if (!clusters.TryGetValue(root, out var members))
            {
                members = new List<int>();
                clusters[root] = members;
            }
            members.Add(i);
        }

        var groups = new List<NearDuplicateGroupDto>();
        foreach (var kvp in clusters)
        {
            var members = kvp.Value;
            if (members.Count < 2) continue;

            // Build the membership set once per cluster so the pairScore scans
            // below are O(1) lookups instead of O(M) List.Contains probes.
            var memberSet = new HashSet<int>(members);

            // topScore = max pairwise score among members of this cluster.
            double topScore = 0;
            foreach (var (key, score) in pairScore)
            {
                if (memberSet.Contains(key.Item1) && memberSet.Contains(key.Item2) && score > topScore)
                    topScore = score;
            }

            // Per-member score = its best pairwise score against any other cluster member.
            var memberDtos = new List<NearDuplicateMemberDto>(members.Count);
            foreach (var mi in members)
            {
                double memberBest = 0;
                foreach (var (key, score) in pairScore)
                {
                    if ((key.Item1 == mi || key.Item2 == mi)
                        && memberSet.Contains(key.Item1) && memberSet.Contains(key.Item2)
                        && score > memberBest)
                        memberBest = score;
                }
                var (tier, entry) = entries[mi];
                memberDtos.Add(new NearDuplicateMemberDto(
                    Id: entry.Id,
                    Tier: TierName(tier),
                    Preview: Preview(entry.Content),
                    Score: memberBest,
                    CreatedAt: entry.CreatedAt));
            }
            memberDtos.Sort((a, b) => b.Score.CompareTo(a.Score));

            groups.Add(new NearDuplicateGroupDto(
                // GroupId is the highest-scoring member (memberDtos is sorted by
                // score DESC) — it serves only as the cluster anchor/identity.
                // The Phase 3 UI "keep newest, delete the rest" choice is made
                // client-side from each member's createdAt, NOT from this anchor.
                GroupId: memberDtos[0].Id,
                TopScore: topScore,
                Members: memberDtos.ToArray()));
        }

        groups.Sort((a, b) => b.TopScore.CompareTo(a.TopScore));
        return groups.ToArray();
    }

    // ---- pin candidates ----------------------------------------------------

    private static PinCandidateDto[] ComputePinCandidates(IInsightsContext ctx, int pinAccessThreshold)
    {
        // Across hot/warm/cold memory tiers, list by access_count DESC, keep
        // entries with access_count >= threshold that are NOT already pinned.
        // (Listing only non-pinned tiers already excludes pinned entries.)
        //
        // Also exclude entries whose content exceeds the pinned-tier cap
        // (tiers.pinned.max_content_chars): memory_pin rejects oversize content,
        // so suggesting it would yield an un-pinnable candidate. We over-fetch a
        // little extra per tier so the cap filter doesn't starve the final cap-to-10.
        var pinnedMaxContentChars = ctx.PinnedMaxContentChars;
        var candidates = new List<(Tier Tier, Entry Entry)>();
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
        {
            var rows = ctx.Store.List(tier, ContentType.Memory,
                new ListEntriesOpts { OrderBy = "access_count DESC", Limit = MaxPinCandidates * 5 });
            int kept = 0;
            foreach (var e in rows)
            {
                if (kept >= MaxPinCandidates) break;
                if (e.AccessCount < pinAccessThreshold) continue;
                if (e.Content.Length > pinnedMaxContentChars) continue;
                candidates.Add((tier, e));
                kept++;
            }
        }

        return candidates
            .OrderByDescending(c => c.Entry.AccessCount)
            .Take(MaxPinCandidates)
            .Select(c => new PinCandidateDto(
                Id: c.Entry.Id,
                Tier: TierName(c.Tier),
                Preview: Preview(c.Entry.Content),
                AccessCount: c.Entry.AccessCount))
            .ToArray();
    }

    // ---- retrieval gaps ----------------------------------------------------

    private static RetrievalGapDto[] ComputeRetrievalGaps(
        IInsightsContext ctx, IReadOnlyList<RetrievalEventRow> events)
    {
        // Prefer pending benchmark candidates (already frequency-aggregated by
        // times_seen, cross-session and authoritative). Merge in aggregated
        // Top-Misses from the event log for any query a candidate row does NOT
        // already cover, so a fresh store with no candidates still surfaces gaps.
        var byQuery = new Dictionary<string, (int Count, double? TopScore)>(StringComparer.Ordinal);

        var candidateQueries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in ctx.ListPendingCandidates())
        {
            byQuery[c.QueryText] = (c.TimesSeen, c.TopScore);
            candidateQueries.Add(c.QueryText);
        }

        // Aggregate misses (no/low score) from the events themselves.
        var threshold = ctx.SimilarityThreshold;
        foreach (var e in events)
        {
            var isMiss = e.TopScore is null || e.TopScore.Value < threshold;
            if (!isMiss) continue;
            if (candidateQueries.Contains(e.QueryText)) continue; // candidate count wins

            byQuery.TryGetValue(e.QueryText, out var existing);
            var top = existing.TopScore;
            if (e.TopScore.HasValue && (top is null || e.TopScore.Value < top.Value))
                top = e.TopScore;
            byQuery[e.QueryText] = (existing.Count + 1, top);
        }

        return byQuery
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Value.TopScore ?? -1.0)
            .Take(MaxRetrievalGaps)
            .Select(kv => new RetrievalGapDto(
                Query: kv.Key,
                TimesSeen: kv.Value.Count,
                TopScore: kv.Value.TopScore))
            .ToArray();
    }

    // ---- threshold curve ---------------------------------------------------

    private static ThresholdCurveDto ComputeThresholdCurve(
        IReadOnlyList<RetrievalEventRow> events, double current)
    {
        var points = new ThresholdPointDto[CurveThresholds.Length];
        for (int i = 0; i < CurveThresholds.Length; i++)
        {
            var t = CurveThresholds[i];
            var report = Metrics.Compute(events, t,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), GraceWindowMs);
            points[i] = new ThresholdPointDto(
                Threshold: t,
                HitRate: report.HitRate,
                Precision: report.Precision,
                Mrr: report.Mrr);
        }
        return new ThresholdCurveDto(Current: current, Points: points);
    }

    // ---- health breakdown --------------------------------------------------

    private static HealthBreakdownDto ComputeHealthBreakdown(
        IInsightsContext ctx, IReadOnlyList<RetrievalEventRow> events)
    {
        // Retrieval (max 35): round(hitRate*35); neutral 25 when 0 events.
        HealthComponentDto retrieval;
        if (events.Count == 0)
        {
            retrieval = new HealthComponentDto(25, 35, "no retrieval events yet (neutral default)");
        }
        else
        {
            var report = Metrics.Compute(events, ctx.SimilarityThreshold,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), GraceWindowMs);
            var score = (int)Math.Round(report.HitRate * 35.0, MidpointRounding.AwayFromZero);
            retrieval = new HealthComponentDto(
                Math.Clamp(score, 0, 35), 35,
                $"hit rate {Math.Round(report.HitRate * 100.0)}%");
        }

        // Capture (max 25): round(min(1, curatedRatio/0.3)*25) over last 30
        // memories (created order), curated = entry_type in {correction, preference, decision}.
        var recent = new List<Entry>();
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold, Tier.Pinned })
            recent.AddRange(ctx.Store.List(tier, ContentType.Memory,
                new ListEntriesOpts { OrderBy = "created_at DESC", Limit = RecentCaptureWindow }));
        recent = recent
            .OrderByDescending(e => e.CreatedAt)
            .Take(RecentCaptureWindow)
            .ToList();
        var curated = recent.Count(e => CuratedTypes.Any(t => e.EntryType.Equals(t)));
        HealthComponentDto capture;
        if (recent.Count == 0)
        {
            // Intentional parity with the client's computeHealthScore: when there
            // are no recent entries the client returns ratio 1 (→ full 25 marks),
            // so the server must award full capture marks too, not 0.
            capture = new HealthComponentDto(25, 25, "no recent entries yet — assumed healthy");
        }
        else
        {
            var curatedRatio = (double)curated / recent.Count;
            var captureScore = (int)Math.Round(Math.Min(1.0, curatedRatio / 0.3) * 25.0, MidpointRounding.AwayFromZero);
            capture = new HealthComponentDto(
                Math.Clamp(captureScore, 0, 25), 25,
                $"{curated} of {recent.Count} recent entries are curated");
        }

        // Pinned (max 20): <= 15 → 20, else max(0, 20-(count-15)).
        // Parity with the client health score, which sums pinned memories AND
        // pinned knowledge. Use IStore.Count (same mechanism as StatusHandler's
        // tier sizing) rather than materializing rows via List().Count.
        var pinnedCount = ctx.Store.Count(Tier.Pinned, ContentType.Memory)
            + ctx.Store.Count(Tier.Pinned, ContentType.Knowledge);
        int pinnedScore = pinnedCount <= 15 ? 20 : Math.Max(0, 20 - (pinnedCount - 15));
        var pinnedDetail = pinnedCount <= 15
            ? $"{pinnedCount} pinned — within budget"
            : $"{pinnedCount} pinned — {pinnedCount - 15} over budget";
        var pinned = new HealthComponentDto(pinnedScore, 20, pinnedDetail);

        // KB (max 20): totalChunks>0 → 20, else 10.
        var totalChunks = ctx.TotalKnowledgeChunks();
        var kb = new HealthComponentDto(
            totalChunks > 0 ? 20 : 10, 20,
            totalChunks > 0 ? $"{totalChunks} knowledge chunks indexed" : "no knowledge base ingested");

        return new HealthBreakdownDto(retrieval, capture, pinned, kb);
    }

    // ---- helpers -----------------------------------------------------------

    private static int ReadPositiveInt(JsonElement args, string name, int fallback)
    {
        if (!args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind != JsonValueKind.Number)
            throw new ArgumentException($"{name} must be a number");
        // TryGetInt32 returns false for fractional numbers (e.g. 7.5) instead of
        // throwing InvalidOperationException like GetInt32 — surface a clear
        // ArgumentException so callers see a sensible validation error.
        if (!el.TryGetInt32(out var v))
            throw new ArgumentException($"{name} must be an integer");
        if (v <= 0) throw new ArgumentException($"{name} must be positive");
        return v;
    }

    private static string Preview(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        // Split on any run of whitespace (null char[] = default whitespace set),
        // dropping empties and trimming each token, then rejoin with single
        // spaces — collapses newlines/tabs/runs into a clean single-line preview.
        var collapsed = string.Join(' ', content.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return collapsed.Length <= PreviewMaxChars ? collapsed : collapsed.Substring(0, PreviewMaxChars);
    }

    private static string TierName(Tier tier) =>
        tier.IsHot ? "hot" : tier.IsWarm ? "warm" : tier.IsCold ? "cold" : "pinned";

    private static InsightsContextProvider BuildProductionProvider() =>
        () => ProductionInsightsContext.Open();

    /// <summary>
    /// Disjoint-set union-find with path compression + union by size. Collapses
    /// transitive near-duplicate pairs into a single cluster.
    /// </summary>
    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _size;

        public UnionFind(int n)
        {
            _parent = new int[n];
            _size = new int[n];
            for (int i = 0; i < n; i++) { _parent[i] = i; _size[i] = 1; }
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
        }
    }

    /// <summary>
    /// Production context: opens a short-lived local SQLite connection, builds a
    /// SqliteStore + VectorSearch + ONNX embedder, and resolves the configured
    /// similarity threshold. Mirrors EvalBenchmarkHandler's local-store setup.
    /// </summary>
    private sealed class ProductionInsightsContext : IInsightsContext
    {
        private readonly MsSqliteConnection _conn;
        private readonly IEmbedder _embedder;

        private ProductionInsightsContext(
            MsSqliteConnection conn, IStore store, IVectorSearch vectors,
            IEmbedder embedder, double threshold, int pinnedMaxContentChars)
        {
            _conn = conn;
            Store = store;
            Vectors = vectors;
            _embedder = embedder;
            SimilarityThreshold = threshold;
            PinnedMaxContentChars = pinnedMaxContentChars;
        }

        public IStore Store { get; }
        public IVectorSearch Vectors { get; }
        public IEmbedder Embedder => _embedder;
        public double SimilarityThreshold { get; }
        public int PinnedMaxContentChars { get; }

        public static ProductionInsightsContext Open()
        {
            var cfg = new ConfigLoader().LoadEffectiveConfig();
            var threshold = cfg.Tiers.Warm.SimilarityThreshold;
            // Pinned tier is optional in Config.fs (absent in legacy config files);
            // fall back to the shared default cap (500) when the section is missing.
            var pinnedMaxContentChars =
                FSharpOption<Core.Config.PinnedTierConfig>.get_IsSome(cfg.Tiers.Pinned)
                    ? cfg.Tiers.Pinned.Value.MaxContentChars
                    : PinnedTierLimits.DefaultMaxContentChars;
            var dbPath = ConfigLoader.GetDbPath();
            var conn = SqliteConnection.Open(dbPath);
            try
            {
                MigrationRunner.RunMigrations(conn);
                var store = new SqliteStore(conn);
                var vec = new VectorSearch(conn);
                var embedder = EmbedderFactory.CreateFromConfig(cfg.Embedding);
                return new ProductionInsightsContext(conn, store, vec, embedder, threshold, pinnedMaxContentChars);
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        public IReadOnlyList<RetrievalEventRow> GetEvents(int days, string? source = null) =>
            new RetrievalEventLog(_conn).GetEvents(new RetrievalEventQuery(Days: days, QuerySource: source));

        public IReadOnlyList<CandidateRow> ListPendingCandidates() =>
            new BenchmarkCandidates(_conn).ListPending();

        public int TotalKnowledgeChunks()
        {
            var total = 0;
            foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold, Tier.Pinned })
                total += Store.Count(tier, ContentType.Knowledge);
            return total;
        }

        public void Dispose()
        {
            try { (_embedder as IDisposable)?.Dispose(); } catch { /* best-effort */ }
            try { _conn.Dispose(); } catch { /* best-effort */ }
        }
    }
}
