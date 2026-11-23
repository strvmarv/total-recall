using System.Text.Json;
using Microsoft.Data.Sqlite;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// One <c>held-delete</c> item (milestone 1b-ii, decisions 7 and 8): a deletion the pull holds back instead of
/// carrying out. <paramref name="Direction"/> is <c>remote</c> (the record is gone from the repository, the entry is
/// kept here) or <c>local</c> (the entry is gone here, the record is kept in the repository); <paramref name="Cause"/> is
/// <c>guard</c>, <c>unproven</c> or <c>sticky</c>, or null when the row has none; <paramref name="ArrivedAt"/> is the
/// applier clock in milliseconds; <paramref name="State"/> is the row's state (<c>open</c>, or <c>approved</c> once the
/// user answered it and the next pull has not carried the answer out yet; <see cref="GitSyncReviews.UpsertHeld"/> always writes <c>open</c>).
/// </summary>
internal sealed record HeldItem(
    string EntryId, string Direction, string? RecordHash, string? Cause, long ArrivedAt, string State = "open");

/// <summary>
/// The <c>held-delete</c> rows of <c>git_sync_review</c>, read and changed here, and the <c>new-pin</c> rows, which are
/// only written, through <see cref="UpsertNewPin"/>. Plain SQL on the caller's connection; every command carries the
/// optional transaction, so the statements run inside an entry's <see cref="SqliteTransaction"/>. Rows of the
/// <c>pin-edit</c> and <c>conflict</c> kinds are never read or changed here, nor are <c>new-pin</c> rows read or
/// changed by anything but <see cref="UpsertNewPin"/>.
/// </summary>
internal static class GitSyncReviews
{
    private const string HeldDelete = "held-delete";

    private const string SelectHeld =
        "SELECT entry_id, direction, record_hash, payload_json, arrived_at, state FROM git_sync_review " +
        "WHERE kind = 'held-delete' AND direction IS NOT NULL";

    private const string SelectOpen = SelectHeld + " AND state = 'open'";

    /// <summary>Every open <c>held-delete</c> item, by entry id (ordinal).</summary>
    public static Dictionary<string, HeldItem> ReadOpenHeld(MsSqliteConnection conn, SqliteTransaction? tx = null) =>
        ReadAll(conn, SelectOpen, tx);

    /// <summary>Every approved <c>held-delete</c> item (answered by the user, not yet carried out), by entry id (ordinal).</summary>
    public static Dictionary<string, HeldItem> ReadApprovedHeld(MsSqliteConnection conn, SqliteTransaction? tx = null) =>
        ReadAll(conn, SelectHeld + " AND state = 'approved'", tx);

    private static Dictionary<string, HeldItem> ReadAll(MsSqliteConnection conn, string sql, SqliteTransaction? tx)
    {
        var rows = new Dictionary<string, HeldItem>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var item = ReadItem(r);
            rows[item.EntryId] = item;
        }
        return rows;
    }

    /// <summary>The entry's <c>held-delete</c> item of any state, or null when it has none.</summary>
    public static HeldItem? ReadHeldItem(MsSqliteConnection conn, string entryId, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SelectHeld + " AND entry_id = $id";
        cmd.Parameters.AddWithValue("$id", entryId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadItem(r) : null;
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
            r.GetInt64(4),
            r.GetString(5));

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

    /// <summary>Sets the entry's <c>held-delete</c> row to <c>approved</c> and changes nothing else; a missing row is a no-op.</summary>
    public static void ApproveHeld(MsSqliteConnection conn, string entryId, SqliteTransaction? tx = null) =>
        UpdateHeld(conn, "state = 'approved'", entryId, null, tx);

    /// <summary>Sets the entry's <c>held-delete</c> row back to <c>open</c> with the record hash it is now held for; a missing row is a no-op.</summary>
    public static void ReopenHeld(MsSqliteConnection conn, string entryId, string recordHash, SqliteTransaction? tx = null) =>
        UpdateHeld(conn, "state = 'open', record_hash = $hash", entryId, recordHash, tx);

    /// <summary>Replaces the record hash of the entry's <c>held-delete</c> row and keeps its state, direction, cause and <c>arrived_at</c>; a missing row is a no-op.</summary>
    public static void RefreshHeldHash(MsSqliteConnection conn, string entryId, string recordHash, SqliteTransaction? tx = null) =>
        UpdateHeld(conn, "record_hash = $hash", entryId, recordHash, tx);

    private static void UpdateHeld(
        MsSqliteConnection conn, string set, string entryId, string? recordHash, SqliteTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE git_sync_review SET " + set + " WHERE entry_id = $id AND kind = 'held-delete'";
        cmd.Parameters.AddWithValue("$id", entryId);
        if (recordHash is not null)
        {
            cmd.Parameters.AddWithValue("$hash", recordHash);
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts the open <c>new-pin</c> row of a restored memory (<c>{"origin":"restore"}</c>, no direction), or replaces
    /// the row with the same <c>(entry_id, 'new-pin')</c>.
    /// </summary>
    public static void UpsertNewPin(
        MsSqliteConnection conn, string entryId, string recordHash, long arrivedAt, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO git_sync_review (entry_id, kind, state, record_hash, direction, payload_json, arrived_at) " +
            "VALUES ($id, 'new-pin', 'open', $hash, NULL, $payload, $arrived) " +
            "ON CONFLICT(entry_id, kind) DO UPDATE SET state = 'open', record_hash = excluded.record_hash, " +
            "direction = NULL, payload_json = excluded.payload_json, arrived_at = excluded.arrived_at";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$hash", recordHash);
        cmd.Parameters.AddWithValue("$payload", RestorePayload());
        cmd.Parameters.AddWithValue("$arrived", arrivedAt);
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

    /// <summary><c>{"origin":"restore"}</c>, written without reflection.</summary>
    private static string RestorePayload()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("origin", "restore");
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
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
