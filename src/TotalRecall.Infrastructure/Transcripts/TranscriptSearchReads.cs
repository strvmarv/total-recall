using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// The filters of one search, applied inside both candidate queries. <paramref name="SinceEpoch"/> compares with the
/// chunk time as epoch seconds (a chunk of unknown time is not found by it); <paramref name="InteractiveOnly"/> keeps
/// sessions of kind <c>interactive</c>.
/// </summary>
public sealed record ChunkFilter(long? SessionId, string? Project, long? SinceEpoch, bool InteractiveOnly);

/// <summary>A nearest-neighbour hit: the chunk id and its cosine similarity (<c>1 - distance</c>).</summary>
public sealed record VectorHit(long ChunkId, double Similarity);

/// <summary>A full-text hit: the chunk id and its <c>bm25</c> value (lower, more negative, is better).</summary>
public sealed record TextHit(long ChunkId, double Bm25);

/// <summary>A chunk with the fields of its session that a result shows. <paramref name="SessionRef"/> is <c>&lt;host&gt;:&lt;host_session_id&gt;</c>.</summary>
public sealed record ChunkRow(
    long Id,
    long SessionId,
    string SessionRef,
    string Project,
    string Kind,
    string? Title,
    string? Ts,
    string ChunkType,
    bool HasError,
    int ExchangeStart,
    int ExchangeEnd,
    string Content);

/// <summary>
/// The reads of one search, run inside a single deferred read transaction so a commit by another connection between the
/// vector candidates, the word candidates, the counts and the chunk rows cannot pair a reused chunk id with the wrong
/// text. Dispose ends the transaction.
/// </summary>
public interface ITranscriptSearchReads : IDisposable
{
    /// <summary>The session row id of <c>&lt;host&gt;:&lt;hostSessionId&gt;</c>, or null when there is none.</summary>
    long? ResolveSession(string host, string hostSessionId);

    /// <summary>
    /// Up to <paramref name="k"/> nearest chunks by cosine similarity, best first, ties by chunk id; the filter applies
    /// inside the search.
    /// </summary>
    /// <exception cref="ArgumentException">The query is not 384 floats.</exception>
    IReadOnlyList<VectorHit> Vector(float[] query, ChunkFilter filter, int k);

    /// <summary>Up to <paramref name="limit"/> chunks matching the FTS5 <paramref name="match"/> string, best bm25 first, ties by chunk id.</summary>
    IReadOnlyList<TextHit> Text(string match, ChunkFilter filter, int limit);

    /// <summary>Chunks in scope; with <paramref name="onlyUnembedded"/> only those with <c>embedded = 0</c>.</summary>
    long CountInScope(ChunkFilter filter, bool onlyUnembedded);

    /// <summary>True when the archive holds at least one chunk (an <c>EXISTS</c>, not a count).</summary>
    bool AnyChunk();

    /// <summary>The rows of the given chunk ids that still exist, by id.</summary>
    IReadOnlyDictionary<long, ChunkRow> Chunks(IReadOnlyCollection<long> ids);
}

/// <summary>The store's implementation: borrows the store's connection for the life of one deferred transaction.</summary>
internal sealed class SqliteTranscriptSearchReads : ITranscriptSearchReads
{
    private const int VectorDimensions = 384;
    private const int IdBatch = 500;

    private const string SinceSql = "CAST(strftime('%s', c.ts) AS INTEGER) >= $since";

    private readonly SqliteConnection _conn;
    private SqliteTransaction? _tx;

    public SqliteTranscriptSearchReads(SqliteConnection conn)
    {
        _conn = conn;
        _tx = conn.BeginTransaction(deferred: true);
    }

    public long? ResolveSession(string host, string hostSessionId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostSessionId);
        using var cmd = Command("SELECT id FROM tx_session WHERE host = $h AND host_session_id = $s");
        cmd.Parameters.AddWithValue("$h", host);
        cmd.Parameters.AddWithValue("$s", hostSessionId);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    public IReadOnlyList<VectorHit> Vector(float[] query, ChunkFilter filter, int k)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        if (query.Length != VectorDimensions)
            throw new ArgumentException($"The query vector has {query.Length} floats; the archive holds {VectorDimensions}.", nameof(query));
        if (k <= 0)
            return Array.Empty<VectorHit>();

        // The metadata columns of tx_chunk_vec filter inside the nearest-neighbour search (spec section 1).
        var sql = new StringBuilder("SELECT rowid, distance FROM tx_chunk_vec WHERE embedding MATCH $q AND k = $k");
        if (filter.SessionId is not null) sql.Append(" AND session_id = $session");
        if (filter.Project is not null) sql.Append(" AND project = $project");
        if (filter.InteractiveOnly) sql.Append(" AND kind = 'interactive'");
        if (filter.SinceEpoch is not null) sql.Append(" AND ts >= $since");
        // vec0 accepts no second ORDER BY term, so ties are broken below
        sql.Append(" ORDER BY distance");

        using var cmd = Command(sql.ToString());
        cmd.Parameters.AddWithValue("$q", MemoryMarshal.AsBytes(new ReadOnlySpan<float>(query)).ToArray());
        cmd.Parameters.AddWithValue("$k", (long)k);
        BindVectorFilter(cmd, filter);

        var hits = new List<VectorHit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            hits.Add(new VectorHit(r.GetInt64(0), 1.0 - r.GetDouble(1)));

        // vec0 returns equal distances in no stated order (observed: descending rowid); the chunk id breaks ties so
        // the order is the same every time. Which tied rows a k cut keeps is still vec0's choice.
        hits.Sort(static (a, b) =>
        {
            var bySimilarity = b.Similarity.CompareTo(a.Similarity);
            return bySimilarity != 0 ? bySimilarity : a.ChunkId.CompareTo(b.ChunkId);
        });
        return hits;
    }

    public IReadOnlyList<TextHit> Text(string match, ChunkFilter filter, int limit)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(filter);
        if (limit <= 0)
            return Array.Empty<TextHit>();

        var sql = new StringBuilder(
            "SELECT c.id, bm25(tx_chunk_fts) AS score FROM tx_chunk_fts " +
            "JOIN tx_chunk c ON c.id = tx_chunk_fts.rowid " +
            "JOIN tx_session s ON s.id = c.session_id " +
            "WHERE tx_chunk_fts MATCH $match");
        AppendScope(sql, filter);
        // the chunk id breaks ties so equal scores (and the cut a limit makes inside them) are the same every time
        sql.Append(" ORDER BY score, c.id LIMIT $limit");

        using var cmd = Command(sql.ToString());
        cmd.Parameters.AddWithValue("$match", match);
        cmd.Parameters.AddWithValue("$limit", (long)limit);
        BindScope(cmd, filter);

        var hits = new List<TextHit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            hits.Add(new TextHit(r.GetInt64(0), r.GetDouble(1)));
        return hits;
    }

    public long CountInScope(ChunkFilter filter, bool onlyUnembedded)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var sql = new StringBuilder("SELECT count(*) FROM tx_chunk c JOIN tx_session s ON s.id = c.session_id WHERE 1 = 1");
        if (onlyUnembedded) sql.Append(" AND c.embedded = 0");
        AppendScope(sql, filter);

        using var cmd = Command(sql.ToString());
        BindScope(cmd, filter);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public bool AnyChunk()
    {
        using var cmd = Command("SELECT EXISTS (SELECT 1 FROM tx_chunk)");
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    public IReadOnlyDictionary<long, ChunkRow> Chunks(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = new Dictionary<long, ChunkRow>();
        var list = new List<long>(ids);
        for (var offset = 0; offset < list.Count; offset += IdBatch)
        {
            var take = Math.Min(IdBatch, list.Count - offset);
            var sql = new StringBuilder(ChunkRows.Select + " WHERE c.id IN (");
            using var cmd = Command(string.Empty);
            for (var i = 0; i < take; i++)
            {
                var name = "$i" + i.ToString(CultureInfo.InvariantCulture);
                if (i > 0) sql.Append(", ");
                sql.Append(name);
                cmd.Parameters.AddWithValue(name, list[offset + i]);
            }
            sql.Append(')');
            cmd.CommandText = sql.ToString();

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var chunk = ChunkRows.Read(r);
                result[chunk.Id] = chunk;
            }
        }
        return result;
    }

    public void Dispose()
    {
        var tx = _tx;
        _tx = null;
        // A read-only transaction: ending it is a rollback and writes nothing.
        tx?.Dispose();
    }

    // The text and count queries filter on the chunk and session rows; the vector query on the table's own columns.
    private static void AppendScope(StringBuilder sql, ChunkFilter f)
    {
        if (f.SessionId is not null) sql.Append(" AND c.session_id = $session");
        if (f.Project is not null) sql.Append(" AND s.project_key = $project");
        if (f.InteractiveOnly) sql.Append(" AND s.kind = 'interactive'");
        if (f.SinceEpoch is not null) sql.Append(" AND ").Append(SinceSql);
    }

    private static void BindScope(SqliteCommand cmd, ChunkFilter f) => BindVectorFilter(cmd, f);

    private static void BindVectorFilter(SqliteCommand cmd, ChunkFilter f)
    {
        if (f.SessionId is { } session) cmd.Parameters.AddWithValue("$session", session);
        if (f.Project is { } project) cmd.Parameters.AddWithValue("$project", project);
        if (f.SinceEpoch is { } since) cmd.Parameters.AddWithValue("$since", since);
    }

    private SqliteCommand Command(string sql)
    {
        var tx = _tx ?? throw new ObjectDisposedException(nameof(ITranscriptSearchReads));
        var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }
}
