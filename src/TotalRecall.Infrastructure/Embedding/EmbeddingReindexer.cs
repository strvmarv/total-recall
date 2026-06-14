using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
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

    /// <summary>_meta key holding the target model the in-flight batched reindex
    /// is converging on. If it differs from the current embedder's model, the
    /// resume cursor is stale and is reset.</summary>
    internal const string CursorTargetKey = "embed.reindex.target";

    /// <summary>_meta key holding the index into <see cref="TierNames.AllTablePairs"/>
    /// of the (tier, type) pair the cursor is paused inside.</summary>
    internal const string CursorPairKey = "embed.reindex.pair";

    /// <summary>_meta key holding the last committed rowid within the cursor pair —
    /// rows with <c>rowid &lt;= this</c> are already re-embedded.</summary>
    internal const string CursorRowidKey = "embed.reindex.rowid";

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
    /// Batched, resumable re-embed. Embeds OFF the write lock; writes each batch in a
    /// short <c>BEGIN IMMEDIATE</c> txn that also advances the <c>_meta</c> cursor
    /// (cursor == committed data). Does NOT stamp the embedder fingerprint — the
    /// coordinator does that after a full pass. Returns the number of rows
    /// re-embedded by THIS invocation.
    ///
    /// Honors a <c>_meta</c> cursor (<see cref="CursorTargetKey"/>,
    /// <see cref="CursorPairKey"/>, <see cref="CursorRowidKey"/>) for resume: an
    /// interrupted run continues from the last committed batch instead of
    /// restarting. If the stored target model differs from the current embedder's
    /// model the cursor is treated as stale and reset to the start.
    ///
    /// Cancellation contract: <paramref name="ct"/> is checked at the top of every
    /// batch. A requested cancellation surfaces as <see cref="OperationCanceledException"/>
    /// AFTER the in-progress batch's commit, so the persisted cursor always points at
    /// fully-committed data and the next invocation resumes cleanly. (The coordinator
    /// treats cancellation as "resume later".)
    ///
    /// Why raw BEGIN/COMMIT/ROLLBACK and not a SqliteTransaction object:
    /// SqliteStore/VectorSearch create commands WITHOUT setting .Transaction;
    /// Microsoft.Data.Sqlite throws if a SqliteTransaction OBJECT is active and a
    /// command's .Transaction isn't set. Raw SQL transaction control does NOT create
    /// a SqliteTransaction object, so it sidesteps that enforcement.
    /// </summary>
    public static int RunBatched(
        MsSqliteConnection conn,
        IStore store,
        IVectorSearch vec,
        IEmbedder embedder,
        ReindexProgress progress,
        CancellationToken ct,
        TextWriter? log,
        int batchSize = 256)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(vec);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(progress);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "batchSize must be positive");

        var meta = (IMetaStore)store;
        var pairs = TierNames.AllTablePairs;

        // Resume from a cursor only when it targets the current model; otherwise it
        // belongs to a different (interrupted) migration and must be discarded.
        int startPair = 0;
        long startRowid = 0;
        var model = embedder.Descriptor.Model;
        if (!string.IsNullOrEmpty(model) && string.Equals(meta.GetMeta(CursorTargetKey), model, StringComparison.Ordinal))
        {
            startPair = ParseIntOr(meta.GetMeta(CursorPairKey), 0);
            startRowid = ParseLongOr(meta.GetMeta(CursorRowidKey), 0);
            // A malformed or out-of-range pair index means the cursor cannot be
            // trusted — restart the whole reindex rather than skipping rows (which
            // would let the caller stamp the fingerprint against stale vectors).
            if (startPair < 0 || startPair >= pairs.Length) { startPair = 0; startRowid = 0; }
        }
        meta.SetMeta(CursorTargetKey, model);

        int total = 0;
        for (int pi = startPair; pi < pairs.Length; pi++)
        {
            var (tier, type) = pairs[pi];
            var table = MigrationRunner.TableName(tier, type);
            long cursorRowid = (pi == startPair) ? startRowid : 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var batch = ReadBatch(conn, table, cursorRowid, batchSize);
                if (batch.Count == 0) break;

                // Embed OFF the write lock — the slow part runs without holding sqlite.
                var embedded = new (long rowid, string id, ReadOnlyMemory<float> vector)[batch.Count];
                for (int i = 0; i < batch.Count; i++)
                    embedded[i] = (batch[i].rowid, batch[i].id, embedder.Embed(batch[i].content));

                long lastRowid = batch[batch.Count - 1].rowid;

                // Short write txn: rewrite this batch's vectors AND advance the cursor
                // atomically, so a committed cursor always reflects committed data.
                Exec(conn, "BEGIN IMMEDIATE");
                try
                {
                    int wrote = 0;
                    foreach (var (rowid, id, vector) in embedded)
                    {
                        vec.DeleteEmbedding(tier, type, rowid);
                        try
                        {
                            vec.InsertEmbedding(tier, type, id, vector);
                            wrote++;
                        }
                        catch (InvalidOperationException) when (store.GetInternalKey(tier, type, id) is null)
                        {
                            // Row was deleted between the read and the write — skip it.
                            // A still-present entry rethrows (triggering the batch ROLLBACK)
                            // so a real failure never silently loses a vector.
                        }
                    }

                    meta.SetMeta(CursorPairKey, pi.ToString(CultureInfo.InvariantCulture));
                    meta.SetMeta(CursorRowidKey, lastRowid.ToString(CultureInfo.InvariantCulture));
                    Exec(conn, "COMMIT");

                    total += wrote;
                    progress.Advance(wrote);
                }
                catch
                {
                    try { Exec(conn, "ROLLBACK"); } catch { /* best-effort */ }
                    throw;
                }

                cursorRowid = lastRowid;
            }
        }

        log?.WriteLine($"[total-recall] re-embedded {total} entries (batched).");
        return total;
    }

    // --- helpers ----------------------------------------------------------

    /// <summary>
    /// Page the next <paramref name="n"/> content rows with <c>rowid &gt;
    /// <paramref name="cursorRowid"/></c>, ordered by rowid. <paramref name="table"/>
    /// comes from the static <see cref="MigrationRunner.TableName"/> map (never user
    /// input), so interpolating it into the SQL is safe.
    /// </summary>
    private static List<(long rowid, string id, string content)> ReadBatch(
        MsSqliteConnection conn, string table, long cursorRowid, int n)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT rowid, id, content FROM {table} WHERE rowid > $cur ORDER BY rowid LIMIT $n";
        cmd.Parameters.AddWithValue("$cur", cursorRowid);
        cmd.Parameters.AddWithValue("$n", n);

        var rows = new List<(long, string, string)>(capacity: n);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static void Exec(MsSqliteConnection conn, string sql)
    {
        using var c = conn.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static int ParseIntOr(string? s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static long ParseLongOr(string? s, long fallback) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
