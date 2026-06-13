using System;
using System.IO;
using System.Linq;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Startup embedder-compatibility policy. Encapsulates what happens at store
/// open when the configured embedder no longer matches the fingerprint stamped
/// in <c>_meta</c>, honoring <c>embedding.on_model_change</c>
/// (<see cref="OnModelChange"/>: auto | warn | block).
///
/// This is the testable seam ServerComposition delegates to. The contract
/// (identical for both backends):
/// <list type="table">
///   <item><term>Match</term><description>no-op.</description></item>
///   <item><term>Unstamped + EMPTY index</term><description>stamp the configured
///   embedder (fresh DB — no vectors to migrate).</description></item>
///   <item><term>Unstamped + POPULATED index</term><description>routes into the
///   SAME mode dispatch as Mismatch — the existing vectors came from an unknown
///   prior model and must be re-embedded (auto) / nagged about (warn) / blocked.
///   There is no <c>stored</c> descriptor, so messages say "existing vectors have
///   no embedder fingerprint" instead of "X -&gt; Y".</description></item>
///   <item><term>Mismatch</term><description>dispatch on policy.</description></item>
/// </list>
///
/// The "Unstamped + populated" branch closes a silent-data-loss window: a cortex
/// DB was never fingerprint-stamped (stamping was wired only into the sqlite /
/// postgres paths), so after a model swap it ends up unstamped-but-populated with
/// stale vectors. Treating that as "fresh → stamp only" would freeze a mixed
/// embedding space in place; it must re-embed like a Mismatch.
///
/// Logs go to the supplied <see cref="TextWriter"/>; callers pass
/// <c>Console.Error</c> because stdout is the MCP stdio channel and must not be
/// polluted.
/// </summary>
public static class EmbedderMigration
{
    /// <summary>
    /// SQLite startup path. Match → no-op; Unstamped+empty → stamp; Mismatch OR
    /// Unstamped+populated → dispatch on <paramref name="mode"/>:
    /// <list type="bullet">
    ///   <item><see cref="OnModelChange.Auto"/> → re-embed every row in place via
    ///   <see cref="EmbeddingReindexer.RunAtomicSqlite"/> (atomic) + restamp, then continue.</item>
    ///   <item><see cref="OnModelChange.Warn"/> → log a degraded-retrieval warning and continue,
    ///   WITHOUT restamping (so the nag persists across restarts).</item>
    ///   <item><see cref="OnModelChange.Block"/> → throw
    ///   <see cref="EmbedderFingerprintMismatchException"/> (legacy fail-fast).</item>
    /// </list>
    /// </summary>
    public static void EnsureCompatibleSqlite(
        MsSqliteConnection conn,
        IStore store,
        IVectorSearch vec,
        IEmbedder embedder,
        OnModelChange mode,
        TextWriter? log)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(vec);
        ArgumentNullException.ThrowIfNull(embedder);

        var meta = (IMetaStore)store;
        var state = EmbedderFingerprint.Check(meta, embedder, out var stored);

        if (state == EmbedderFingerprint.FingerprintState.Match)
        {
            return;
        }

        // Unstamped + EMPTY index = fresh DB → stamp and continue (no migration).
        if (state == EmbedderFingerprint.FingerprintState.Unstamped && IndexIsEmpty(store))
        {
            EmbedderFingerprint.Restamp(meta, embedder);
            return;
        }

        // Otherwise: Mismatch, OR Unstamped + populated. Both mean the existing
        // vectors live in a model space that is not the configured one, so we
        // dispatch identically. `stored` is null only in the unstamped-populated
        // case — pick wording / a synthesized descriptor accordingly.
        var from = stored?.Model ?? "an unknown model (no embedder fingerprint)";
        switch (mode)
        {
            case OnModelChange.Block:
                throw new EmbedderFingerprintMismatchException(
                    stored ?? new EmbedderDescriptor("unknown", "unknown", "", embedder.Descriptor.Dimensions),
                    embedder.Descriptor);

            case OnModelChange.Warn:
                log?.WriteLine(stored is null
                    ? "[total-recall] existing vectors have no embedder fingerprint (likely produced by a " +
                      $"prior model); running with stale vectors (on_model_change=warn). Local retrieval quality " +
                      "is degraded until you run `total-recall reindex-embeddings`."
                    : $"[total-recall] embedding model changed ({from} -> {embedder.Descriptor.Model}); " +
                      "running with stale vectors (on_model_change=warn). Local retrieval quality is degraded " +
                      "until you run `total-recall reindex-embeddings`.");
                return;

            default: // Auto
                log?.WriteLine(stored is null
                    ? "[total-recall] existing vectors have no embedder fingerprint (likely produced by a " +
                      $"prior model); re-embedding the local database in place (one-time, this may take a moment)..."
                    : $"[total-recall] embedding model changed ({from} -> {embedder.Descriptor.Model}); " +
                      "re-embedding the local database in place (one-time, this may take a moment)...");
                int n;
                try
                {
                    n = EmbeddingReindexer.RunAtomicSqlite(conn, store, vec, embedder, log);
                }
                catch (Exception ex)
                {
                    // The reindex is atomic, so a failure here left the DB unchanged (old
                    // vectors + old fingerprint) — the next boot will retry. Surface the
                    // escape hatch so a permanently-broken embedder isn't an unrecoverable
                    // boot loop with an opaque error.
                    throw new InvalidOperationException(
                        $"[total-recall] automatic re-embedding failed after a model change " +
                        $"({from} -> {embedder.Descriptor.Model}): {ex.Message}. The database " +
                        "was left unchanged. To start the server with degraded retrieval instead, set " +
                        "embedding.on_model_change=\"warn\" (or \"block\" to keep failing fast), then run " +
                        "`total-recall reindex-embeddings` once the embedder is healthy.", ex);
                }
                log?.WriteLine(
                    $"[total-recall] re-embedded {n} entries; embedder fingerprint updated.");
                return;
        }
    }

    /// <summary>
    /// Postgres startup path. Match → no-op; Unstamped+empty → stamp; Mismatch OR
    /// Unstamped+populated → dispatch on <paramref name="mode"/>:
    /// <list type="bullet">
    ///   <item><see cref="OnModelChange.Auto"/> → throw <see cref="InvalidOperationException"/>:
    ///   in-place auto-migration is unsupported on the postgres backend; the message is actionable.</item>
    ///   <item><see cref="OnModelChange.Warn"/> → log a degraded-retrieval warning and continue,
    ///   WITHOUT restamping.</item>
    ///   <item><see cref="OnModelChange.Block"/> → throw
    ///   <see cref="EmbedderFingerprintMismatchException"/> (legacy fail-fast).</item>
    /// </list>
    /// </summary>
    public static void EnsureCompatiblePostgres(
        IStore store,
        IEmbedder embedder,
        OnModelChange mode,
        TextWriter? log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);

        var meta = (IMetaStore)store;
        var state = EmbedderFingerprint.Check(meta, embedder, out var stored);

        if (state == EmbedderFingerprint.FingerprintState.Match)
        {
            return;
        }

        // Unstamped + EMPTY index = fresh DB → stamp and continue (no migration).
        if (state == EmbedderFingerprint.FingerprintState.Unstamped && IndexIsEmpty(store))
        {
            EmbedderFingerprint.Restamp(meta, embedder);
            return;
        }

        // Otherwise: Mismatch, OR Unstamped + populated — dispatch identically.
        // `stored` is null only in the unstamped-populated case.
        var from = stored?.Model ?? "an unknown model (no embedder fingerprint)";
        switch (mode)
        {
            case OnModelChange.Block:
                throw new EmbedderFingerprintMismatchException(
                    stored ?? new EmbedderDescriptor("unknown", "unknown", "", embedder.Descriptor.Dimensions),
                    embedder.Descriptor);

            case OnModelChange.Warn:
                log?.WriteLine(stored is null
                    ? "[total-recall] existing vectors have no embedder fingerprint (likely produced by a " +
                      "prior model); running with stale vectors (on_model_change=warn). Local retrieval quality " +
                      "is degraded. On the postgres backend, re-embed by re-ingesting into a fresh database " +
                      "(`total-recall reindex-embeddings` does not support postgres)."
                    : $"[total-recall] embedding model changed ({from} -> {embedder.Descriptor.Model}); " +
                      "running with stale vectors (on_model_change=warn). Local retrieval quality is degraded. " +
                      "On the postgres backend, re-embed by re-ingesting into a fresh database " +
                      "(`total-recall reindex-embeddings` does not support postgres).");
                return;

            default: // Auto
                throw new InvalidOperationException(stored is null
                    ? "[total-recall] existing vectors have no embedder fingerprint (likely produced by a " +
                      "prior model), but automatic re-embedding is not supported on the postgres backend. " +
                      "Options: re-ingest into a fresh database, set embedding.on_model_change=\"warn\" to run " +
                      "with degraded retrieval, or restore the previous embedder configuration."
                    : $"[total-recall] embedding model changed ({from} -> {embedder.Descriptor.Model}), " +
                      "but automatic re-embedding is not supported on the postgres backend. Options: re-ingest " +
                      "into a fresh database, set embedding.on_model_change=\"warn\" to run with degraded " +
                      "retrieval, or restore the previous embedder configuration.");
        }
    }

    /// <summary>
    /// True when the index holds no content rows in ANY of
    /// <see cref="TierNames.AllTablePairs"/> — the exact set
    /// <see cref="EmbeddingReindexer.Reindex"/> walks, so "non-empty" here means
    /// "the reindexer would rewrite ≥1 vector." Short-circuits on the first
    /// non-empty pair so a populated DB does not enumerate every table.
    /// </summary>
    private static bool IndexIsEmpty(IStore store)
    {
        foreach (var (tier, type) in TierNames.AllTablePairs)
        {
            if (store.List(tier, type).Any())
            {
                return false;
            }
        }
        return true;
    }
}
