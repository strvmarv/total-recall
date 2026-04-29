using System;
using System.Collections.Generic;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Sync;

/// <summary>A single item in the persistent sync queue.</summary>
public sealed record SyncQueueItem(
    long Id, string EntityType, string Operation, string? EntityId,
    string Payload, DateTime CreatedAt, int Attempts, string? LastError);

/// <summary>
/// Persistent SQLite-backed outbound queue for Cortex sync. Items are
/// enqueued when RoutingStore writes a local memory and drained by
/// SyncService to push upstream. If Cortex is unreachable, items stay
/// in the queue with incremented attempt counts and a backoff window
/// (<c>next_attempt_at</c>); they keep retrying forever, just spaced
/// further apart. Survives process crashes because it lives in the same
/// SQLite database.
/// </summary>
public sealed class SyncQueue
{
    private readonly MsSqliteConnection _conn;
    private readonly Func<DateTime> _utcNow;

    // Exponential backoff: 60s * 2^(attempts-1), capped at 1h.
    // attempts=1 → 60s, 2 → 120s, 3 → 240s, ..., 7+ → 3600s.
    private const int BaseBackoffSeconds = 60;
    private const int MaxBackoffSeconds = 3600;

    public SyncQueue(MsSqliteConnection conn) : this(conn, () => DateTime.UtcNow) { }

    // Test seam: inject clock for deterministic backoff assertions.
    internal SyncQueue(MsSqliteConnection conn, Func<DateTime> utcNow)
    {
        _conn = conn;
        _utcNow = utcNow;
    }

    internal static int ComputeBackoffSeconds(int attempts)
    {
        if (attempts <= 0) return 0;
        // 2^(attempts-1) overflows int around attempts=32; cap the shift.
        var shift = Math.Min(attempts - 1, 20);
        long seconds = (long)BaseBackoffSeconds << shift;
        return (int)Math.Min(seconds, MaxBackoffSeconds);
    }

    /// <summary>Enqueue a new item for sync.</summary>
    public void Enqueue(string entityType, string operation, string? entityId, string payload)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_queue (entity_type, operation, entity_id, payload, created_at)
            VALUES ($entityType, $operation, $entityId, $payload, $createdAt)
            """;
        cmd.Parameters.AddWithValue("$entityType", entityType);
        cmd.Parameters.AddWithValue("$operation", operation);
        cmd.Parameters.AddWithValue("$entityId", (object?)entityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Drain up to <paramref name="limit"/> items from the queue. Items in a
    /// backoff window (<c>next_attempt_at &gt; now</c>) are skipped; they
    /// re-enter the candidate set once their wait expires. Memory entries
    /// are prioritized over usage/retrieval/compaction.
    /// </summary>
    public IReadOnlyList<SyncQueueItem> Drain(int limit)
    {
        var items = new List<SyncQueueItem>();
        var nowIso = _utcNow().ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entity_type, operation, entity_id, payload, created_at, attempts, last_error
            FROM sync_queue
            WHERE next_attempt_at IS NULL OR next_attempt_at <= $now
            ORDER BY CASE entity_type WHEN 'memory' THEN 0 ELSE 1 END ASC, id ASC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$now", nowIso);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new SyncQueueItem(
                Id: reader.GetInt64(0),
                EntityType: reader.GetString(1),
                Operation: reader.GetString(2),
                EntityId: reader.IsDBNull(3) ? null : reader.GetString(3),
                Payload: reader.GetString(4),
                CreatedAt: DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Attempts: reader.GetInt32(6),
                LastError: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return items;
    }

    /// <summary>Remove a successfully synced item from the queue.</summary>
    public void MarkCompleted(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sync_queue WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Increment attempts, record the error, and set a backoff window so the
    /// item is excluded from <see cref="Drain"/> until the window expires.
    /// </summary>
    public void MarkFailed(long id, string error)
    {
        // Read current attempts so we compute the backoff against the
        // post-increment value without round-tripping twice.
        int currentAttempts;
        using (var read = _conn.CreateCommand())
        {
            read.CommandText = "SELECT attempts FROM sync_queue WHERE id = $id";
            read.Parameters.AddWithValue("$id", id);
            var raw = read.ExecuteScalar();
            currentAttempts = raw is long l ? (int)l : 0;
        }
        var nextAttempt = _utcNow().AddSeconds(ComputeBackoffSeconds(currentAttempts + 1)).ToString("o");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sync_queue
            SET attempts = attempts + 1,
                last_error = $error,
                next_attempt_at = $next
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$error", error);
        cmd.Parameters.AddWithValue("$next", nextAttempt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Count of all items not yet successfully synced (regardless of backoff
    /// state). Items in a backoff window still count as pending — they will
    /// eventually retry.
    /// </summary>
    public int PendingCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_queue";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }
}
