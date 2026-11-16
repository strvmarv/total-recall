using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using SyncRecordModule = TotalRecall.Core.SyncRecord;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>One <c>git_sync_entry</c> row, typed. A <c>null</c> member stands for a NULL column ("nothing seen" or "none").</summary>
public sealed record GitSyncRow(
    string EntryId,
    SyncMerge.RowState State,
    string? BaseJson,
    string? ScannedContentHash,
    string? ScannedTagsHash,
    bool? LocalSticky,
    Tier? LocalTier,
    SyncStamp.Stamp? ContentStamp,
    SyncStamp.Stamp? StickyStamp,
    SyncStamp.Stamp? TierStamp,
    string? RepushGroups,
    string? PushingStamps,
    string? BlockedHash,
    string? BlockedReason,
    string? AllowedHash);

/// <summary>
/// Typed access to <c>git_sync_entry</c>: read every row, and write one (an upsert by <c>entry_id</c>). Plain SQL on
/// the caller's connection; the commands carry no transaction, so a caller that has run a raw <c>BEGIN</c> on the
/// connection gets that transaction. Reading, inserting and updating only: this milestone deletes no row.
/// </summary>
public static class GitSyncRows
{
    private const string Columns =
        "entry_id, state, base_json, scanned_content_hash, scanned_tags_hash, local_sticky, local_tier, content_stamp, " +
        "sticky_stamp, tier_stamp, repush_groups, pushing_stamps, blocked_hash, blocked_reason, allowed_hash";

    /// <summary>Every row, by entry id (ordinal). Throws <see cref="FormatException"/> for a state or tier text this code does not know.</summary>
    public static Dictionary<string, GitSyncRow> ReadAll(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        var rows = new Dictionary<string, GitSyncRow>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM git_sync_entry";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new GitSyncRow(
                EntryId: r.GetString(0),
                State: ParseState(r.GetString(1)),
                BaseJson: Text(r, 2),
                ScannedContentHash: Text(r, 3),
                ScannedTagsHash: Text(r, 4),
                LocalSticky: r.IsDBNull(5) ? null : r.GetInt64(5) != 0,
                LocalTier: ParseTier(Text(r, 6)),
                ContentStamp: ParseStamp(Text(r, 7)),
                StickyStamp: ParseStamp(Text(r, 8)),
                TierStamp: ParseStamp(Text(r, 9)),
                RepushGroups: Text(r, 10),
                PushingStamps: Text(r, 11),
                BlockedHash: Text(r, 12),
                BlockedReason: Text(r, 13),
                AllowedHash: Text(r, 14));
            rows[row.EntryId] = row;
        }
        return rows;
    }

    /// <summary>Inserts the row, or replaces every column of the row with the same <c>entry_id</c>.</summary>
    public static void Write(MsSqliteConnection conn, GitSyncRow row)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(row);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO git_sync_entry ({Columns}) VALUES ($id, $state, $base, $ch, $th, $sticky, $tier, $cs, $ss, $ts, $rg, $ps, $bh, $br, $ah) " +
            "ON CONFLICT(entry_id) DO UPDATE SET state = excluded.state, base_json = excluded.base_json, " +
            "scanned_content_hash = excluded.scanned_content_hash, scanned_tags_hash = excluded.scanned_tags_hash, " +
            "local_sticky = excluded.local_sticky, local_tier = excluded.local_tier, content_stamp = excluded.content_stamp, " +
            "sticky_stamp = excluded.sticky_stamp, tier_stamp = excluded.tier_stamp, repush_groups = excluded.repush_groups, " +
            "pushing_stamps = excluded.pushing_stamps, blocked_hash = excluded.blocked_hash, " +
            "blocked_reason = excluded.blocked_reason, allowed_hash = excluded.allowed_hash";
        cmd.Parameters.AddWithValue("$id", row.EntryId);
        cmd.Parameters.AddWithValue("$state", StateName(row.State));
        cmd.Parameters.AddWithValue("$base", (object?)row.BaseJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ch", (object?)row.ScannedContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$th", (object?)row.ScannedTagsHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sticky", row.LocalSticky is { } s ? (s ? 1L : 0L) : DBNull.Value);
        cmd.Parameters.AddWithValue("$tier", row.LocalTier is { } t ? SyncRecordModule.tierName(t) : DBNull.Value);
        cmd.Parameters.AddWithValue("$cs", Stamp(row.ContentStamp));
        cmd.Parameters.AddWithValue("$ss", Stamp(row.StickyStamp));
        cmd.Parameters.AddWithValue("$ts", Stamp(row.TierStamp));
        cmd.Parameters.AddWithValue("$rg", (object?)row.RepushGroups ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ps", (object?)row.PushingStamps ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bh", (object?)row.BlockedHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$br", (object?)row.BlockedReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ah", (object?)row.AllowedHash ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static object Stamp(SyncStamp.Stamp? s) =>
        s is null ? DBNull.Value : SyncStamp.StampModule.format(s);

    /// <summary>A stamp text that does not parse reads as none (the same as a NULL column).</summary>
    private static SyncStamp.Stamp? ParseStamp(string? text)
    {
        if (text is null) return null;
        var parsed = SyncStamp.StampModule.tryParse(text);
        return FSharpOption<SyncStamp.Stamp>.get_IsSome(parsed) ? parsed.Value : null;
    }

    private static Tier? ParseTier(string? text)
    {
        if (text is null) return null;
        var parsed = SyncRecordModule.tryParseTier(text);
        if (!FSharpOption<Tier>.get_IsSome(parsed))
            throw new FormatException($"git_sync_entry.local_tier holds an unknown tier: '{text}'.");
        return parsed.Value;
    }

    private static SyncMerge.RowState ParseState(string text) => text switch
    {
        "active" => SyncMerge.RowState.Active,
        "forgotten" => SyncMerge.RowState.Forgotten,
        "left" => SyncMerge.RowState.Left,
        _ => throw new FormatException($"git_sync_entry.state holds an unknown state: '{text}'."),
    };

    private static string StateName(SyncMerge.RowState state) =>
        state.IsActive ? "active"
        : state.IsForgotten ? "forgotten"
        : state.IsLeft ? "left"
        : throw new ArgumentOutOfRangeException(nameof(state));
}
