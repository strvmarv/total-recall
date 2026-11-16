using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncRowsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tr-rows-" + Guid.NewGuid().ToString("N"));
    private readonly MsSqliteConnection _conn;

    public GitSyncRowsTests()
    {
        _conn = SyncDb.OpenMigrated(Path.Combine(_dir, "total-recall.db"));
    }

    public void Dispose()
    {
        _conn.Dispose();
        MsSqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static SyncStamp.Stamp Stamp(int n) =>
        SyncStamp.StampModule.tryParse($"2026-11-17T10:00:{n:00}.000Z.000000.4f2a9c1e").Value;

    private static GitSyncRow Empty(string id, SyncMerge.RowState state) =>
        new(id, state, null, null, null, null, null, null, null, null, null, null, null, null, null);

    private static GitSyncRow Full(string id) =>
        new(id, SyncMerge.RowState.Active, "{\"base\":1}", "ch", "th", true, Tier.Warm, Stamp(1), Stamp(2), Stamp(3),
            "content,tier", "[\"a\"]", "bh", "secret", "ah");

    [Fact]
    public void Write_ThenReadAll_RoundTripsEveryColumn()
    {
        GitSyncRows.Write(_conn, Full("3f9c2a7e"));
        GitSyncRows.Write(_conn, Empty("aaaa1111", SyncMerge.RowState.Left));

        var rows = GitSyncRows.ReadAll(_conn);

        Assert.Equal(2, rows.Count);
        Assert.Equal(Full("3f9c2a7e"), rows["3f9c2a7e"]);
        Assert.Equal(Empty("aaaa1111", SyncMerge.RowState.Left), rows["aaaa1111"]);
    }

    [Fact]
    public void Write_TwiceWithTheSameId_UpdatesInPlace()
    {
        GitSyncRows.Write(_conn, Full("3f9c2a7e"));
        GitSyncRows.Write(_conn, Empty("3f9c2a7e", SyncMerge.RowState.Forgotten));

        var rows = GitSyncRows.ReadAll(_conn);

        Assert.Single(rows);
        Assert.Equal(Empty("3f9c2a7e", SyncMerge.RowState.Forgotten), rows["3f9c2a7e"]);
    }

    [Fact]
    public void State_RoundTrips_ForActiveForgottenAndLeft()
    {
        GitSyncRows.Write(_conn, Empty("aaaa1111", SyncMerge.RowState.Active));
        GitSyncRows.Write(_conn, Empty("bbbb2222", SyncMerge.RowState.Forgotten));
        GitSyncRows.Write(_conn, Empty("cccc3333", SyncMerge.RowState.Left));

        var rows = GitSyncRows.ReadAll(_conn);

        Assert.True(rows["aaaa1111"].State.IsActive);
        Assert.True(rows["bbbb2222"].State.IsForgotten);
        Assert.True(rows["cccc3333"].State.IsLeft);
    }

    [Fact]
    public void Tiers_RoundTrip_AndStampsAreStoredAsFormattedText()
    {
        foreach (var (id, tier) in new[] { ("aaaa1111", Tier.Hot), ("bbbb2222", Tier.Warm), ("cccc3333", Tier.Cold) })
            GitSyncRows.Write(_conn, Empty(id, SyncMerge.RowState.Active) with { LocalTier = tier, ContentStamp = Stamp(7) });

        var rows = GitSyncRows.ReadAll(_conn);

        Assert.True(rows["aaaa1111"].LocalTier!.IsHot);
        Assert.True(rows["bbbb2222"].LocalTier!.IsWarm);
        Assert.True(rows["cccc3333"].LocalTier!.IsCold);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT content_stamp, local_tier FROM git_sync_entry WHERE entry_id = 'aaaa1111'";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("2026-11-17T10:00:07.000Z.000000.4f2a9c1e", r.GetString(0));
        Assert.Equal("hot", r.GetString(1));
    }

    [Fact]
    public void LocalSticky_RoundTrips_AsNullTrueAndFalse()
    {
        GitSyncRows.Write(_conn, Empty("aaaa1111", SyncMerge.RowState.Active));
        GitSyncRows.Write(_conn, Empty("bbbb2222", SyncMerge.RowState.Active) with { LocalSticky = true });
        GitSyncRows.Write(_conn, Empty("cccc3333", SyncMerge.RowState.Active) with { LocalSticky = false });

        var rows = GitSyncRows.ReadAll(_conn);

        Assert.Null(rows["aaaa1111"].LocalSticky);
        Assert.True(rows["bbbb2222"].LocalSticky);
        Assert.False(rows["cccc3333"].LocalSticky);
    }

    [Fact]
    public void AnUnknownStoredState_ThrowsFormatException()
    {
        GitSyncRows.Write(_conn, Empty("aaaa1111", SyncMerge.RowState.Active));
        Exec("PRAGMA ignore_check_constraints = 1");
        Exec("UPDATE git_sync_entry SET state = 'bogus'");
        Exec("PRAGMA ignore_check_constraints = 0");

        Assert.Throws<FormatException>(() => GitSyncRows.ReadAll(_conn));
    }

    [Fact]
    public void AnUnknownStoredTier_ThrowsFormatException()
    {
        GitSyncRows.Write(_conn, Empty("aaaa1111", SyncMerge.RowState.Active));
        Exec("UPDATE git_sync_entry SET local_tier = 'lukewarm'");

        Assert.Throws<FormatException>(() => GitSyncRows.ReadAll(_conn));
    }

    [Fact]
    public void AStampThatDoesNotParse_ReadsAsNone()
    {
        GitSyncRows.Write(_conn, Full("3f9c2a7e"));
        Exec("UPDATE git_sync_entry SET content_stamp = 'not a stamp', sticky_stamp = ''");

        var row = GitSyncRows.ReadAll(_conn)["3f9c2a7e"];

        Assert.Null(row.ContentStamp);
        Assert.Null(row.StickyStamp);
        Assert.NotNull(row.TierStamp);
    }

    [Fact]
    public void Write_InsideARawTransaction_IsRolledBackWithIt()
    {
        Exec("BEGIN IMMEDIATE");
        GitSyncRows.Write(_conn, Full("3f9c2a7e"));
        Exec("ROLLBACK");

        Assert.Empty(GitSyncRows.ReadAll(_conn));
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
