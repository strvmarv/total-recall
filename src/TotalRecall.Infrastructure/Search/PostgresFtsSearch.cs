using System;
using System.Collections.Generic;
using System.Text;
using Npgsql;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Search;

/// <summary>
/// Postgres full-text search implementation of <see cref="IFtsSearch"/> using
/// <c>tsvector</c>/<c>tsquery</c> with <c>ts_rank</c>.
///
/// <list type="bullet">
///   <item>Each content table (<c>memories</c>, <c>knowledge</c>) has a
///   generated <c>fts tsvector</c> column and a GIN index — no separate FTS
///   tables are required.</item>
///   <item>User queries are sanitized into Postgres tsquery AND-expression
///   syntax (tokens joined with <c>&amp;</c>, single quotes doubled).</item>
///   <item>Scores from <c>ts_rank</c> are min-max normalized to
///   <c>[0, 1]</c>; if all rows share a rank, every score is
///   <c>1.0</c>.</item>
/// </list>
///
/// The <see cref="NpgsqlDataSource"/> is borrowed, not owned — disposal is
/// the caller's responsibility.
/// </summary>
public sealed class PostgresFtsSearch : IFtsSearch
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _ownerId;

    public PostgresFtsSearch(NpgsqlDataSource dataSource, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        _dataSource = dataSource;
        _ownerId = ownerId;
    }

    /// <inheritdoc/>
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

        var sanitized = SanitizeFtsQuery(query);
        if (sanitized.Length == 0)
            return Array.Empty<FtsSearchResult>();

        var table = TableName(type);
        var tierStr = TierString(tier);

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT id, ts_rank(fts, to_tsquery('english', $1)) AS rank
FROM {table}
WHERE tier = $2 AND fts @@ to_tsquery('english', $1)
  AND (owner_id = $3 OR visibility IN ('team', 'public'))
ORDER BY rank DESC
LIMIT $4";
        cmd.Parameters.Add(new NpgsqlParameter { Value = sanitized });
        cmd.Parameters.Add(new NpgsqlParameter { Value = tierStr });
        cmd.Parameters.Add(new NpgsqlParameter { Value = _ownerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = opts.TopK });

        var rows = new List<(string Id, double Rank)>(capacity: opts.TopK);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var rank = reader.GetDouble(1);
                rows.Add((id, rank));
            }
        }

        if (rows.Count == 0)
            return Array.Empty<FtsSearchResult>();

        // Min-max normalize ts_rank scores to [0, 1].
        var min = rows[0].Rank;
        var max = rows[0].Rank;
        for (var i = 1; i < rows.Count; i++)
        {
            var v = rows[i].Rank;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var range = max - min;
        var results = new List<FtsSearchResult>(rows.Count);
        foreach (var (id, rank) in rows)
        {
            var score = range > 0 ? (rank - min) / range : 1.0;
            results.Add(new FtsSearchResult(id, score));
        }
        return results;
    }

    /// <summary>
    /// Converts a raw user query to Postgres tsquery AND-expression syntax.
    /// Each whitespace-separated token has its single quotes doubled and the
    /// tokens are joined with <c> &amp; </c>. Returns the empty string for
    /// empty/whitespace-only input.
    /// </summary>
    internal static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        var words = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return string.Empty;

        var sb = new StringBuilder(query.Length + (words.Length * 3));
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0) sb.Append(" & ");
            foreach (var c in words[i])
            {
                if (c == '\'') sb.Append('\'');
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // --- helpers -------------------------------------------------------------

    private static string TableName(ContentType type)
    {
        if (type.IsMemory) return "memories";
        if (type.IsKnowledge) return "knowledge";
        throw new ArgumentOutOfRangeException(nameof(type));
    }

    private static string TierString(Tier tier)
    {
        if (tier.IsHot) return "hot";
        if (tier.IsWarm) return "warm";
        if (tier.IsCold) return "cold";
        throw new ArgumentOutOfRangeException(nameof(tier));
    }
}
