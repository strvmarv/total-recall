using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Conflict review (Part 4), D1 and the D3 helper: the shared arrival rule and the writer-side <c>Append</c>.</summary>
public sealed class ConflictItemsTests : IDisposable
{
    private const string Id = "3f9c2a7e";
    private const string Applied = "The text that won.";

    private readonly ApplyRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static SyncConflict.Loser Loser(string text, SyncMerge.Side? side = null, string machine = "7b3d1e55", string stamp = "") =>
        new(side ?? SyncMerge.Side.Remote, text, stamp, machine);

    private static string Hash(string? applied, params string[] losers) =>
        SyncConflict.shownHash(applied is null ? FSharpOption<string>.None : FSharpOption<string>.Some(applied), ListModule.OfSeq(losers));

    private void Append(SyncConflict.Loser loser, long now)
    {
        using var tx = _rig.Scan.Store.BeginImmediateTransaction();
        ConflictItems.Append(tx, Id, loser, now);
        tx.Commit();
    }

    private string Row() =>
        _rig.Full(issues: false);

    private ConflictItem Item() =>
        GitSyncReviews.ReadConflictItem(_rig.Scan.Connection, Id) ?? throw new InvalidOperationException("no conflict item");

    private string[] Texts(ConflictItem item) => GitSyncReviews.ReadConflictLosers(item.PayloadJson).Select(l => l.Text).ToArray();

    [Fact]
    public void Append_FilesAnOpenItem_WithoutADecision()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, content: Applied);
        var loser = Loser("Their text.", machine: "aabbccdd", stamp: "2026-11-12T09:10:00.000Z.000000.aabbccdd");

        Append(loser, 1_800_000_000_123L);

        var item = Item();
        Assert.Equal("open", item.State);
        Assert.Equal(Hash(Applied, "Their text."), item.RecordHash);
        Assert.Equal("aabbccdd", item.Machine);
        Assert.Equal(1_800_000_000_123L, item.ArrivedAt);
        Assert.Equal([loser], GitSyncReviews.ReadConflictLosers(item.PayloadJson).ToArray());
    }

    [Fact]
    public void Append_ASecondText_IsAppendedUnseen_KeepingArrivedAt()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, content: Applied);
        Append(Loser("First.", machine: "11111111"), 100);

        Append(Loser("Second.", SyncMerge.Side.Local, machine: "22222222"), 200);

        var item = Item();
        Assert.Equal("unseen", item.State);
        Assert.Equal(Hash(Applied, "First.", "Second."), item.RecordHash);
        Assert.Equal("22222222", item.Machine);
        Assert.Equal(100, item.ArrivedAt);
        Assert.Equal(["First.", "Second."], Texts(item));
    }

    [Fact]
    public void Append_TheSameTextTwice_IsOneLoser_AndWritesNothing()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, content: Applied);
        Append(Loser("Same.", machine: "11111111"), 100);
        var before = Row();

        Append(Loser("Same.", SyncMerge.Side.Local, machine: "22222222"), 200);

        Assert.Equal(before, Row());
        Assert.Equal("open", Item().State);
        Assert.Equal(["Same."], Texts(Item()));
    }

    [Fact]
    public void Append_ANinthLoser_DropsTheOldest()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, content: Applied);
        for (var i = 1; i <= 9; i++)
            Append(Loser($"Text {i}."), 100 + i);

        var item = Item();
        Assert.Equal(Enumerable.Range(2, 8).Select(i => $"Text {i}.").ToArray(), Texts(item));
        Assert.Equal(Hash(Applied, Enumerable.Range(2, 8).Select(i => $"Text {i}.").ToArray()), item.RecordHash);
        Assert.Equal(101, item.ArrivedAt);
    }

    [Fact]
    public void Append_ToAnAbsentEntry_Throws_AndWritesNothing()
    {
        var before = Row();

        Assert.Throws<InvalidOperationException>(() => Append(Loser("Their text."), 100));

        Assert.Equal(before, Row());
        Assert.Null(GitSyncReviews.ReadConflictItem(_rig.Scan.Connection, Id));
    }

    [Fact]
    public void Append_ToAnEntryInTwoTiers_Throws_AndWritesNothing()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, content: Applied);
        _rig.Scan.AddMemory(Tier.Hot, Id, content: Applied);
        var before = Row();

        Assert.Throws<InvalidOperationException>(() => Append(Loser("Their text."), 100));

        Assert.Equal(before, Row());
        Assert.Null(GitSyncReviews.ReadConflictItem(_rig.Scan.Connection, Id));
    }

    [Fact]
    public void Append_ADuplicateLoser_OnAStaleStoredHash_MarksUnseen_KeepingMachinePayloadAndArrivedAt()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, content: Applied);
        var kept = Loser("Their text.", machine: "11111111", stamp: "2026-11-12T09:10:00.000Z.000000.11111111");
        var payload = GitSyncReviews.ConflictPayload([kept]);
        GitSyncReviews.UpsertConflictItem(
            _rig.Scan.Connection, new ConflictItem(Id, "open", "stale", "11111111", 100, payload), keepArrivedAt: false);

        Append(Loser("Their text.", SyncMerge.Side.Local, machine: "99999999"), 200);

        var item = Item();
        Assert.Equal("unseen", item.State);
        Assert.Equal(Hash(Applied, "Their text."), item.RecordHash);
        Assert.Equal("11111111", item.Machine);
        Assert.Equal(payload, item.PayloadJson);
        Assert.Equal(100, item.ArrivedAt);
    }

    // ---- the pure parts ----

    private static ConflictItem Stored(string state, string hash, string machine, params SyncConflict.Loser[] losers) =>
        new(Id, state, hash, machine, 5, GitSyncReviews.ConflictPayload(losers));

    [Fact]
    public void Of_ReadsTheItem_AndNoItemIsNull()
    {
        Assert.Null(ConflictItems.Of(null));
        var state = ConflictItems.Of(Stored("unseen", "h", "m", Loser("a"), Loser("b")))!;

        Assert.Equal("unseen", state.State);
        Assert.Equal("h", state.Hash);
        Assert.Equal("m", state.Machine);
        Assert.Equal(["a", "b"], state.Losers.Select(l => l.Text).ToArray());
        Assert.False(state.Fresh);
    }

    [Fact]
    public void Arrive_OnNoItem_IsOpenAndFresh_AndOnAnItemAppendsOrKeeps()
    {
        var first = ConflictItems.Arrive(null, Loser("a", machine: "m1"), Applied);
        Assert.Equal(new[] { "open", Hash(Applied, "a"), "m1" }, new[] { first.State, first.Hash, first.Machine });
        Assert.True(first.Fresh);

        var second = ConflictItems.Arrive(first with { Fresh = false }, Loser("b", machine: "m2"), Applied);
        Assert.Equal("unseen", second.State);
        Assert.Equal(Hash(Applied, "a", "b"), second.Hash);
        Assert.Equal("m2", second.Machine);
        Assert.False(second.Fresh);

        var again = ConflictItems.Arrive(second, Loser("a", machine: "m3"), Applied);
        Assert.Same(second, again);
    }

    [Fact]
    public void Diff_ChoosesTheSmallestWrite()
    {
        var item = Stored("open", "h", "m", Loser("a"));
        var same = ConflictItems.Of(item)!;

        Assert.Null(ConflictItems.Diff(null, null));
        Assert.Null(ConflictItems.Diff(item, same));
        Assert.Equal(ConflictWriteAction.Remove, ConflictItems.Diff(item, null)!.Action);

        var seen = ConflictItems.Diff(item, same with { State = "unseen", Hash = "h2" })!;
        Assert.Equal(ConflictWriteAction.MarkUnseen, seen.Action);
        Assert.Equal("h2", seen.Hash);

        var more = ConflictItems.Diff(item, ConflictItems.Arrive(same, Loser("b"), Applied))!;
        Assert.Equal(ConflictWriteAction.Upsert, more.Action);
        Assert.False(more.Fresh);
        Assert.Equal("unseen", more.State);
        Assert.Equal(["a", "b"], GitSyncReviews.ReadConflictLosers(more.PayloadJson!).Select(l => l.Text).ToArray());

        var created = ConflictItems.Diff(null, ConflictItems.Arrive(null, Loser("a"), Applied))!;
        Assert.Equal(ConflictWriteAction.Upsert, created.Action);
        Assert.True(created.Fresh);

        // removed by the reconcile and filed again by an arrival of the same pull: a new item over the stored one
        var refiled = ConflictItems.Diff(item, ConflictItems.Arrive(null, Loser("c"), Applied))!;
        Assert.Equal(ConflictWriteAction.Upsert, refiled.Action);
        Assert.Equal("open", refiled.State);
        Assert.True(refiled.Fresh);
    }

    [Fact]
    public void Reconcile_KeepsRemovesOrRewrites_WithTheLastKeptLosersMachine()
    {
        var active = SyncMerge.RowState.Active;
        var item = ConflictItems.Of(Stored("open", Hash(Applied, "a", "b"), "m2", Loser("a", machine: "m1"), Loser("b", machine: "m2")))!;

        Assert.Null(ConflictItems.Reconcile(null, Applied, active, heldAfter: false));
        Assert.Same(item, ConflictItems.Reconcile(item, Applied, active, heldAfter: false));
        Assert.Same(item, ConflictItems.Reconcile(item, Applied, null, heldAfter: false));     // no row yet
        Assert.Null(ConflictItems.Reconcile(item, Applied, SyncMerge.RowState.Forgotten, heldAfter: true));
        Assert.Null(ConflictItems.Reconcile(item, null, active, heldAfter: false));
        Assert.Same(item, ConflictItems.Reconcile(item, null, active, heldAfter: true));

        var dropped = ConflictItems.Reconcile(item, "b", active, heldAfter: false)!;
        Assert.Equal(new[] { "unseen", Hash("b", "a"), "m1" }, new[] { dropped.State, dropped.Hash, dropped.Machine });
        Assert.Equal(["a"], dropped.Losers.Select(l => l.Text).ToArray());

        var edited = ConflictItems.Reconcile(item, "c", active, heldAfter: false)!;
        Assert.Equal(new[] { "unseen", Hash("c", "a", "b"), "m2" }, new[] { edited.State, edited.Hash, edited.Machine });
        Assert.Equal(ConflictWriteAction.MarkUnseen, ConflictItems.Diff(Stored("open", Hash(Applied, "a", "b"), "m2", Loser("a", machine: "m1"), Loser("b", machine: "m2")), edited)!.Action);

        Assert.Throws<InvalidOperationException>(() => ConflictItems.Reconcile(item with { State = "approved" }, Applied, active, false));
    }
}
