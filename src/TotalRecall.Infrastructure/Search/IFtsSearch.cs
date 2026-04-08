using System.Collections.Generic;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Search;

/// <summary>
/// Result of an FTS5 lookup: the entry id and a score in <c>[0, 1]</c>
/// where higher is better. Scores are produced by flipping the sign of
/// FTS5's negative BM25 <c>rank</c> and min-max normalizing across the
/// returned rows. See <see cref="IFtsSearch.SearchByFts"/>.
/// </summary>
public sealed record FtsSearchResult(string Id, double Score);

/// <summary>
/// Options for an FTS5 query. Mirrors <c>FtsSearchOpts</c> in
/// <c>src-ts/search/fts-search.ts</c>.
/// </summary>
public sealed record FtsSearchOpts(int TopK);

/// <summary>
/// FTS5 full-text search seam over the <c>{tbl}_fts</c> virtual tables
/// created in Migration 3 of
/// <see cref="TotalRecall.Infrastructure.Storage.MigrationRunner"/>.
/// Ports <c>src-ts/search/fts-search.ts</c>.
/// </summary>
public interface IFtsSearch
{
    /// <summary>
    /// Run a single-(tier, type) FTS5 query. The raw query string is
    /// sanitized into FTS5 phrase-query syntax (each whitespace-separated
    /// word is wrapped in double quotes, internal quotes doubled) before
    /// being passed to <c>MATCH</c>. Scores are flipped from FTS5's
    /// negative BM25 <c>rank</c> and min-max normalized to <c>[0, 1]</c>;
    /// if all rows share a rank, every score is <c>1.0</c>.
    ///
    /// Returns an empty list if the FTS virtual table does not exist
    /// (pre-migration DB) or if <paramref name="query"/> is empty/whitespace.
    /// </summary>
    IReadOnlyList<FtsSearchResult> SearchByFts(
        Tier tier,
        ContentType type,
        string query,
        FtsSearchOpts opts);
}
