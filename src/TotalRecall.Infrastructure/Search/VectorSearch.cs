using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Search;

/// <summary>
/// sqlite-vec-backed implementation of <see cref="IVectorSearch"/>. Ports
/// <c>src-ts/search/vector-search.ts</c> line-for-line, including the
/// <c>oversample = topK * 2</c> prefetch and the
/// <c>WHERE embedding MATCH ? AND k = ?</c> KNN-constraint syntax required by
/// vec0 (a plain <c>LIMIT</c> is rejected by the virtual table).
///
/// The connection is borrowed, not owned — disposal is the caller's
/// responsibility. Plan 4's composition root holds a single long-lived
/// connection shared between <see cref="SqliteStore"/>,
/// <see cref="VectorSearch"/>, and the upcoming FTS / hybrid searchers.
/// </summary>
public sealed class VectorSearch : IVectorSearch
{
    private readonly MsSqliteConnection _conn;

    public VectorSearch(MsSqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _conn = connection;
    }

    public void InsertEmbedding(
        Tier tier,
        ContentType type,
        string entryId,
        ReadOnlyMemory<float> embedding)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        var contentTable = MigrationRunner.TableName(tier, type);
        var vecTable = MigrationRunner.VecTableName(tier, type);

        var rowid = ResolveRowid(contentTable, entryId);
        if (rowid is null)
        {
            throw new InvalidOperationException(
                $"Entry {entryId} not found in {contentTable}");
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {vecTable} (rowid, embedding) VALUES ($rowid, $embedding)";
        cmd.Parameters.AddWithValue("$rowid", rowid.Value);
        cmd.Parameters.AddWithValue("$embedding", EmbeddingToBytes(embedding));
        cmd.ExecuteNonQuery();
    }

    public void DeleteEmbedding(Tier tier, ContentType type, string entryId)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        var contentTable = MigrationRunner.TableName(tier, type);
        var vecTable = MigrationRunner.VecTableName(tier, type);

        var rowid = ResolveRowid(contentTable, entryId);
        if (rowid is null) return;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {vecTable} WHERE rowid = $rowid";
        cmd.Parameters.AddWithValue("$rowid", rowid.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<VectorSearchResult> SearchByVector(
        Tier tier,
        ContentType type,
        ReadOnlyMemory<float> queryVec,
        VectorSearchOpts opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (opts.TopK <= 0)
            throw new ArgumentOutOfRangeException(nameof(opts), "TopK must be positive");

        var contentTable = MigrationRunner.TableName(tier, type);
        var vecTable = MigrationRunner.VecTableName(tier, type);
        var oversample = opts.TopK * 2;

        using var cmd = _conn.CreateCommand();
        // vec0 KNN syntax: topK is passed via `k = ?` in the WHERE clause,
        // NOT via LIMIT. A plain LIMIT is rejected by the virtual table. The
        // INNER JOIN maps the vec rowid back to the content table's string id.
        cmd.CommandText = $@"
SELECT c.id, v.distance AS dist
FROM {vecTable} v
INNER JOIN {contentTable} c ON c.rowid = v.rowid
WHERE v.embedding MATCH $vec AND k = $k
ORDER BY v.distance ASC";
        cmd.Parameters.AddWithValue("$vec", EmbeddingToBytes(queryVec));
        cmd.Parameters.AddWithValue("$k", oversample);

        var raw = new List<VectorSearchResult>(capacity: oversample);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var dist = reader.GetDouble(1);
                raw.Add(new VectorSearchResult(id, 1.0 - dist));
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

    public IReadOnlyList<VectorSearchResult> SearchMultipleTiers(
        IReadOnlyList<(Tier Tier, ContentType Type)> targets,
        ReadOnlyMemory<float> queryVec,
        VectorSearchOpts opts)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(opts);

        var all = new List<VectorSearchResult>();
        foreach (var (tier, type) in targets)
        {
            all.AddRange(SearchByVector(tier, type, queryVec, opts));
        }

        all.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        if (all.Count > opts.TopK)
            all.RemoveRange(opts.TopK, all.Count - opts.TopK);
        return all;
    }

    // --- helpers ----------------------------------------------------------

    private long? ResolveRowid(string contentTable, string entryId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT rowid FROM {contentTable} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", entryId);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : null;
    }

    /// <summary>
    /// Reinterpret a <see cref="ReadOnlyMemory{T}"/> of float as its raw IEEE
    /// 754 little-endian byte layout. vec0 expects exactly
    /// <c>dim * 4</c> bytes — 1536 for the 384-dim MiniLM vectors this
    /// project uses. Microsoft.Data.Sqlite parameters do not accept
    /// <c>ReadOnlySpan&lt;byte&gt;</c>, so we materialize to a byte[].
    /// </summary>
    private static byte[] EmbeddingToBytes(ReadOnlyMemory<float> v) =>
        MemoryMarshal.AsBytes(v.Span).ToArray();
}
