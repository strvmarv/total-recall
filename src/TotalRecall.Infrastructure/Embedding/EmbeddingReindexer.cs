using System;
using System.IO;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Re-embeds every stored content row in place with a (new) embedder and
/// replaces its vec0 row. Backend-agnostic over <see cref="IStore"/> +
/// <see cref="IVectorSearch"/>. Documents use the symmetric <see cref="IEmbedder.Embed"/>
/// path (no query prefix).
/// </summary>
public sealed class EmbeddingReindexer
{
    private readonly IStore _store;
    private readonly IVectorSearch _vectors;
    private readonly IEmbedder _embedder;

    public EmbeddingReindexer(IStore store, IVectorSearch vectors, IEmbedder embedder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vectors = vectors ?? throw new ArgumentNullException(nameof(vectors));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    /// <summary>Re-embed every row; returns the number of vectors rewritten.</summary>
    public int Reindex(TextWriter? progress)
    {
        int total = 0;
        foreach (var (tier, type) in TierNames.AllTablePairs)
        {
            var rows = _store.List(tier, type);
            int inPair = 0;
            foreach (var entry in rows)
            {
                var vec = _embedder.Embed(entry.Content);
                var rowid = _store.GetInternalKey(tier, type, entry.Id);
                if (rowid is not null)
                    _vectors.DeleteEmbedding(tier, type, rowid.Value);
                _vectors.InsertEmbedding(tier, type, entry.Id, vec);
                inPair++;
                total++;
            }
            if (inPair > 0)
                progress?.WriteLine($"  {TierNames.TierName(tier)}/{TierNames.ContentTypeName(type)}: {inPair} re-embedded");
        }
        return total;
    }

    /// <summary>
    /// Atomically re-embed every row with <paramref name="embedder"/> and re-stamp
    /// the embedder fingerprint, on a single sqlite connection. Returns the number
    /// of vectors re-embedded. A mid-run failure rolls the whole thing back so the
    /// DB never ends up half-rewritten (a mix of old and new embedding spaces).
    ///
    /// Shared by the <c>reindex-embeddings</c> CLI command and the sqlite startup
    /// auto-migration path so both get identical atomicity.
    ///
    /// SqliteStore/VectorSearch create commands WITHOUT setting .Transaction;
    /// Microsoft.Data.Sqlite throws if a SqliteTransaction OBJECT is active and a
    /// command's .Transaction isn't set. Raw SQL transaction control
    /// (BEGIN/COMMIT/ROLLBACK) does NOT create a SqliteTransaction object, so it
    /// sidesteps that enforcement.
    /// </summary>
    public static int RunAtomicSqlite(
        MsSqliteConnection conn,
        IStore store,
        IVectorSearch vec,
        IEmbedder embedder,
        TextWriter? progress)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(vec);
        ArgumentNullException.ThrowIfNull(embedder);

        void Exec(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        Exec("BEGIN IMMEDIATE");
        try
        {
            var reindexer = new EmbeddingReindexer(store, vec, embedder);
            int n = reindexer.Reindex(progress);
            EmbedderFingerprint.Restamp((IMetaStore)store, embedder);
            Exec("COMMIT");
            return n;
        }
        catch
        {
            try { Exec("ROLLBACK"); } catch { /* best-effort */ }
            throw;
        }
    }
}
