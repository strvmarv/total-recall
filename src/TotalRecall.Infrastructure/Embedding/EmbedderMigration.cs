using System;
using System.IO;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Outcome of an <c>EnsureCompatible*</c> startup-policy decision. This is a
/// pure classification — the method performs no embedding/IO beyond stamping a
/// fresh DB; the caller acts on the decision (Task 5 launches a background
/// re-index worker when it sees <see cref="ReindexInBackground"/>).
/// </summary>
public enum EmbedderCompatibility
{
    /// <summary>Fingerprint matches (or a fresh DB was just stamped). Proceed normally.</summary>
    Compatible,

    /// <summary>Mismatch under <c>on_model_change=warn</c>: a degraded-retrieval
    /// warning was logged, the fingerprint was deliberately left un-restamped (so
    /// the nag persists), and the server should run with stale vectors.</summary>
    Warned,

    /// <summary>Mismatch under <c>on_model_change=auto</c> on the sqlite/cortex-local
    /// backend: the caller should launch a background re-index. Nothing was logged
    /// or re-embedded here — the caller logs when it starts the worker.</summary>
    ReindexInBackground,
}

/// <summary>
/// Startup embedder-compatibility policy. Encapsulates what happens at store
/// open when the configured embedder no longer matches the fingerprint stamped
/// in <c>_meta</c>, honoring <c>embedding.on_model_change</c>
/// (<see cref="OnModelChange"/>: auto | warn | block).
///
/// This is a DECISION-ONLY seam: it classifies the situation and returns an
/// <see cref="EmbedderCompatibility"/>. It performs no re-embedding here (the
/// caller drives that, e.g. via a background worker). The only mutation it makes
/// is stamping a fresh (unstamped + empty) DB. The contract (identical for both
/// backends, except the auto branch):
/// <list type="table">
///   <item><term>Match</term><description><see cref="EmbedderCompatibility.Compatible"/>.</description></item>
///   <item><term>Unstamped + EMPTY index</term><description>stamp the configured
///   embedder (fresh DB — no vectors to migrate); <see cref="EmbedderCompatibility.Compatible"/>.</description></item>
///   <item><term>Unstamped + POPULATED index</term><description>routes into the
///   SAME mode dispatch as Mismatch — the existing vectors came from an unknown
///   prior model. There is no <c>stored</c> descriptor, so warn messages say
///   "existing vectors have no embedder fingerprint" instead of "X -&gt; Y".</description></item>
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
    /// SQLite startup decision. Match → <see cref="EmbedderCompatibility.Compatible"/>;
    /// Unstamped+empty → stamp, <see cref="EmbedderCompatibility.Compatible"/>; Mismatch OR
    /// Unstamped+populated → dispatch on <paramref name="mode"/>:
    /// <list type="bullet">
    ///   <item><see cref="OnModelChange.Auto"/> → <see cref="EmbedderCompatibility.ReindexInBackground"/>
    ///   (no logging, no re-embed here — the caller starts the background worker and logs then).</item>
    ///   <item><see cref="OnModelChange.Warn"/> → log a degraded-retrieval warning (WITHOUT
    ///   restamping, so the nag persists across restarts) and return
    ///   <see cref="EmbedderCompatibility.Warned"/>.</item>
    ///   <item><see cref="OnModelChange.Block"/> → throw
    ///   <see cref="EmbedderFingerprintMismatchException"/> (legacy fail-fast).</item>
    /// </list>
    /// </summary>
    public static EmbedderCompatibility EnsureCompatibleSqlite(
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
            return EmbedderCompatibility.Compatible;
        }

        // Unstamped + EMPTY index = fresh DB → stamp and continue (no migration).
        if (state == EmbedderFingerprint.FingerprintState.Unstamped && IndexIsEmpty(store))
        {
            EmbedderFingerprint.Restamp(meta, embedder);
            return EmbedderCompatibility.Compatible;
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
                return EmbedderCompatibility.Warned;

            default: // Auto — defer the re-embed to a background worker the caller starts.
                // Do NOT log or restamp here: the caller logs when it launches the
                // worker, and the fingerprint is restamped only once the background
                // pass completes (so an interrupted run resumes on the next boot).
                return EmbedderCompatibility.ReindexInBackground;
        }
    }

    /// <summary>
    /// Postgres startup decision. Match → <see cref="EmbedderCompatibility.Compatible"/>;
    /// Unstamped+empty → stamp, <see cref="EmbedderCompatibility.Compatible"/>; Mismatch OR
    /// Unstamped+populated → dispatch on <paramref name="mode"/>:
    /// <list type="bullet">
    ///   <item><see cref="OnModelChange.Auto"/> → throw <see cref="InvalidOperationException"/>:
    ///   in-place auto-migration is unsupported on the postgres backend (there is no local vec
    ///   index to background-reindex); the message is actionable.</item>
    ///   <item><see cref="OnModelChange.Warn"/> → log a degraded-retrieval warning (WITHOUT
    ///   restamping) and return <see cref="EmbedderCompatibility.Warned"/>.</item>
    ///   <item><see cref="OnModelChange.Block"/> → throw
    ///   <see cref="EmbedderFingerprintMismatchException"/> (legacy fail-fast).</item>
    /// </list>
    /// </summary>
    public static EmbedderCompatibility EnsureCompatiblePostgres(
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
            return EmbedderCompatibility.Compatible;
        }

        // Unstamped + EMPTY index = fresh DB → stamp and continue (no migration).
        if (state == EmbedderFingerprint.FingerprintState.Unstamped && IndexIsEmpty(store))
        {
            EmbedderFingerprint.Restamp(meta, embedder);
            return EmbedderCompatibility.Compatible;
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
                return EmbedderCompatibility.Warned;

            default: // Auto — unsupported on postgres (no local vec index to background-reindex).
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
    /// "the reindexer would rewrite ≥1 vector." Uses <see cref="IStore.Count"/>
    /// (a <c>SELECT COUNT(*)</c>) rather than <c>List(...).Any()</c> so a large
    /// table is never materialized just to test for existence; short-circuits on
    /// the first non-empty pair.
    /// </summary>
    private static bool IndexIsEmpty(IStore store)
    {
        foreach (var (tier, type) in TierNames.AllTablePairs)
        {
            if (store.Count(tier, type) > 0)
            {
                return false;
            }
        }
        return true;
    }
}
