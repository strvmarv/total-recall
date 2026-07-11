using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Telemetry;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// Task 6 — one-time, app-layer data move into the merged tier model (tier
/// model v2). Runs once at <c>session_start</c> where an embedder exists, so
/// each moved row can be synchronously re-embedded (vec0 companions are
/// rowid-keyed with no triggers, so a raw SQL table copy would lose the
/// embedding).
///
/// After it runs: every former <c>pinned_*</c> row is in <c>hot_*</c> with
/// <c>sticky=1</c>, <c>decay_score=1.0</c>, all 18 content columns + a
/// re-embedded vec preserved; every pre-existing NON-sticky <c>hot_*</c> row
/// is in <c>warm_*</c>; the <c>pinned_*</c> tables are dropped; the
/// <c>_meta['tier_v2_migrated']</c> flag is set — ALL inside ONE transaction
/// (atomic on crash), idempotent via the flag.
///
/// DU-independent by construction: it reads/writes tables by literal name
/// (no <see cref="TotalRecall.Core.Tier"/> DU reference), so it survives the
/// <c>Tier.Pinned</c> removal in Task 9.
///
/// RC1: every DB command runs raw with <c>cmd.Transaction = tx</c>. It never
/// calls <see cref="IVectorSearch"/> or any self-transacting
/// <see cref="SqliteStore"/> method inside the open transaction (they don't
/// enlist and would throw). That is why the signature takes an
/// <see cref="IEmbedder"/> (pure, safe to call) rather than a vector-search
/// service — vec rows are written via raw SQL enlisted in <c>tx</c>.
/// </summary>
public static class TierV2DataMigration
{
    private const string Flag = "tier_v2_migrated";

    // The 18 content columns shared by pinned_*, hot_* and warm_* tables. Hot
    // additionally carries `sticky` (set separately after the copy); it is not
    // part of this list so the same SELECT works for every source/destination.
    private const string Columns =
        "id, content, summary, source, source_tool, project, tags, " +
        "created_at, updated_at, last_accessed_at, access_count, decay_score, " +
        "parent_id, collection_id, metadata, scope, entry_type, times_injected";

    // Same 18 columns, but decay_score forced to 1.0 (used for pinned -> hot).
    private const string ColumnsResetDecay =
        "id, content, summary, source, source_tool, project, tags, " +
        "created_at, updated_at, last_accessed_at, access_count, 1.0, " +
        "parent_id, collection_id, metadata, scope, entry_type, times_injected";

    /// <summary>
    /// Idempotently move existing pinned/hot data into the merged tier model.
    /// SQLite-only. No-op (returns immediately) if the flag is already set.
    /// </summary>
    /// <param name="conn">Open, migrated SQLite connection (owns the tables).</param>
    /// <param name="embedder">
    /// Embedder for synchronous re-embedding of each moved row. May be
    /// <c>null</c> (embedding disabled/offline) — the vec step is then skipped
    /// entirely (NI1); content + FTS still move and the flag is still set.
    /// </param>
    /// <param name="log">Optional compaction log; movement rows are appended
    /// AFTER the transaction commits (LogEvent does not enlist in <c>tx</c>).</param>
    public static void RunOnce(MsSqliteConnection conn, IEmbedder? embedder, CompactionLog? log)
    {
        ArgumentNullException.ThrowIfNull(conn);

        // NC2 (repeat guard): idempotent on the 2nd+ run.
        if (GetMeta(conn, null, Flag) == "1") return;

        // A DB that never had a pinned tier (exotic already-clean case) has
        // nothing to move — set the flag and return. On a normal DB Migration 16
        // always creates pinned_* so the full path runs (over zero rows on a
        // genuine fresh install); the _meta flag is the real repeat-guard.
        if (!TableExists(conn, "pinned_memories"))
        {
            SetMeta(conn, null, Flag, "1");
            return;
        }

        // Movement log rows are collected here and flushed AFTER commit — RC1:
        // CompactionLog.LogEvent creates un-enlisted commands and would throw
        // inside the open transaction.
        var pendingLog = new List<CompactionLogEntry>();

        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var type in new[] { "memories", "knowledge" })
            {
                // 1. hot -> warm, NON-STICKY ONLY (NC2). Per-row; deletes the
                //    source vec row as it goes so no orphan hot_*_vec row
                //    survives to collide with a reused rowid in step 2 (RC2).
                MoveRowsPerRow(conn, tx, src: $"hot_{type}", dst: $"warm_{type}",
                    where: "sticky = 0", stickyOnDst: false, resetDecay: false,
                    srcTier: "hot", dstTier: "warm", reason: "migration_hot_to_warm",
                    embedder, pendingLog);

                // 2. pinned -> hot, sticky=1, decay=1.0.
                MoveRowsPerRow(conn, tx, src: $"pinned_{type}", dst: $"hot_{type}",
                    where: null, stickyOnDst: true, resetDecay: true,
                    srcTier: "pinned", dstTier: "hot", reason: "migration_pin_to_sticky",
                    embedder, pendingLog);

                // 3. NC1: RunOnce OWNS the pinned_* drop, AFTER moving rows, in
                //    its own transaction. No open-time SQL migration may drop
                //    these tables earlier (RunMigrations runs before session_start).
                foreach (var t in new[] { $"pinned_{type}", $"pinned_{type}_vec", $"pinned_{type}_fts" })
                    Exec(conn, tx, $"DROP TABLE IF EXISTS {t}");
            }

            SetMeta(conn, tx, Flag, "1");
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        // Best-effort telemetry, post-commit. A logging failure must not undo a
        // committed migration.
        if (log is not null)
        {
            try
            {
                foreach (var entry in pendingLog)
                    log.LogEvent(entry);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"total-recall: tier_v2 migration logging failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Move every row of <paramref name="src"/> matching <paramref name="where"/>
    /// into <paramref name="dst"/>, one row at a time, mirroring
    /// <see cref="SqliteStore.InsertWithEmbedding"/>'s transactional vec dance.
    /// All commands are enlisted in <paramref name="tx"/> (RC1).
    /// </summary>
    private static void MoveRowsPerRow(
        MsSqliteConnection conn,
        SqliteTransaction tx,
        string src,
        string dst,
        string? where,
        bool stickyOnDst,
        bool resetDecay,
        string srcTier,
        string dstTier,
        string reason,
        IEmbedder? embedder,
        List<CompactionLogEntry> pendingLog)
    {
        // Snapshot the source ids (+ content for re-embed) into a stable list
        // first, so mutating src while iterating cannot disturb the cursor.
        var rows = new List<(string Id, string Content)>();
        using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = where is null
                ? $"SELECT id, content FROM {src}"
                : $"SELECT id, content FROM {src} WHERE {where}";
            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        var selectCols = resetDecay ? ColumnsResetDecay : Columns;

        foreach (var (id, content) in rows)
        {
            // 1. Copy the content row (explicit 18 columns, C2 — preserves
            //    entry_type + scope + times_injected). The dst FTS AFTER INSERT
            //    trigger fires here. decay_score is forced to 1.0 when resetDecay.
            using (var insertCmd = conn.CreateCommand())
            {
                insertCmd.Transaction = tx;
                insertCmd.CommandText =
                    $"INSERT INTO {dst} ({Columns}) SELECT {selectCols} FROM {src} WHERE id = $id";
                insertCmd.Parameters.AddWithValue("$id", id);
                insertCmd.ExecuteNonQuery();
            }

            // 2. RI1: read last_insert_rowid() immediately for THIS row, right
            //    after its INSERT (mirrors InsertWithEmbedding:99-105) — before
            //    any further INSERT (including the sticky UPDATE's FTS trigger)
            //    could move it.
            long rowid;
            using (var rowidCmd = conn.CreateCommand())
            {
                rowidCmd.Transaction = tx;
                rowidCmd.CommandText = "SELECT last_insert_rowid()";
                rowid = (long)rowidCmd.ExecuteScalar()!;
            }

            // 3. sticky flag (only hot_* carries the column).
            if (stickyOnDst)
            {
                using var stickyCmd = conn.CreateCommand();
                stickyCmd.Transaction = tx;
                stickyCmd.CommandText = $"UPDATE {dst} SET sticky = 1 WHERE id = $id";
                stickyCmd.Parameters.AddWithValue("$id", id);
                stickyCmd.ExecuteNonQuery();
            }

            // 4. Vec (NI1: only when an embedder is available). Mirror
            //    InsertWithEmbedding:113-131 — delete any orphan at the
            //    destination rowid first (RC2 belt-and-suspenders), then insert
            //    the re-embedded vector. embedder.Embed is pure and safe inside tx.
            if (embedder is not null)
            {
                var embedding = embedder.Embed(content);

                using (var orphanCmd = conn.CreateCommand())
                {
                    orphanCmd.Transaction = tx;
                    orphanCmd.CommandText = $"DELETE FROM {dst}_vec WHERE rowid = $rowid";
                    orphanCmd.Parameters.AddWithValue("$rowid", rowid);
                    orphanCmd.ExecuteNonQuery();
                }

                using var vecCmd = conn.CreateCommand();
                vecCmd.Transaction = tx;
                vecCmd.CommandText =
                    $"INSERT INTO {dst}_vec (rowid, embedding) VALUES ($rowid, $embedding)";
                vecCmd.Parameters.AddWithValue("$rowid", rowid);
                vecCmd.Parameters.AddWithValue(
                    "$embedding",
                    MemoryMarshal.AsBytes(new ReadOnlySpan<float>(embedding)).ToArray());
                vecCmd.ExecuteNonQuery();
            }

            // 5. RC2: delete the SOURCE vec companion BEFORE the source content
            //    row (so the rowid subquery still resolves), leaving no orphan
            //    hot_*_vec row to collide with a reused rowid in step 2. The src
            //    FTS AFTER DELETE trigger fires on the content delete.
            using (var srcVecCmd = conn.CreateCommand())
            {
                srcVecCmd.Transaction = tx;
                srcVecCmd.CommandText =
                    $"DELETE FROM {src}_vec WHERE rowid = (SELECT rowid FROM {src} WHERE id = $id)";
                srcVecCmd.Parameters.AddWithValue("$id", id);
                srcVecCmd.ExecuteNonQuery();
            }
            using (var srcDelCmd = conn.CreateCommand())
            {
                srcDelCmd.Transaction = tx;
                srcDelCmd.CommandText = $"DELETE FROM {src} WHERE id = $id";
                srcDelCmd.Parameters.AddWithValue("$id", id);
                srcDelCmd.ExecuteNonQuery();
            }

            // 6. Queue a movement log row (flushed post-commit — RC1).
            pendingLog.Add(new CompactionLogEntry(
                SessionId: "tier_v2_migration",
                SourceTier: srcTier,
                TargetTier: dstTier,
                SourceEntryIds: new[] { id },
                TargetEntryId: id,
                DecayScores: new Dictionary<string, double>(),
                Reason: reason,
                ConfigSnapshotId: ""));
        }
    }

    // --- raw-SQL _meta / schema helpers (no IStore surface needed) ----------

    private static string? GetMeta(MsSqliteConnection conn, SqliteTransaction? tx, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(MsSqliteConnection conn, SqliteTransaction? tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO _meta (key, value) VALUES ($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(MsSqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = $n";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() is long l && l > 0;
    }

    private static void Exec(MsSqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
