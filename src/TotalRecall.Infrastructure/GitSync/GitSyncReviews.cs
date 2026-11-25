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
/// One pin item (pin review, decisions D1 and D2): a <c>new-pin</c> item (a record pins an entry that is not pinned here)
/// or a <c>pin-edit</c> item (a held edit of a pinned entry). <paramref name="State"/> is <c>open</c>, <c>unseen</c>,
/// <c>approved</c> or <c>declined</c>; <paramref name="RecordHash"/> is the shown hash of what the item shows;
/// <paramref name="Machine"/> is the machine that wrote the record; <paramref name="ArrivedAt"/> is the applier clock in
/// milliseconds; <paramref name="Origin"/> is <c>restore</c> for an item a restore opened, null for one a pull made.
/// </summary>
internal sealed record PinItem(
    string EntryId, string Kind, string State, string? RecordHash, string? Machine, long ArrivedAt, string? Origin = null);

/// <summary>
/// The <c>held-delete</c> rows of <c>git_sync_review</c>, read and changed by the held members, and the <c>new-pin</c> and
/// <c>pin-edit</c> rows, read and changed only by the pin members (<see cref="ReadPinItems"/> and the members after it).
/// <c>conflict</c> rows are read and changed by none. Plain SQL on the caller's connection; every command carries the
/// optional transaction, so the statements run inside an entry's <see cref="SqliteTransaction"/>. Every member's SQL is
/// restricted to its own kind.
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

    /// <summary>
    /// The entry's <c>held-delete</c> item, open or approved, or null when it has none. A row in any other state the
    /// schema admits (<c>unseen</c>, <c>declined</c>, <c>resolved</c>) is not an item: the pull reads only open and
    /// approved items, so the entry's transaction, which compares this read with the pull's, sees no item either.
    /// </summary>
    public static HeldItem? ReadHeldItem(MsSqliteConnection conn, string entryId, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SelectHeld + " AND state IN ('open', 'approved') AND entry_id = $id";
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

    private const string SelectPin =
        "SELECT entry_id, kind, state, record_hash, machine, arrived_at, payload_json FROM git_sync_review " +
        "WHERE kind IN ('new-pin', 'pin-edit') AND state IN ('open', 'unseen', 'approved', 'declined')";

    /// <summary>Every pin item (kinds <c>new-pin</c> and <c>pin-edit</c>, states <c>open</c>, <c>unseen</c>, <c>approved</c>, <c>declined</c>), ordinal by id, then kind.</summary>
    public static IReadOnlyList<PinItem> ReadPinItems(MsSqliteConnection conn, SqliteTransaction? tx = null)
    {
        var items = new List<PinItem>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SelectPin + " ORDER BY entry_id COLLATE BINARY, kind COLLATE BINARY";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            items.Add(ReadPin(r));
        return items;
    }

    /// <summary>The entry's pin item of <paramref name="kind"/> in one of the four states, or null when it has none.</summary>
    public static PinItem? ReadPinItem(MsSqliteConnection conn, string entryId, string kind, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = SelectPin + " AND entry_id = $id AND kind = $kind";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$kind", kind);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadPin(r) : null;
    }

    private static PinItem ReadPin(SqliteDataReader r) =>
        new(
            r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.GetInt64(5),
            r.IsDBNull(6) ? null : ParseString(r.GetString(6), "origin"));

    /// <summary>
    /// Inserts the pin item, or replaces the row with the same <c>(entry_id, kind)</c> (whatever its state): state, hash,
    /// machine, <c>arrived_at</c>, no direction, and the payload <c>{}</c> (a pull) or <c>{"origin":"restore"}</c>.
    /// </summary>
    public static void UpsertPinItem(MsSqliteConnection conn, PinItem item, SqliteTransaction? tx = null)
    {
        RequirePinKind(item.Kind);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO git_sync_review (entry_id, kind, state, record_hash, machine, direction, payload_json, arrived_at) " +
            "VALUES ($id, $kind, $state, $hash, $machine, NULL, $payload, $arrived) " +
            "ON CONFLICT(entry_id, kind) DO UPDATE SET state = excluded.state, record_hash = excluded.record_hash, " +
            "machine = excluded.machine, direction = NULL, payload_json = excluded.payload_json, arrived_at = excluded.arrived_at";
        cmd.Parameters.AddWithValue("$id", item.EntryId);
        cmd.Parameters.AddWithValue("$kind", item.Kind);
        cmd.Parameters.AddWithValue("$state", item.State);
        cmd.Parameters.AddWithValue("$hash", (object?)item.RecordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$machine", (object?)item.Machine ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", StringPayload("origin", item.Origin));
        cmd.Parameters.AddWithValue("$arrived", item.ArrivedAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sets the pin item to <c>unseen</c> with the hash and machine it is now shown for; payload and <c>arrived_at</c> are kept; a missing row is a no-op.</summary>
    public static void MarkPinUnseen(
        MsSqliteConnection conn, string entryId, string kind, string recordHash, string? machine, SqliteTransaction? tx = null)
    {
        RequirePinKind(kind);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "UPDATE git_sync_review SET state = 'unseen', record_hash = $hash, machine = $machine " +
            "WHERE entry_id = $id AND kind = $kind";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$hash", recordHash);
        cmd.Parameters.AddWithValue("$machine", (object?)machine ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sets the pin item's state to <c>approved</c> or <c>declined</c> and changes nothing else; a missing row is a no-op.</summary>
    public static void ResolvePinItem(
        MsSqliteConnection conn, string entryId, string kind, string state, SqliteTransaction? tx = null)
    {
        if (state is not ("approved" or "declined"))
            throw new ArgumentException("a pin item is answered approved or declined", nameof(state));
        RequirePinKind(kind);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE git_sync_review SET state = $state WHERE entry_id = $id AND kind = $kind";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes the entry's pin item of <paramref name="kind"/>; a missing row is a no-op.</summary>
    public static void DeletePinItem(MsSqliteConnection conn, string entryId, string kind, SqliteTransaction? tx = null)
    {
        RequirePinKind(kind);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM git_sync_review WHERE entry_id = $id AND kind = $kind";
        cmd.Parameters.AddWithValue("$id", entryId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The pin members touch only the two pin kinds: any other kind is a programming error, never a statement against another kind's rows.</summary>
    private static void RequirePinKind(string kind)
    {
        if (kind is not ("new-pin" or "pin-edit"))
            throw new ArgumentException("a pin item is a new-pin or a pin-edit", nameof(kind));
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
    private static string CausePayload(string cause) => StringPayload("cause", cause);

    /// <summary><c>{"&lt;name&gt;":"&lt;value&gt;"}</c>, or <c>{}</c> when the value is null, written without reflection.</summary>
    private static string StringPayload(string name, string? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (value is not null)
                writer.WriteString(name, value);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ParseCause(string payload) => ParseString(payload, "cause");

    /// <summary>The string member <paramref name="name"/> of a payload, or null when the payload is not a JSON object holding it.</summary>
    private static string? ParseString(string payload, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(name, out var member)
                && member.ValueKind == JsonValueKind.String
                    ? member.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
