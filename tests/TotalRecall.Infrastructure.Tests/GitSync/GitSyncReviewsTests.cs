using TotalRecall.Core;
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

    [Theory]
    [InlineData("unseen")]
    [InlineData("declined")]
    [InlineData("resolved")]
    public void ReadHeldItem_DoesNotReadARowThatIsNeitherOpenNorApproved(string state)
    {
        InsertHeld("k", state);

        Assert.Null(GitSyncReviews.ReadHeldItem(_conn, "k"));
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

    private void InsertRow(string id, string kind, string state, string? direction = null, string? hash = null, string? machine = null, string? payload = "{}")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO git_sync_review (entry_id, kind, state, record_hash, direction, machine, payload_json, arrived_at) " +
            "VALUES ($id, $kind, $state, $hash, $direction, $machine, $payload, 7)";
        cmd.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$direction", (object?)direction ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$machine", (object?)machine ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ReadPinItems_ReadsBothKindsInEveryAnswerState_AndNoHeldDelete()
    {
        InsertRow("d", "new-pin", "open", hash: "h1", machine: "m1");
        InsertRow("c", "pin-edit", "unseen", hash: "h2");
        InsertRow("b", "new-pin", "declined", hash: "h3");
        InsertRow("a", "pin-edit", "approved", hash: "h4");
        InsertRow("a", "held-delete", "open", direction: "remote");
        InsertRow("e", "pin-edit", "resolved", hash: "h5");

        var items = GitSyncReviews.ReadPinItems(_conn);

        Assert.Equal(
            [
                new PinItem("a", "pin-edit", "approved", "h4", null, 7),
                new PinItem("b", "new-pin", "declined", "h3", null, 7),
                new PinItem("c", "pin-edit", "unseen", "h2", null, 7),
                new PinItem("d", "new-pin", "open", "h1", "m1", 7),
            ],
            items);
    }

    [Fact]
    public void ReadPinItem_ReadsOneByIdAndKind()
    {
        InsertRow("k", "new-pin", "open", hash: "h1");
        InsertRow("k", "pin-edit", "unseen", hash: "h2", machine: "m");
        InsertRow("r", "new-pin", "resolved", hash: "h3");
        InsertRow("k", "held-delete", "open", direction: "local");

        Assert.Equal(new PinItem("k", "new-pin", "open", "h1", null, 7), GitSyncReviews.ReadPinItem(_conn, "k", "new-pin"));
        Assert.Equal(new PinItem("k", "pin-edit", "unseen", "h2", "m", 7), GitSyncReviews.ReadPinItem(_conn, "k", "pin-edit"));
        Assert.Null(GitSyncReviews.ReadPinItem(_conn, "r", "new-pin")); // resolved is not an item
        Assert.Null(GitSyncReviews.ReadPinItem(_conn, "k", "held-delete"));
        Assert.Null(GitSyncReviews.ReadPinItem(_conn, "none", "new-pin"));
    }

    [Fact]
    public void ReadPinItem_ReadsTheOriginOfTheRestorePayload()
    {
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "new-pin", "open", "h", "m", 5, Origin: "restore"));

        Assert.Equal("restore", GitSyncReviews.ReadPinItem(_conn, "k", "new-pin")!.Origin);
        Assert.Equal("restore", GitSyncReviews.ReadPinItems(_conn).Single().Origin);
    }

    [Fact]
    public void UpsertPinItem_WritesTheItem_AndASecondReplacesIt()
    {
        var held = new HeldItem("k", "local", "blob", "guard", 5);
        GitSyncReviews.UpsertHeld(_conn, held);

        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "new-pin", "open", "h1", "m1", 100));
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "new-pin", "open", "h2", "m", 200));

        const string where = " FROM git_sync_review WHERE entry_id = 'k' AND kind = 'new-pin'";
        Assert.Equal("1", Scalar("SELECT COUNT(*)" + where));
        Assert.Equal(
            "open|h2|m|NULL|{}|200",
            Scalar(
                "SELECT state || '|' || record_hash || '|' || machine || '|' || COALESCE(direction, 'NULL') || '|' || payload_json || '|' || arrived_at" + where));

        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "new-pin", "unseen", "h3", "m2", 300, Origin: "restore"));
        Assert.Equal("unseen", Scalar("SELECT state" + where));
        Assert.Equal("m2", Scalar("SELECT machine" + where));
        Assert.Equal("""{"origin":"restore"}""", Scalar("SELECT payload_json" + where));
        Assert.Equal("300", Scalar("SELECT arrived_at" + where));

        Assert.Equal(held, GitSyncReviews.ReadOpenHeldItem(_conn, "k"));
        Assert.Equal(held, GitSyncReviews.ReadHeldItem(_conn, "k"));
        Assert.Equal(["k"], GitSyncReviews.ReadOpenHeld(_conn).Keys.ToArray());
    }

    [Fact]
    public void MarkPinUnseen_SetsStateHashAndMachine_KeepingPayloadAndArrivedAt()
    {
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "pin-edit", "open", "h1", "m1", 100, Origin: "restore"));
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "new-pin", "open", "n1", "m1", 100));

        GitSyncReviews.MarkPinUnseen(_conn, "k", "pin-edit", "h2", null);

        const string where = " FROM git_sync_review WHERE entry_id = 'k' AND kind = ";
        Assert.Equal("unseen", Scalar("SELECT state" + where + "'pin-edit'"));
        Assert.Equal("h2", Scalar("SELECT record_hash" + where + "'pin-edit'"));
        Assert.Null(Scalar("SELECT machine" + where + "'pin-edit'"));
        Assert.Equal("""{"origin":"restore"}""", Scalar("SELECT payload_json" + where + "'pin-edit'"));
        Assert.Equal("100", Scalar("SELECT arrived_at" + where + "'pin-edit'"));
        Assert.Equal("open", Scalar("SELECT state" + where + "'new-pin'")); // the other kind is untouched
        Assert.Equal("n1", Scalar("SELECT record_hash" + where + "'new-pin'"));
    }

    [Fact]
    public void ResolvePinItem_SetsOnlyTheState()
    {
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "new-pin", "open", "h1", "m1", 100, Origin: "restore"));
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "pin-edit", "open", "h2", "m1", 100));
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "local", "blob", "guard", 5));

        GitSyncReviews.ResolvePinItem(_conn, "k", "new-pin", "approved");

        Assert.Equal(new PinItem("k", "new-pin", "approved", "h1", "m1", 100, "restore"), GitSyncReviews.ReadPinItem(_conn, "k", "new-pin"));
        GitSyncReviews.ResolvePinItem(_conn, "k", "new-pin", "declined");
        Assert.Equal("declined", GitSyncReviews.ReadPinItem(_conn, "k", "new-pin")!.State);
        Assert.Equal("open", GitSyncReviews.ReadPinItem(_conn, "k", "pin-edit")!.State);
        Assert.Equal(new HeldItem("k", "local", "blob", "guard", 5), GitSyncReviews.ReadHeldItem(_conn, "k"));
        Assert.Throws<ArgumentException>(() => GitSyncReviews.ResolvePinItem(_conn, "k", "new-pin", "open"));
        Assert.Throws<ArgumentException>(() => GitSyncReviews.ResolvePinItem(_conn, "k", "new-pin", "resolved"));
        Assert.Equal("declined", GitSyncReviews.ReadPinItem(_conn, "k", "new-pin")!.State);
    }

    [Fact]
    public void DeletePinItem_RemovesOnlyThatKind()
    {
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "new-pin", "open", "h1", null, 1));
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("k", "pin-edit", "open", "h2", null, 1));
        GitSyncReviews.UpsertHeld(_conn, new HeldItem("k", "remote", null, "guard", 1));
        InsertRow("k", "conflict", "open");

        GitSyncReviews.DeletePinItem(_conn, "k", "new-pin");
        GitSyncReviews.DeletePinItem(_conn, "missing", "new-pin");

        Assert.Null(GitSyncReviews.ReadPinItem(_conn, "k", "new-pin"));
        Assert.NotNull(GitSyncReviews.ReadPinItem(_conn, "k", "pin-edit"));
        Assert.NotNull(GitSyncReviews.ReadHeldItem(_conn, "k"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM git_sync_review WHERE kind = 'conflict'"));
    }

    [Fact]
    public void ReadOpenHeld_StillIgnoresPinRows()
    {
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("p", "new-pin", "open", "h", null, 1));
        GitSyncReviews.UpsertPinItem(_conn, new PinItem("q", "pin-edit", "open", "h", null, 1));

        Assert.Empty(GitSyncReviews.ReadOpenHeld(_conn));
        Assert.Empty(GitSyncReviews.ReadApprovedHeld(_conn));
        Assert.Null(GitSyncReviews.ReadHeldItem(_conn, "p"));
    }

    [Fact]
    public void ApproveHeld_OnAMissingRow_IsANoOp()
    {
        GitSyncReviews.ApproveHeld(_conn, "none");
        GitSyncReviews.ReopenHeld(_conn, "none", "h");
        GitSyncReviews.RefreshHeldHash(_conn, "none", "h");

        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM git_sync_review"));
    }

    // ---- conflict items ----

    private static SyncConflict.Loser Loser(SyncMerge.Side side, string text, string stamp = "", string machine = "7b3d1e55") =>
        new(side, text, stamp, machine);

    private string Row(string id, string kind) =>
        Scalar(
            "SELECT state || '|' || ifnull(record_hash, 'NULL') || '|' || ifnull(machine, 'NULL') || '|' || ifnull(direction, 'NULL') || '|' || " +
            $"ifnull(payload_json, 'NULL') || '|' || arrived_at FROM git_sync_review WHERE entry_id = '{id}' AND kind = '{kind}'")
        ?? "none";

    [Fact]
    public void ReadConflictItems_ReadsOpenAndUnseenConflictRows_AndNoOtherKind()
    {
        var payload = GitSyncReviews.ConflictPayload([Loser(SyncMerge.Side.Local, "a")]);
        InsertRow("c", "conflict", "unseen", hash: "h2", machine: "m2", payload: payload);
        InsertRow("b", "conflict", "open", hash: "h1", machine: "m1", payload: payload);
        InsertRow("a", "conflict", "resolved", hash: "h0", payload: payload);
        InsertRow("d", "held-delete", "open", direction: "remote");
        InsertRow("e", "new-pin", "open", hash: "h");

        var items = GitSyncReviews.ReadConflictItems(_conn);

        Assert.Equal(
            [new ConflictItem("b", "open", "h1", "m1", 7, payload), new ConflictItem("c", "unseen", "h2", "m2", 7, payload)],
            items);
        Assert.Equal(items[0], GitSyncReviews.ReadConflictItem(_conn, "b"));
        Assert.Null(GitSyncReviews.ReadConflictItem(_conn, "a"));
        Assert.Null(GitSyncReviews.ReadConflictItem(_conn, "d"));
        Assert.Null(GitSyncReviews.ReadConflictItem(_conn, "none"));
    }

    [Fact]
    public void ReadConflictItems_ReadsARowWithNoPayloadHashOrMachine_AsEmpty()
    {
        InsertRow("k", "conflict", "open", payload: null);

        var item = Assert.Single(GitSyncReviews.ReadConflictItems(_conn));

        Assert.Equal(new ConflictItem("k", "open", "", "", 7, ""), item);
        Assert.Empty(GitSyncReviews.ReadConflictLosers(item.PayloadJson));
    }

    [Fact]
    public void UpsertConflictItem_WritesTheRow_AndAReplaceKeepsArrivedAt()
    {
        InsertRow("k", "new-pin", "open", hash: "pin");
        var p1 = GitSyncReviews.ConflictPayload([Loser(SyncMerge.Side.Local, "one")]);
        var p2 = GitSyncReviews.ConflictPayload([Loser(SyncMerge.Side.Local, "one"), Loser(SyncMerge.Side.Remote, "two")]);

        GitSyncReviews.UpsertConflictItem(_conn, new ConflictItem("k", "open", "h", "m", 100, p1), keepArrivedAt: false);
        Assert.Equal($"open|h|m|NULL|{p1}|100", Row("k", "conflict"));

        GitSyncReviews.UpsertConflictItem(_conn, new ConflictItem("k", "unseen", "h2", "m2", 200, p2), keepArrivedAt: true);
        Assert.Equal($"unseen|h2|m2|NULL|{p2}|100", Row("k", "conflict"));

        GitSyncReviews.UpsertConflictItem(_conn, new ConflictItem("k", "unseen", "h2", "m2", 200, p2), keepArrivedAt: false);
        Assert.Equal($"unseen|h2|m2|NULL|{p2}|200", Row("k", "conflict"));
        Assert.Equal("open|pin|NULL|NULL|{}|7", Row("k", "new-pin"));
    }

    [Fact]
    public void MarkConflictUnseen_SetsStateAndHashOnly()
    {
        var payload = GitSyncReviews.ConflictPayload([Loser(SyncMerge.Side.Local, "one")]);
        GitSyncReviews.UpsertConflictItem(_conn, new ConflictItem("k", "open", "h", "m", 100, payload), keepArrivedAt: false);
        InsertRow("k", "new-pin", "open", hash: "pin");

        GitSyncReviews.MarkConflictUnseen(_conn, "k", "h2");
        GitSyncReviews.MarkConflictUnseen(_conn, "missing", "h3");

        Assert.Equal($"unseen|h2|m|NULL|{payload}|100", Row("k", "conflict"));
        Assert.Equal("open|pin|NULL|NULL|{}|7", Row("k", "new-pin"));
        Assert.Equal("none", Row("missing", "conflict"));
    }

    [Fact]
    public void DeleteConflictItem_RemovesOnlyTheConflictRow()
    {
        InsertRow("k", "conflict", "open");
        InsertRow("k", "new-pin", "open", hash: "pin");
        InsertRow("k", "held-delete", "open", direction: "remote");
        InsertRow("j", "conflict", "open");

        GitSyncReviews.DeleteConflictItem(_conn, "k");
        GitSyncReviews.DeleteConflictItem(_conn, "missing");

        Assert.Equal("none", Row("k", "conflict"));
        Assert.NotEqual("none", Row("k", "new-pin"));
        Assert.NotEqual("none", Row("k", "held-delete"));
        Assert.NotEqual("none", Row("j", "conflict"));
    }

    [Fact]
    public void ConflictPayload_RoundTrips_HostileText()
    {
        var hostile = new[]
        {
            "nul \0 byte", "crlf \r\n line", "bidi \u202E override", "quote \" and \\ backslash", new string('x', 300_000), "",
            "astral \U0001F600 and \U0001D11E",
        };
        var losers = hostile
            .Select((t, i) => Loser(i % 2 == 0 ? SyncMerge.Side.Local : SyncMerge.Side.Remote, t, i == 0 ? "" : "2026-11-12T09:10:00.000Z.000000.7b3d1e55"))
            .ToList();

        var payload = GitSyncReviews.ConflictPayload(losers);
        var back = GitSyncReviews.ReadConflictLosers(payload);

        Assert.Equal(losers, back.ToList());
        Assert.StartsWith("{\"losers\":[{\"side\":\"local\",\"text\":", payload);
        Assert.Equal("""{"losers":[]}""", GitSyncReviews.ConflictPayload([]));
    }

    [Fact]
    public void ConflictPayload_OfAHugeText_RoundTripsThroughSqlite()
    {
        var losers = new[] { Loser(SyncMerge.Side.Remote, new string('x', 300_000) + "\U0001F600") };
        var payload = GitSyncReviews.ConflictPayload(losers);

        GitSyncReviews.UpsertConflictItem(_conn, new ConflictItem("k", "open", "h", "m", 100, payload), keepArrivedAt: false);

        var item = GitSyncReviews.ReadConflictItem(_conn, "k")!;
        Assert.Equal(payload, item.PayloadJson);
        Assert.Equal(losers, GitSyncReviews.ReadConflictLosers(item.PayloadJson).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"losers":5}""")]
    [InlineData("""{"losers":{"side":"local"}}""")]
    public void ReadConflictLosers_OfAMalformedPayload_IsEmpty(string payload) =>
        Assert.Empty(GitSyncReviews.ReadConflictLosers(payload));

    [Fact]
    public void ReadConflictLosers_SkipsALoserWithAnUnknownSideOrAMissingKey()
    {
        var payload =
            """{"losers":[{"side":"local","text":"a","stamp":"","machine":"m"},{"side":"sideways","text":"b","stamp":"","machine":"m"},""" +
            """{"side":"remote","text":"c","stamp":""},{"side":"remote","text":5,"stamp":"","machine":"m"},7,""" +
            """{"side":"remote","text":"d","stamp":"s","machine":"m"}]}""";

        var losers = GitSyncReviews.ReadConflictLosers(payload).ToList();

        Assert.Equal([Loser(SyncMerge.Side.Local, "a", "", "m"), Loser(SyncMerge.Side.Remote, "d", "s", "m")], losers);
    }

    [Fact]
    public void HasAnyItem_IsTrueForARowOfAnyKindOrState()
    {
        InsertRow("a", "new-pin", "declined");
        InsertRow("b", "pin-edit", "unseen");
        InsertRow("c", "conflict", "open");
        InsertRow("d", "held-delete", "resolved", direction: "remote");
        InsertRow("e", "held-delete", "approved", direction: "local");

        foreach (var id in new[] { "a", "b", "c", "d", "e" })
            Assert.True(GitSyncReviews.HasAnyItem(_conn, id), id);
        Assert.False(GitSyncReviews.HasAnyItem(_conn, "f"));
    }

    [Fact]
    public void ThePinAndHeldMembers_StillIgnoreConflictRows()
    {
        InsertRow("k", "conflict", "open", hash: "h", payload: """{"losers":[]}""");
        InsertRow("j", "conflict", "approved", hash: "h");

        Assert.Empty(GitSyncReviews.ReadPinItems(_conn));
        Assert.Null(GitSyncReviews.ReadPinItem(_conn, "k", "new-pin"));
        Assert.Empty(GitSyncReviews.ReadOpenHeld(_conn));
        Assert.Empty(GitSyncReviews.ReadApprovedHeld(_conn));
        Assert.Null(GitSyncReviews.ReadHeldItem(_conn, "k"));
        Assert.Throws<ArgumentException>(() => GitSyncReviews.DeletePinItem(_conn, "k", "conflict"));
        GitSyncReviews.DeleteHeld(_conn, "k");
        GitSyncReviews.ApproveHeld(_conn, "k");
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM git_sync_review WHERE kind = 'conflict'"));
        Assert.StartsWith("open|", Row("k", "conflict"));
    }
}
