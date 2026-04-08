using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Search;

/// <summary>
/// Options for <see cref="HybridSearch.Search"/>. Mirrors the options bag
/// accepted by <c>searchMemory</c> in <c>src-ts/memory/search.ts</c>.
/// <para>
/// <see cref="MinScore"/> is propagated to the vector seam only (matching
/// the TS reference which does not pass minScore to FTS).
/// </para>
/// <para>
/// <see cref="FtsWeight"/> defaults to <c>0.3</c> when null — see
/// <c>DEFAULT_FTS_WEIGHT</c> in the TS reference.
/// </para>
/// </summary>
public sealed record HybridSearchOpts(
    int TopK,
    double? MinScore = null,
    double? FtsWeight = null);

/// <summary>
/// Orchestration layer that fuses vector and FTS5 search across one or more
/// (tier, type) pairs. Ports <c>searchMemory</c> from
/// <c>src-ts/memory/search.ts</c>.
///
/// <para>
/// Each underlying seam is queried with <c>topK * 2</c> (oversampling) so
/// that fusion has room to reorder. Scores are merged per-id: max vector
/// score wins across tier hits, max FTS score wins across tier hits, and
/// the fused score is computed by the F# Core
/// <see cref="Ranking.hybridScore(double, double, double)"/> function —
/// Infrastructure does not own the ranking math.
/// </para>
///
/// <para>
/// <b>Side effect:</b> every resolved entry is "touched"
/// (<see cref="UpdateEntryOpts.Touch"/>) via <see cref="ISqliteStore.Update"/>,
/// bumping <c>access_count</c> and <c>last_accessed_at</c>. This matches the
/// TS behaviour where reads update LRU metadata.
/// </para>
///
/// <para>
/// This class deliberately does not implement an interface. It is the
/// orchestration composition root on top of the three search/storage seams
/// and does not need a seam of its own.
/// </para>
/// </summary>
public sealed class HybridSearch
{
    private const double DefaultFtsWeight = 0.3;

    private readonly IVectorSearch _vectorSearch;
    private readonly IFtsSearch _ftsSearch;
    private readonly ISqliteStore _store;

    public HybridSearch(
        IVectorSearch vectorSearch,
        IFtsSearch ftsSearch,
        ISqliteStore store)
    {
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(ftsSearch);
        ArgumentNullException.ThrowIfNull(store);
        _vectorSearch = vectorSearch;
        _ftsSearch = ftsSearch;
        _store = store;
    }

    /// <summary>
    /// Run hybrid search across <paramref name="tiers"/>. Both vector and
    /// FTS seams are queried per tier/type pair with <c>topK * 2</c>; the
    /// resulting scores are fused via F# Core's <c>Ranking.hybridScore</c>,
    /// sorted descending, truncated to <see cref="HybridSearchOpts.TopK"/>,
    /// resolved to <see cref="Entry"/> rows from <see cref="ISqliteStore"/>,
    /// touched in the store, and returned with ranks <c>1..N</c>.
    /// </summary>
    public IReadOnlyList<SearchResult> Search(
        IReadOnlyList<(Tier Tier, ContentType Type)> tiers,
        string query,
        ReadOnlyMemory<float> queryEmbedding,
        HybridSearchOpts opts)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(opts);

        var ftsWeight = opts.FtsWeight ?? DefaultFtsWeight;
        var oversampledK = opts.TopK * 2;

        var scoreMap = new Dictionary<string, ScoreEntry>(StringComparer.Ordinal);

        foreach (var (tier, type) in tiers)
        {
            var vectorResults = _vectorSearch.SearchByVector(
                tier,
                type,
                queryEmbedding,
                new VectorSearchOpts(TopK: oversampledK, MinScore: opts.MinScore));

            foreach (var vr in vectorResults)
            {
                if (scoreMap.TryGetValue(vr.Id, out var existing))
                {
                    if (vr.Score > existing.VectorScore)
                    {
                        existing.VectorScore = vr.Score;
                        existing.Tier = tier;
                        existing.Type = type;
                    }
                }
                else
                {
                    scoreMap[vr.Id] = new ScoreEntry
                    {
                        VectorScore = vr.Score,
                        FtsScore = 0.0,
                        Tier = tier,
                        Type = type,
                    };
                }
            }

            var ftsResults = _ftsSearch.SearchByFts(
                tier,
                type,
                query,
                new FtsSearchOpts(TopK: oversampledK));

            foreach (var fr in ftsResults)
            {
                if (scoreMap.TryGetValue(fr.Id, out var existing))
                {
                    if (fr.Score > existing.FtsScore)
                    {
                        existing.FtsScore = fr.Score;
                    }
                }
                else
                {
                    scoreMap[fr.Id] = new ScoreEntry
                    {
                        VectorScore = 0.0,
                        FtsScore = fr.Score,
                        Tier = tier,
                        Type = type,
                    };
                }
            }
        }

        // Fuse via F# Core. This is the single source of truth for the
        // hybrid ranking formula — keep the math out of Infrastructure.
        var candidates = scoreMap
            .Select(kvp => new Candidate(
                Id: kvp.Key,
                FusedScore: Ranking.hybridScore(
                    kvp.Value.VectorScore,
                    kvp.Value.FtsScore,
                    ftsWeight),
                Tier: kvp.Value.Tier,
                Type: kvp.Value.Type))
            .OrderByDescending(c => c.FusedScore)
            .Take(opts.TopK)
            .ToList();

        var merged = new List<SearchResult>(candidates.Count);
        var rank = 1;
        foreach (var c in candidates)
        {
            var entry = _store.Get(c.Tier, c.Type, c.Id);
            if (entry is null) continue;

            _store.Update(
                c.Tier,
                c.Type,
                c.Id,
                new UpdateEntryOpts { Touch = true });

            merged.Add(new SearchResult(
                entry,
                c.Tier,
                c.Type,
                c.FusedScore,
                rank));
            rank++;
        }

        return merged;
    }

    private sealed class ScoreEntry
    {
        public double VectorScore;
        public double FtsScore;
        public Tier Tier = null!;
        public ContentType Type = null!;
    }

    private readonly record struct Candidate(
        string Id,
        double FusedScore,
        Tier Tier,
        ContentType Type);
}
