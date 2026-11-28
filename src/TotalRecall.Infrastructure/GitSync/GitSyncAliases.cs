using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// One <c>git_sync_alias</c> row (git sync part 5, #165): <paramref name="OtherId"/> holds the same memory as
/// <paramref name="CanonicalId"/> (the ordinal minimum of its group). <paramref name="OriginHash"/> is the origin hash the
/// group was detected by (nullable in the schema; this part never writes NULL); <paramref name="State"/> is <c>open</c>,
/// <c>converged</c>, <c>resolved</c> or <c>abandoned</c>.
/// </summary>
internal sealed record AliasRow(string CanonicalId, string OtherId, string? OriginHash, string State);

/// <summary>
/// The skip of D1, planned before the entry's transaction and recomputed inside it: the record carrying origin hash
/// <paramref name="Hash"/> aliases the local ids <paramref name="Targets"/>, the group's canonical id is
/// <paramref name="Canonical"/>, and <paramref name="Inserts"/> are the other ids of the <c>open</c> rows
/// <c>(Canonical, other)</c> to write (ordinal). The two plans must <see cref="Matches"/>, else the entry is deferred.
/// </summary>
internal sealed record AliasPlan(string Hash, string Canonical, IReadOnlyList<string> Targets, IReadOnlyList<string> Inserts)
{
    /// <summary>True when there is no row to write (a repeated pull: no transaction is needed).</summary>
    public bool IsEmpty => Inserts.Count == 0;

    /// <summary>Every member equal (ordinal), the two lists element by element; false for null.</summary>
    public bool Matches(AliasPlan? other) =>
        other is not null
        && string.Equals(Hash, other.Hash, StringComparison.Ordinal)
        && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal)
        && Targets.SequenceEqual(other.Targets, StringComparer.Ordinal)
        && Inserts.SequenceEqual(other.Inserts, StringComparer.Ordinal);
}

/// <summary>
/// The <c>git_sync_alias</c> rows and the <c>import_log</c> reads of alias detection (D1) and convergence (D3 steps 5 and 7).
/// Plain SQL on the caller's connection; every command carries the optional transaction, so the statements run inside an
/// entry's <see cref="SqliteTransaction"/>. Ids are compared and ordered ordinally.
/// </summary>
internal static class GitSyncAliases
{
    private static readonly Tier[] Tiers = [Tier.Hot, Tier.Warm, Tier.Cold];

    private const string Columns = "canonical_id, other_id, origin_hash, state";

    /// <summary><c>EXISTS</c> over the three memory tables for the id expression <paramref name="idExpr"/>.</summary>
    private static string ExistsAsMemory(string idExpr) =>
        "(" + string.Join(
            " OR ",
            Tiers.Select(t => $"EXISTS (SELECT 1 FROM {MigrationRunner.TableName(t, ContentType.Memory)} m WHERE m.id = {idExpr})")) + ")";

    /// <summary>Every row, any state, ordinal by canonical id, then other id.</summary>
    public static IReadOnlyList<AliasRow> ReadAll(MsSqliteConnection conn, SqliteTransaction? tx = null) =>
        Read(conn, tx, $"SELECT {Columns} FROM git_sync_alias ORDER BY canonical_id COLLATE BINARY, other_id COLLATE BINARY");

    /// <summary>The <c>open</c> rows with origin hash <paramref name="hash"/> (a NULL hash never matches), ordinal as <see cref="ReadAll"/>.</summary>
    public static IReadOnlyList<AliasRow> ReadOpenByHash(MsSqliteConnection conn, string hash, SqliteTransaction? tx = null) =>
        Read(
            conn, tx,
            $"SELECT {Columns} FROM git_sync_alias WHERE state = 'open' AND origin_hash = $h " +
            "ORDER BY canonical_id COLLATE BINARY, other_id COLLATE BINARY",
            ("$h", hash));

    /// <summary>The <c>open</c> rows whose other id is <paramref name="otherId"/>, ordinal by canonical id.</summary>
    public static IReadOnlyList<AliasRow> ReadOpenOf(MsSqliteConnection conn, string otherId, SqliteTransaction? tx = null) =>
        Read(
            conn, tx,
            $"SELECT {Columns} FROM git_sync_alias WHERE state = 'open' AND other_id = $x ORDER BY canonical_id COLLATE BINARY",
            ("$x", otherId));

    private static List<AliasRow> Read(
        MsSqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, string Value)[] parameters)
    {
        var rows = new List<AliasRow>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new AliasRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)));
        return rows;
    }

    /// <summary>
    /// D1's alias targets of a record: the <c>target_entry_id</c> of every memory <c>import_log</c> row with content hash
    /// <paramref name="hash"/>, from any <c>source_tool</c>, that exists as a memory in one of the three tiers, is an eligible
    /// id and differs from <paramref name="recordId"/>; distinct, ordinal.
    /// </summary>
    public static IReadOnlyList<string> ImportTargets(
        MsSqliteConnection conn, string hash, string recordId, SqliteTransaction? tx = null)
    {
        var ids = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT DISTINCT l.target_entry_id FROM import_log l " +
            "WHERE l.content_hash = $h AND l.target_type = 'memory' AND " + ExistsAsMemory("l.target_entry_id");
        cmd.Parameters.AddWithValue("$h", hash);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.GetString(0);
            if (SyncValidation.isEligibleId(id) && !string.Equals(id, recordId, StringComparison.Ordinal))
                ids.Add(id);
        }
        return Ordinal(ids);
    }

    /// <summary>The content hashes of the memory <c>import_log</c> rows of <paramref name="entryId"/>, any <c>source_tool</c>; distinct, ordinal.</summary>
    public static IReadOnlyList<string> HashesOf(MsSqliteConnection conn, string entryId, SqliteTransaction? tx = null)
    {
        var hashes = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT DISTINCT content_hash FROM import_log WHERE target_entry_id = $id AND target_type = 'memory'";
        cmd.Parameters.AddWithValue("$id", entryId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            hashes.Add(r.GetString(0));
        return Ordinal(hashes);
    }

    /// <summary>True when a memory with this id exists in the hot, warm or cold table (a knowledge entry is not a memory).</summary>
    public static bool MemoryExists(MsSqliteConnection conn, string id, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT " + ExistsAsMemory("$id");
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    /// <summary>Writes the row <c>(canonical, other, hash, 'open')</c>; an existing row of the pair, in any state, is left as it is (D1).</summary>
    public static void InsertOpen(
        MsSqliteConnection conn, string canonical, string other, string hash, SqliteTransaction? tx = null) =>
        Write(
            conn, tx,
            "INSERT OR IGNORE INTO git_sync_alias (canonical_id, other_id, origin_hash, state) VALUES ($c, $o, $h, 'open')",
            canonical, other, hash);

    /// <summary>
    /// Writes the row <c>(canonical, other, hash, 'resolved')</c>, or sets an existing row of the pair to <c>resolved</c>
    /// keeping its origin hash (D3 step 7).
    /// </summary>
    public static void MarkResolved(
        MsSqliteConnection conn, string canonical, string other, string hash, SqliteTransaction? tx = null) =>
        Write(
            conn, tx,
            "INSERT INTO git_sync_alias (canonical_id, other_id, origin_hash, state) VALUES ($c, $o, $h, 'resolved') " +
            "ON CONFLICT(canonical_id, other_id) DO UPDATE SET state = 'resolved'",
            canonical, other, hash);

    private static void Write(
        MsSqliteConnection conn, SqliteTransaction? tx, string sql, string canonical, string other, string hash)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$c", canonical);
        cmd.Parameters.AddWithValue("$o", other);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Points the memory <c>import_log</c> rows of <paramref name="fromId"/> at <paramref name="toId"/> (D3 step 5); the
    /// rows' own ids (<c>MD5(tool:path:hash)</c>) and every knowledge row stay.
    /// </summary>
    public static void RetargetImports(MsSqliteConnection conn, string fromId, string toId, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "UPDATE import_log SET target_entry_id = $to WHERE target_entry_id = $from AND target_type = 'memory'";
        cmd.Parameters.AddWithValue("$to", toId);
        cmd.Parameters.AddWithValue("$from", fromId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// D1's skip for record <paramref name="recordId"/> with origin hash <paramref name="hash"/>: null when the record aliases
    /// no local id (<see cref="ImportTargets"/> is empty), else the group's canonical id, the targets and the open rows to
    /// write (<see cref="SyncAlias.detect"/> over the open rows of the hash, those of their ids that exist here as a memory,
    /// and every existing row).
    /// </summary>
    public static AliasPlan? PlanSkip(MsSqliteConnection conn, string recordId, string hash, SqliteTransaction? tx = null)
    {
        var targets = ImportTargets(conn, hash, recordId, tx);
        if (targets.Count == 0)
            return null;

        var open = ReadOpenByHash(conn, hash, tx);
        var members = open
            .SelectMany(r => new[] { r.CanonicalId, r.OtherId })
            .Distinct(StringComparer.Ordinal)
            .Where(id => MemoryExists(conn, id, tx));
        var existing = ReadAll(conn, tx).Select(r => Tuple.Create(r.CanonicalId, r.OtherId));

        var input = new SyncAlias.DetectInput(
            recordId,
            ListModule.OfSeq(targets),
            ListModule.OfSeq(open.Select(r => Tuple.Create(r.CanonicalId, r.OtherId))),
            SetModule.OfSeq(members),
            SetModule.OfSeq(existing));
        var detection = SyncAlias.detect(input);
        if (detection is null)
            return null;
        var d = detection.Value;
        return new AliasPlan(hash, d.Canonical, targets, d.Rows.Select(r => r.Item2).ToList());
    }

    private static List<string> Ordinal(IEnumerable<string> ids)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
