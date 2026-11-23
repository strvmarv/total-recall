using TotalRecall.Infrastructure.GitSync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Milestone 1b-ii, decisions 7 and 8: the <c>held-delete</c> rows of <c>git_sync_review</c>.</summary>
public sealed class GitSyncReviewsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tr-reviews-" + Guid.NewGuid().ToString("N"));
    private readonly MsSqliteConnection _conn;

    public GitSyncReviewsTests() => _conn = SyncDb.OpenMigrated(Path.Combine(_dir, "total-recall.db"));

    public void Dispose()
    {
        _conn.Dispose();
        MsSqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string? Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() is { } v and not DBNull ? v.ToString() : null;
    }

    [Fact]
    public void Upsert_then_read_returns_the_open_item()
    {
        var item = new HeldItem("3f9c2a7e", "remote", "blob1", "guard", 1_800_000_000_000L);

        GitSyncReviews.UpsertHeld(_conn, item);

        var open = GitSyncReviews.ReadOpenHeld(_conn);
        Assert.Equal(["3f9c2a7e"], open.Keys.ToArray());
        Assert.Equal(item, open["3f9c2a7e"]);
        Assert.Equal("open", Scalar("SELECT state FROM git_sync_review WHERE entry_id = '3f9c2a7e'"));
        Assert.Equal("held-delete", Scalar("SELECT kind FROM git_sync_review WHERE entry_id = '3f9c2a7e'"));
        Assert.Equal("""{"cause":"guard"}""", Scalar("SELECT payload_json FROM git_sync_review WHERE entry_id = '3f9c2a7e'"));
    }

    [Fact]
    public void A_missing_cause_and_hash_round_trip_as_null()
    {
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "local", null, null, 5));

        var item = GitSyncReviews.ReadOpenHeld(_conn)["k"];

        Assert.Equal(new HeldItem("k", "local", null, null, 5), item);
        Assert.Null(Scalar("SELECT payload_json FROM git_sync_review WHERE entry_id = 'k'"));
    }

    [Fact]
    public void Upsert_replaces_an_existing_item_of_the_other_direction()
    {
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "remote", "blob1", "guard", 10));

        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "local", null, "sticky", 20));

        var open = GitSyncReviews.ReadOpenHeld(_conn);
        Assert.Single(open);
        Assert.Equal(new HeldItem("k", "local", null, "sticky", 20), open["k"]);
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM git_sync_review"));
    }

    [Fact]
    public void Delete_removes_only_held_delete_rows()
    {
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "remote", "blob1", "unproven", 10));
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("other", "local", null, "guard", 10));
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO git_sync_review (entry_id, kind, state, arrived_at) VALUES ('k', 'new-pin', 'open', 1)";
            cmd.ExecuteNonQuery();
        }

        GitSyncReviews.DeleteHeld(_conn, "k");
        GitSyncReviews.DeleteHeld(_conn, "missing");

        Assert.Equal(["other"], GitSyncReviews.ReadOpenHeld(_conn).Keys.ToArray());
        Assert.Equal("new-pin", Scalar("SELECT kind FROM git_sync_review WHERE entry_id = 'k'"));
    }

    [Fact]
    public void Only_open_held_delete_rows_are_read()
    {
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "remote", null, "guard", 10));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO git_sync_review (entry_id, kind, state, arrived_at) VALUES ('p', 'new-pin', 'open', 1); " +
            "INSERT INTO git_sync_review (entry_id, kind, state, direction, arrived_at) VALUES ('r', 'held-delete', 'resolved', 'remote', 1)";
        cmd.ExecuteNonQuery();

        Assert.Equal(["k"], GitSyncReviews.ReadOpenHeld(_conn).Keys.ToArray());
    }

    [Fact]
    public void One_entrys_open_item_is_read_by_its_id()
    {
        var item = new HeldItem("k", "local", "blob", "unproven", 10);
        GitSyncReviews.UpsertHeld(_conn, item);
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("other", "remote", null, "guard", 11));
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO git_sync_review (entry_id, kind, state, direction, arrived_at) VALUES ('r', 'held-delete', 'resolved', 'remote', 1)";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(item, GitSyncReviews.ReadOpenHeldItem(_conn, "k"));
        Assert.Null(GitSyncReviews.ReadOpenHeldItem(_conn, "r")); // not open
        Assert.Null(GitSyncReviews.ReadOpenHeldItem(_conn, "none"));
    }

    [Fact]
    public void Rows_follow_the_callers_transaction()
    {
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("kept", "remote", null, "guard", 1));

        using (var tx = _conn.BeginTransaction())
        {
            GitSyncReviews.UpsertHeld(_conn, new HeldItem("new", "local", null, "sticky", 2), tx);
            GitSyncReviews.DeleteHeld(_conn, "kept", tx);
            Assert.Equal(["new"], GitSyncReviews.ReadOpenHeld(_conn, tx).Keys.ToArray());
            tx.Rollback();
        }

        Assert.Equal(["kept"], GitSyncReviews.ReadOpenHeld(_conn).Keys.ToArray());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"cause":5}""")]
    [InlineData("""{"other":"guard"}""")]
    [InlineData("[]")]
    public void Unparsable_payload_reads_as_no_cause(string payload)
    {
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO git_sync_review (entry_id, kind, state, direction, payload_json, arrived_at) VALUES ('k', 'held-delete', 'open', 'remote', $p, 1)";
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.ExecuteNonQuery();
        }

        Assert.Null(GitSyncReviews.ReadOpenHeld(_conn)["k"].Cause);
    }

    private void InsertHeld(string id, string state, string direction = "remote")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO git_sync_review (entry_id, kind, state, direction, arrived_at) VALUES ($id, 'held-delete', $state, $direction, 1)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$direction", direction);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ReadHeldItem_ReadsAnApprovedItem_WithItsState()
    {
        var item = new HeldItem("k", "local", "blob", "unproven", 10);
        GitSyncReviews.UpsertHeld(_conn, item);
        Assert.Equal(item, GitSyncReviews.ReadHeldItem(_conn, "k"));

        GitSyncReviews.ApproveHeld(_conn, "k");

        Assert.Equal(item with { State = "approved" }, GitSyncReviews.ReadHeldItem(_conn, "k"));
        Assert.Null(GitSyncReviews.ReadHeldItem(_conn, "none"));
    }

    [Fact]
    public void ReadOpenHeld_DoesNotReadAnApprovedItem()
    {
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "remote", null, "guard", 10));
        GitSyncReviews.ApproveHeld(_conn, "k");

        Assert.Empty(GitSyncReviews.ReadOpenHeld(_conn));
        Assert.Null(GitSyncReviews.ReadOpenHeldItem(_conn, "k"));
    }

    [Fact]
    public void ReadApprovedHeld_ReadsOnlyApproved()
    {
        InsertHeld("o", "open");
        InsertHeld("a", "approved");
        InsertHeld("r", "resolved");

        var approved = GitSyncReviews.ReadApprovedHeld(_conn);

        Assert.Equal(["a"], approved.Keys.ToArray());
        Assert.Equal("approved", approved["a"].State);
    }

    [Fact]
    public void Reopen_SetsOpenAndTheHash_KeepingCauseAndArrivedAt()
    {
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "local", "old", "sticky", 77));
        GitSyncReviews.ApproveHeld(_conn, "k");

        GitSyncReviews.ReopenHeld(_conn, "k", "new");

        Assert.Equal(new HeldItem("k", "local", "new", "sticky", 77), GitSyncReviews.ReadOpenHeld(_conn)["k"]);
    }

    [Fact]
    public void RefreshHeldHash_ChangesOnlyTheHash()
    {
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "local", "old", "unproven", 77));
        GitSyncReviews.ApproveHeld(_conn, "k");

        GitSyncReviews.RefreshHeldHash(_conn, "k", "new");

        Assert.Equal(
            new HeldItem("k", "local", "new", "unproven", 77, "approved"),
            GitSyncReviews.ReadHeldItem(_conn, "k"));
    }

    [Fact]
    public void UpsertNewPin_WritesAnOpenNewPinRow_AndASecondReplacesIt()
    {
        var held = new HeldItem("k", "local", "blob", "guard", 5);
        GitSyncReviews.UpsertHeld(_conn, held);

        GitSyncReviews.UpsertNewPin(_conn, "k", "hash1", 100);
        GitSyncReviews.UpsertNewPin(_conn, "k", "hash2", 200);

        const string where = " FROM git_sync_review WHERE entry_id = 'k' AND kind = 'new-pin'";
        Assert.Equal("1", Scalar("SELECT COUNT(*)" + where));
        Assert.Equal("hash2", Scalar("SELECT record_hash" + where));
        Assert.Equal("open", Scalar("SELECT state" + where));
        Assert.Equal("200", Scalar("SELECT arrived_at" + where));
        Assert.Equal("""{"origin":"restore"}""", Scalar("SELECT payload_json" + where));
        Assert.Null(Scalar("SELECT direction" + where));
        Assert.Equal(held, GitSyncReviews.ReadOpenHeldItem(_conn, "k"));
        Assert.Equal(held, GitSyncReviews.ReadHeldItem(_conn, "k"));
        Assert.Equal(["k"], GitSyncReviews.ReadOpenHeld(_conn).Keys.ToArray());
    }

    [Fact]
    public void ApproveHeld_OnAMissingRow_IsANoOp()
    {
        GitSyncReviews.ApproveHeld(_conn, "none");
        GitSyncReviews.ReopenHeld(_conn, "none", "h");
        GitSyncReviews.RefreshHeldHash(_conn, "none", "h");

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM git_sync_review"));
    }
}
