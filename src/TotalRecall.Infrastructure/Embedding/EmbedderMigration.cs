using System;
using System.IO;
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
/// This is the testable seam ServerComposition delegates to: both
/// <see cref="EnsureCompatibleSqlite"/> and <see cref="EnsureCompatiblePostgres"/>
/// preserve the old <see cref="EmbedderFingerprint.EnsureMatches"/> behavior for
/// the Unstamped (stamp) and Match (no-op) cases; the ONLY behavior change is
/// that a Mismatch now dispatches per policy instead of always throwing.
///
/// Logs go to the supplied <see cref="TextWriter"/>; callers pass
/// <c>Console.Error</c> because stdout is the MCP stdio channel and must not be
/// polluted.
/// </summary>
public static class EmbedderMigration
{
    /// <summary>
    /// SQLite startup path. Unstamped → stamp; Match → no-op; Mismatch →
    /// dispatch on <paramref name="mode"/>:
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
        switch (EmbedderFingerprint.Check(meta, embedder, out var stored))
        {
            case EmbedderFingerprint.FingerprintState.Unstamped:
                EmbedderFingerprint.Restamp(meta, embedder);
                return;

            case EmbedderFingerprint.FingerprintState.Match:
                return;

            default: // Mismatch
                switch (mode)
                {
                    case OnModelChange.Block:
                        throw new EmbedderFingerprintMismatchException(stored!, embedder.Descriptor);

                    case OnModelChange.Warn:
                        log?.WriteLine(
                            $"[total-recall] embedding model changed ({stored!.Model} -> {embedder.Descriptor.Model}); " +
                            "running with stale vectors (on_model_change=warn). Local retrieval quality is degraded " +
                            "until you run `total-recall reindex-embeddings`.");
                        return;

                    default: // Auto
                        log?.WriteLine(
                            $"[total-recall] embedding model changed ({stored!.Model} -> {embedder.Descriptor.Model}); " +
                            "re-embedding the local database in place (one-time, this may take a moment)...");
                        var n = EmbeddingReindexer.RunAtomicSqlite(conn, store, vec, embedder, log);
                        log?.WriteLine(
                            $"[total-recall] re-embedded {n} entries; embedder fingerprint updated.");
                        return;
                }
        }
    }

    /// <summary>
    /// Postgres startup path. Unstamped → stamp; Match → no-op; Mismatch →
    /// dispatch on <paramref name="mode"/>:
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
        switch (EmbedderFingerprint.Check(meta, embedder, out var stored))
        {
            case EmbedderFingerprint.FingerprintState.Unstamped:
                EmbedderFingerprint.Restamp(meta, embedder);
                return;

            case EmbedderFingerprint.FingerprintState.Match:
                return;

            default: // Mismatch
                switch (mode)
                {
                    case OnModelChange.Block:
                        throw new EmbedderFingerprintMismatchException(stored!, embedder.Descriptor);

                    case OnModelChange.Warn:
                        log?.WriteLine(
                            $"[total-recall] embedding model changed ({stored!.Model} -> {embedder.Descriptor.Model}); " +
                            "running with stale vectors (on_model_change=warn). Local retrieval quality is degraded " +
                            "until you run `total-recall reindex-embeddings`.");
                        return;

                    default: // Auto
                        throw new InvalidOperationException(
                            $"[total-recall] embedding model changed ({stored!.Model} -> {embedder.Descriptor.Model}), " +
                            "but automatic re-embedding is not supported on the postgres backend. Options: re-ingest " +
                            "into a fresh database, set embedding.on_model_change=\"warn\" to run with degraded " +
                            "retrieval, or restore the previous embedder configuration.");
                }
        }
    }
}
