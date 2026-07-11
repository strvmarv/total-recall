using System;
using System.Collections.Generic;
using Npgsql;
using Pgvector;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Search;

/// <summary>
/// pgvector-backed implementation of <see cref="IVectorSearch"/>. Embeddings
/// are stored inline in the <c>memories</c> and <c>knowledge</c> content
/// tables rather than in separate vec tables (unlike the SQLite
/// <see cref="VectorSearch"/> layout).
///
/// <list type="bullet">
///   <item><see cref="InsertEmbedding"/> UPDATEs the <c>embedding</c> column on
///   an existing row, keyed by <c>(id, tier)</c>.</item>
///   <item><see cref="DeleteEmbedding"/> sets <c>embedding = NULL</c> on the row
///   identified by <c>internal_key</c>.</item>
///   <item><see cref="SearchByVector"/> issues a KNN query using the pgvector
///   <c>&lt;=&gt;</c> cosine-distance operator with owner/visibility scoping,
///   oversampling by <c>topK * 2</c> before applying the optional
///   <c>minScore</c> filter.</item>
/// </list>
/// </summary>
public sealed class PgvectorSearch : IVectorSearch
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _ownerId;

    public PgvectorSearch(NpgsqlDataSource dataSource, string ownerId)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        _dataSource = dataSource;
        _ownerId = ownerId;
    }

    // --- IVectorSearch ---------------------------------------------------

    /// <inheritdoc/>
    public void InsertEmbedding(
        Tier tier,
        ContentType type,
        string entryId,
        ReadOnlyMemory<float> embedding)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        var table = TableName(type);
        var tierStr = TierString(tier);

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {table} SET embedding = $1 WHERE id = $2 AND tier = $3";
        cmd.Parameters.Add(new NpgsqlParameter { Value = new Vector(embedding.ToArray()) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = entryId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = tierStr });
        var affected = cmd.ExecuteNonQuery();

        if (affected == 0)
            throw new InvalidOperationException(
                $"Entry {entryId} not found in {table} tier={tierStr}");
    }

    /// <inheritdoc/>
    public void DeleteEmbedding(Tier tier, ContentType type, long rowid)
    {
        var table = TableName(type);

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {table} SET embedding = NULL WHERE internal_key = $1";
        cmd.Parameters.Add(new NpgsqlParameter { Value = rowid });
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public IReadOnlyList<VectorSearchResult> SearchByVector(
        Tier tier,
        ContentType type,
        ReadOnlyMemory<float> queryVec,
        VectorSearchOpts opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (opts.TopK <= 0)
            throw new ArgumentOutOfRangeException(nameof(opts), "TopK must be positive");

        var table = TableName(type);
        var tierStr = TierString(tier);
        var oversample = opts.TopK * 2;
        var queryVector = new Vector(queryVec.ToArray());

        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT id, 1.0 - (embedding <=> $1::vector) AS score
FROM {table}
WHERE tier = $2 AND embedding IS NOT NULL
  AND (owner_id = $3 OR visibility IN ('team', 'public'))
ORDER BY embedding <=> $1::vector
LIMIT $4";
        cmd.Parameters.Add(new NpgsqlParameter { Value = queryVector });
        cmd.Parameters.Add(new NpgsqlParameter { Value = tierStr });
        cmd.Parameters.Add(new NpgsqlParameter { Value = _ownerId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = oversample });

        var raw = new List<VectorSearchResult>(capacity: oversample);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var score = reader.GetDouble(1);
                raw.Add(new VectorSearchResult(id, score));
            }
        }

        if (opts.MinScore is double minScore)
        {
            var filtered = new List<VectorSearchResult>(raw.Count);
            foreach (var r in raw)
            {
                if (r.Score >= minScore) filtered.Add(r);
            }
            raw = filtered;
        }

        if (raw.Count > opts.TopK)
            raw.RemoveRange(opts.TopK, raw.Count - opts.TopK);
        return raw;
    }

    /// <inheritdoc/>
    public IReadOnlyList<VectorSearchResult> SearchMultipleTiers(
        IReadOnlyList<(Tier Tier, ContentType Type)> targets,
        ReadOnlyMemory<float> queryVec,
        VectorSearchOpts opts)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(opts);

        var all = new List<VectorSearchResult>();
        foreach (var (t, ct) in targets)
        {
            all.AddRange(SearchByVector(t, ct, queryVec, opts));
        }

        all.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        if (all.Count > opts.TopK)
            all.RemoveRange(opts.TopK, all.Count - opts.TopK);
        return all;
    }

    // --- helpers ---------------------------------------------------------

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
