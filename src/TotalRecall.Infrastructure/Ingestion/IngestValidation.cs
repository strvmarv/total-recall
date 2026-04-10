using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Ingestion;

/// <summary>
/// Per-probe outcome from <see cref="IngestValidator.ValidateChunks"/>: the
/// chunk index that was probed, the best score observed among results scoped
/// to the target collection, and whether that score cleared the pass
/// threshold.
/// </summary>
public sealed record ProbeResult(int ChunkIndex, double Score, bool Passed);

/// <summary>
/// Aggregate result of a chunk-validation run. <see cref="Passed"/> is true
/// iff every probe passed.
/// </summary>
public sealed record ValidationResult(bool Passed, IReadOnlyList<ProbeResult> Probes);

/// <summary>
/// Post-ingest sanity check: embeds a handful of the chunks we just wrote and
/// confirms that a cold/knowledge vector search can find them back within the
/// same collection with a score above <c>PROBE_MIN_SCORE</c>. Ports
/// <c>src-ts/ingestion/ingest-validation.ts</c> line-for-line, including the
/// (largely dead) <c>parent_id === collectionId</c> branch in the scope filter
/// so TS and .NET produce identical results against identical databases.
/// </summary>
public sealed class IngestValidator
{
    private const double ProbeMinScore = 0.5;
    private const int ProbeTopK = 3;

    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;
    private readonly IStore _store;

    public IngestValidator(IEmbedder embedder, IVectorSearch vectorSearch, IStore store)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(store);
        _embedder = embedder;
        _vectorSearch = vectorSearch;
        _store = store;
    }

    /// <summary>
    /// Indices into a chunk array to probe. For <c>&lt;= 3</c> chunks, all of
    /// them; otherwise the triple <c>[0, N/3, 2N/3]</c>.
    /// </summary>
    public static IReadOnlyList<int> SelectProbeIndices(int totalChunks)
    {
        if (totalChunks <= 0) return Array.Empty<int>();
        if (totalChunks <= 3)
        {
            var all = new int[totalChunks];
            for (var i = 0; i < totalChunks; i++) all[i] = i;
            return all;
        }
        return new[] { 0, totalChunks / 3, (2 * totalChunks) / 3 };
    }

    /// <summary>
    /// Embed each selected probe chunk, run a cold/knowledge KNN query, and
    /// filter results to rows in <paramref name="collectionId"/>. Passes if
    /// every probe's best in-scope score exceeds <c>PROBE_MIN_SCORE</c>.
    /// </summary>
    public ValidationResult ValidateChunks(IReadOnlyList<string> chunkContents, string collectionId)
    {
        ArgumentNullException.ThrowIfNull(chunkContents);
        ArgumentNullException.ThrowIfNull(collectionId);

        var indices = SelectProbeIndices(chunkContents.Count);
        var probes = new List<ProbeResult>(indices.Count);

        foreach (var idx in indices)
        {
            var queryVec = _embedder.Embed(chunkContents[idx]);
            var results = _vectorSearch.SearchByVector(
                Tier.Cold,
                ContentType.Knowledge,
                queryVec,
                new VectorSearchOpts(TopK: ProbeTopK * 3, MinScore: 0));

            double bestScore = 0;
            var hasAny = false;
            foreach (var r in results)
            {
                if (!TryGetScope(r.Id, out var collId, out var parentId)) continue;
                // TS parity: `parent_id === collectionId` branch is effectively
                // dead (chunks parent doc, not collection) but preserved for
                // bit-for-bit behavioural match with the TS reference.
                if (collId == collectionId || parentId == collectionId)
                {
                    if (!hasAny || r.Score > bestScore) bestScore = r.Score;
                    hasAny = true;
                }
            }

            probes.Add(new ProbeResult(idx, bestScore, bestScore > ProbeMinScore));
        }

        var passed = probes.All(p => p.Passed);
        return new ValidationResult(passed, probes);
    }

    private bool TryGetScope(string id, out string? collectionId, out string? parentId)
    {
        var entry = _store.Get(Tier.Cold, ContentType.Knowledge, id);
        if (entry is null)
        {
            collectionId = null;
            parentId = null;
            return false;
        }
        collectionId = Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(entry.CollectionId)
            ? entry.CollectionId.Value : null;
        parentId = Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(entry.ParentId)
            ? entry.ParentId.Value : null;
        return true;
    }
}
