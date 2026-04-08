using System;
using System.Collections.Generic;
using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Search;

/// <summary>
/// FTS5-backed implementation of <see cref="IFtsSearch"/>. Ports
/// <c>src-ts/search/fts-search.ts</c> line-for-line, including:
/// <list type="bullet">
///   <item>The phrase-query sanitizer (split on whitespace, wrap each
///     word in double quotes, escape internal quotes by doubling).</item>
///   <item>The graceful fallback to an empty result set when the FTS
///     virtual table is missing (pre-migration DB).</item>
///   <item>The min-max score normalization over flipped-sign BM25
///     ranks: <c>score = (−rank − min) / (max − min)</c>, with
///     <c>1.0</c> for the degenerate single-row / all-equal case.</item>
/// </list>
///
/// The connection is borrowed, not owned - disposal is the caller's
/// responsibility. Plan 4's composition root holds a single long-lived
/// connection shared between <see cref="SqliteStore"/>,
/// <see cref="VectorSearch"/>, and this FTS searcher.
/// </summary>
public sealed class FtsSearch : IFtsSearch
{
    private readonly MsSqliteConnection _conn;

    public FtsSearch(MsSqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _conn = connection;
    }

    public IReadOnlyList<FtsSearchResult> SearchByFts(
        Tier tier,
        ContentType type,
        string query,
        FtsSearchOpts opts)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(opts);
        if (opts.TopK <= 0)
            throw new ArgumentOutOfRangeException(nameof(opts), "TopK must be positive");

        var contentTable = MigrationRunner.TableName(tier, type);
        var ftsTable = MigrationRunner.FtsTableName(tier, type);

        // Graceful fallback: if the FTS virtual table is missing (e.g. an
        // older DB that predates Migration 3), return an empty list rather
        // than throwing. Matches the TS reference behavior.
        using (var checkCmd = _conn.CreateCommand())
        {
            checkCmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name = $name";
            checkCmd.Parameters.AddWithValue("$name", ftsTable);
            if (checkCmd.ExecuteScalar() is null)
                return Array.Empty<FtsSearchResult>();
        }

        var sanitized = SanitizeFtsQuery(query);
        if (sanitized.Length == 0)
            return Array.Empty<FtsSearchResult>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
SELECT c.id, fts.rank AS bm25_rank
FROM {ftsTable} fts
INNER JOIN {contentTable} c ON c.rowid = fts.rowid
WHERE {ftsTable} MATCH $query
ORDER BY fts.rank
LIMIT $k";
        cmd.Parameters.AddWithValue("$query", sanitized);
        cmd.Parameters.AddWithValue("$k", opts.TopK);

        var rows = new List<(string Id, double NegRank)>(capacity: opts.TopK);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var rawRank = reader.GetDouble(1);
                // FTS5 rank is negative BM25 (lower = better match). Flip
                // the sign so that "higher is better" matches our score
                // convention and aligns with VectorSearch.
                rows.Add((id, -rawRank));
            }
        }

        if (rows.Count == 0)
            return Array.Empty<FtsSearchResult>();

        var min = rows[0].NegRank;
        var max = rows[0].NegRank;
        for (var i = 1; i < rows.Count; i++)
        {
            var v = rows[i].NegRank;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var range = max - min;
        var results = new List<FtsSearchResult>(rows.Count);
        foreach (var (id, negRank) in rows)
        {
            var score = range > 0 ? (negRank - min) / range : 1.0;
            results.Add(new FtsSearchResult(id, score));
        }
        return results;
    }

    /// <summary>
    /// Escape a raw user query into FTS5 phrase-query syntax. Each
    /// whitespace-separated token is wrapped in double quotes, and any
    /// internal double quotes are escaped by doubling (FTS5's convention).
    /// Empty / whitespace-only input returns the empty string.
    ///
    /// Mirrors <c>sanitizeFtsQuery</c> in
    /// <c>src-ts/search/fts-search.ts</c>.
    /// </summary>
    internal static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        // Null delimiter + RemoveEmptyEntries splits on any run of
        // whitespace, equivalent to the TS `split(/\s+/).filter(Boolean)`.
        var words = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return string.Empty;

        var sb = new StringBuilder(query.Length + (words.Length * 3));
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append('"');
            foreach (var c in words[i])
            {
                if (c == '"') sb.Append('"');
                sb.Append(c);
            }
            sb.Append('"');
        }
        return sb.ToString();
    }
}
