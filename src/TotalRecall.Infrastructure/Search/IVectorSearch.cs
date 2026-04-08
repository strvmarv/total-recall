using System;
using System.Collections.Generic;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Search;

/// <summary>
/// Result of a vector KNN lookup: the entry id and a score in
/// <c>[0, 1]</c>-ish range where higher is better. See
/// <see cref="IVectorSearch.SearchByVector"/> for the exact score semantics.
/// </summary>
public sealed record VectorSearchResult(string Id, double Score);

/// <summary>
/// Options for a vector KNN query. Mirrors <c>VectorSearchOpts</c> in
/// <c>src-ts/search/vector-search.ts</c>.
/// </summary>
public sealed record VectorSearchOpts(int TopK, double? MinScore = null);

/// <summary>
/// Vector search seam over the <c>vec0</c> virtual tables created in
/// Migration 1 of <see cref="TotalRecall.Infrastructure.Storage.MigrationRunner"/>.
/// Ports <c>src-ts/search/vector-search.ts</c>.
///
/// Embeddings are passed as <see cref="ReadOnlyMemory{T}"/> rather than
/// <see cref="ReadOnlySpan{T}"/> because interface methods in net8.0 cannot
/// take ref-struct parameters; <see cref="ReadOnlyMemory{T}"/> keeps the
/// zero-copy ambition while remaining AOT-friendly.
/// </summary>
public interface IVectorSearch
{
    /// <summary>
    /// Resolve <paramref name="entryId"/> to a rowid in the content table and
    /// insert the corresponding embedding into the vec0 virtual table.
    /// Throws <see cref="InvalidOperationException"/> if the entry does not
    /// exist.
    /// </summary>
    void InsertEmbedding(
        Tier tier,
        ContentType type,
        string entryId,
        ReadOnlyMemory<float> embedding);

    /// <summary>
    /// Remove the embedding for <paramref name="entryId"/>. Silent no-op if
    /// the entry (or its embedding row) does not exist.
    /// </summary>
    void DeleteEmbedding(Tier tier, ContentType type, string entryId);

    /// <summary>
    /// KNN query against a single (tier, type) vec0 table. Oversamples by
    /// <c>topK * 2</c> before applying the optional <c>minScore</c> filter,
    /// then truncates to <c>topK</c>. Score is <c>1 - distance</c> where
    /// distance is the default vec0 metric (L2-squared) — negative scores
    /// are possible for sufficiently far vectors.
    /// </summary>
    IReadOnlyList<VectorSearchResult> SearchByVector(
        Tier tier,
        ContentType type,
        ReadOnlyMemory<float> queryVec,
        VectorSearchOpts opts);

    /// <summary>
    /// Run <see cref="SearchByVector"/> across multiple (tier, type) targets,
    /// merge the results, re-sort by score descending, and truncate to
    /// <c>topK</c>.
    /// </summary>
    IReadOnlyList<VectorSearchResult> SearchMultipleTiers(
        IReadOnlyList<(Tier Tier, ContentType Type)> targets,
        ReadOnlyMemory<float> queryVec,
        VectorSearchOpts opts);
}
