using System.Text.Json;
using Microsoft.Data.Sqlite;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// One open <c>held-delete</c> item (milestone 1b-ii, decisions 7 and 8): a deletion the pull holds back instead of
/// carrying out. <paramref name="Direction"/> is <c>remote</c> (the record is gone from the repository, the entry is
/// kept here) or <c>local</c> (the entry is gone here, the record is kept in the repository); <paramref name="Cause"/> is
/// <c>guard</c>, <c>unproven</c> or <c>sticky</c>, or null when the row has none; <paramref name="ArrivedAt"/> is the
/// applier clock in milliseconds.
/// </summary>
internal sealed record HeldItem(string EntryId, string Direction, string? RecordHash, string? Cause, long ArrivedAt);

/// <summary>
/// The <c>held-delete</c> rows of <c>git_sync_review</c>. Plain SQL on the caller's connection; every command carries the
/// optional transaction, so the statements run inside an entry's <see cref="SqliteTransaction"/>. Rows of the other
/// kinds (<c>new-pin</c>, <c>pin-edit</c>, <c>conflict</c>) are never read or changed here.
/// </summary>
internal static class GitSyncReviews
{
    private const string HeldDelete = "held-delete";

    private const string SelectOpen =
        "SELECT entry_id, direction, record_hash, payload_json, arrived_at FROM git_sync_review " +
        "WHERE kind = 'held-delete' AND state = 'open' AND direction IS NOT NULL";

    /// <summary>Every open <c>held-delete</c> item, by entry id (ordinal).</summary>
    public static Dictionary<string, HeldItem> ReadOpenHeld(MsSqliteConnection conn, SqliteTransaction? tx = null)
    {
        var rows = new Dictionary<string, HeldItem>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SelectOpen;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var item = ReadItem(r);
            rows[item.EntryId] = item;
        }
        return rows;
    }

    /// <summary>The entry's open <c>held-delete</c> item, or null when it has none.</summary>
    public static HeldItem? ReadOpenHeldItem(MsSqliteConnection conn, string entryId, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SelectOpen + " AND entry_id = $id";
        cmd.Parameters.AddWithValue("$id", entryId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadItem(r) : null;
    }

    private static HeldItem ReadItem(SqliteDataReader r) =>
        new(
            r.GetString(0), r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : ParseCause(r.GetString(3)),
            r.GetInt64(4));

    /// <summary>Inserts the open item, or replaces the row with the same <c>(entry_id, 'held-delete')</c> (whatever its state and direction).</summary>
    public static void UpsertHeld(MsSqliteConnection conn, HeldItem item, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO git_sync_review (entry_id, kind, state, record_hash, direction, payload_json, arrived_at) " +
            "VALUES ($id, 'held-delete', 'open', $hash, $direction, $payload, $arrived) " +
            "ON CONFLICT(entry_id, kind) DO UPDATE SET state = 'open', record_hash = excluded.record_hash, " +
            "direction = excluded.direction, payload_json = excluded.payload_json, arrived_at = excluded.arrived_at";
        cmd.Parameters.AddWithValue("$id", item.EntryId);
        cmd.Parameters.AddWithValue("$hash", (object?)item.RecordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$direction", item.Direction);
        cmd.Parameters.AddWithValue("$payload", item.Cause is null ? DBNull.Value : CausePayload(item.Cause));
        cmd.Parameters.AddWithValue("$arrived", item.ArrivedAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes the entry's <c>held-delete</c> row; a missing row is a no-op.</summary>
    public static void DeleteHeld(MsSqliteConnection conn, string entryId, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM git_sync_review WHERE entry_id = $id AND kind = 'held-delete'";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.ExecuteNonQuery();
    }

    /// <summary><c>{"cause":"&lt;cause&gt;"}</c>, written without reflection (the engine is NativeAOT).</summary>
    private static string CausePayload(string cause)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("cause", cause);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The <c>cause</c> of a payload, or null when the payload is not a JSON object holding a string <c>cause</c>.</summary>
    private static string? ParseCause(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("cause", out var cause)
                && cause.ValueKind == JsonValueKind.String
                    ? cause.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
