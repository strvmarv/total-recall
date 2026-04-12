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
/// in the queue with incremented attempt counts. Survives process
/// crashes because it lives in the same SQLite database.
/// </summary>
public sealed class SyncQueue
{
    private readonly MsSqliteConnection _conn;
    private const int MaxAttempts = 10;

    public SyncQueue(MsSqliteConnection conn) => _conn = conn;

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
    /// Drain up to <paramref name="limit"/> items from the queue, excluding
    /// items that have reached <see cref="MaxAttempts"/> (poison pill).
    /// Items are returned in FIFO order.
    /// </summary>
    public IReadOnlyList<SyncQueueItem> Drain(int limit)
    {
        var items = new List<SyncQueueItem>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entity_type, operation, entity_id, payload, created_at, attempts, last_error
            FROM sync_queue
            WHERE attempts < $maxAttempts
            ORDER BY id ASC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$maxAttempts", MaxAttempts);
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

    /// <summary>Increment attempts and record the error for a failed sync item.</summary>
    public void MarkFailed(long id, string error)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sync_queue
            SET attempts = attempts + 1, last_error = $error
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$error", error);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Count items still eligible for sync (attempts &lt; MaxAttempts).</summary>
    public int PendingCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_queue WHERE attempts < $maxAttempts";
        cmd.Parameters.AddWithValue("$maxAttempts", MaxAttempts);
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }
}
