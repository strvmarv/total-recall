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
    }
}
