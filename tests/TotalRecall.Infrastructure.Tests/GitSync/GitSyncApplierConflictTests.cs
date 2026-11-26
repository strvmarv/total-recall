using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Conflict review D3 and D8: no decision postpones any more. A text conflict (FC4, FC5, NC4) is carried out as any other
/// decision (FC4 keeps the local text and its pending stamp, FC5 and NC4 take the record's text and clear it), and in the
/// entry's transaction the losing text the decision carried is filed as a <c>conflict</c> item by the arrival rule
/// (<see cref="ConflictItems"/>): a new item is <c>open</c>, a later loser is appended and turns the item <c>unseen</c>.
/// </summary>
public sealed class GitSyncApplierConflictTests : IDisposable
{
    private const string Id = "3f9c2a7e-0000-4000-8000-000000000001";
    private const string ThisMachine = ScanRig.MachineId; // 4f2a9c1e
    private const string Other = ApplyRig.OtherMachine;   // 7b3d1e55

    private readonly ApplyRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static T Is<T>(EntryResult r) where T : EntryResult =>
        r is EntryResult.Failed f && typeof(T) != typeof(EntryResult.Failed)
            ? throw new Xunit.Sdk.XunitException(f.Detail)
            : Assert.IsType<T>(r);

    private static string StampText(int minute) => SyncStamp.StampModule.format(ApplyRig.St(minute));

    private GitSyncRow Row(ApplyRig? rig = null) => (rig ?? _rig).Scan.Rows()[Id];

    private ConflictItem? Item(ApplyRig? rig = null) => GitSyncReviews.ReadConflictItem((rig ?? _rig).Scan.Connection, Id);

    /// <summary>The item's losers as <c>side|text|stamp|machine</c> lines, oldest first.</summary>
    private static IReadOnlyList<string> Losers(ConflictItem item) =>
        GitSyncReviews.ReadConflictLosers(item.PayloadJson)
            .Select(l => $"{SyncConflict.sideName(l.Side)}|{l.Text}|{l.Stamp}|{l.Machine}")
            .ToList();

    private static string Shown(string? applied, params string[] losers) =>
        SyncConflict.shownHash(applied is null ? null : FSharpOption<string>.Some(applied), ListModule.OfSeq(losers));

    private static SyncRecordModule.SyncRecord Rec(string text, int minute, bool sticky = false, Tier? tier = null) =>
        ApplyRig.Rec(Id, text, tier: tier, sticky: sticky, minute: minute, writtenBy: Other);

    private void Edit(string text, ApplyRig? rig = null)
    {
        var r = rig ?? _rig;
        var tier = r.Entry(Id)!.Tier;
        r.Scan.Store.Update(tier, ContentType.Memory, Id, new UpdateEntryOpts { Content = text });
        r.RunScan();
    }

    /// <summary>A synced memory (base at minute 10) edited here at <paramref name="editAt"/>; the scan stamped the edit.</summary>
    private void SyncedThenEditedHere(string localText, long editAt)
    {
        Is<EntryResult.Carried>(_rig.Apply(Rec("Use bun, not npm.", 10)));
        _rig.Now = editAt;
        Edit(localText);
        Assert.NotNull(Row().ContentStamp);
    }

    // A local edit later than any record stamp (2027) wins (FC4); one earlier (2026-05) loses (FC5).
    private const long Later = 1_800_000_000_000L;
    private const long Earlier = 1_780_000_000_000L;

    // ---- the three conflicts are carried out ----

    [Fact]
    public void AnFC4LocalWinner_IsKept_WithTheRecordsTextAsARemoteLoser()
    {
        SyncedThenEditedHere("Local edit.", Later);
        var pending = Row().ContentStamp!;

        var carried = Is<EntryResult.Carried>(_rig.Apply(Rec("Their edit.", 20)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal("Local edit.", _rig.Entry(Id)!.Row.Content);
        Assert.Equal(pending, Row().ContentStamp);              // the push writes the local text
        Assert.Equal("Their edit.", ApplyPlanning.ParseBase(Id, Row().BaseJson!).Parsed!.Record.Content);
        var item = Item()!;
        Assert.Equal("open", item.State);
        Assert.Equal([$"remote|Their edit.|{StampText(20)}|{Other}"], Losers(item));
        Assert.Equal(Shown("Local edit.", "Their edit."), item.RecordHash);
        Assert.Equal(Other, item.Machine);
        Assert.Equal(_rig.Now, item.ArrivedAt);
    }

    [Fact]
    public void AnFC5RecordWinner_IsApplied_WithTheLocalTextAsALocalLoser()
    {
        SyncedThenEditedHere("Local edit.", Earlier);
        var pending = SyncStamp.StampModule.format(Row().ContentStamp!);

        var carried = Is<EntryResult.Carried>(_rig.Apply(Rec("Their edit.", 20)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal("Their edit.", _rig.Entry(Id)!.Row.Content);
        Assert.Null(Row().ContentStamp);
        var item = Item()!;
        Assert.Equal("open", item.State);
        Assert.Equal([$"local|Local edit.|{pending}|{ThisMachine}"], Losers(item));
        Assert.Equal(Shown("Their edit.", "Local edit."), item.RecordHash);
        Assert.Equal(ThisMachine, item.Machine);
    }

    [Fact]
    public void AnNC4WithoutARow_FilesTheLocalText_WithThisMachinesId()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, "Local text.");

        var carried = Is<EntryResult.Carried>(_rig.Apply(Rec("Their text.", 10)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal("Their text.", _rig.Entry(Id)!.Row.Content);
        Assert.Null(Row().ContentStamp);
        var item = Item()!;
        Assert.Equal("open", item.State);
        Assert.Equal([$"local|Local text.||{ThisMachine}"], Losers(item));
        Assert.Equal(Shown("Their text.", "Local text."), item.RecordHash);
        Assert.Equal(ThisMachine, item.Machine);
    }

    [Fact]
    public void AnNC4WithoutARow_AndNoValidMachineId_FilesTheLocalText_WithAnEmptyMachine()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, "Local text.");
        _rig.Scan.SetMeta("git_sync_machine", "not-a-machine");

        Is<EntryResult.Carried>(_rig.Apply(Rec("Their text.", 10)));

        var item = Item()!;
        Assert.Equal(["local|Local text.||"], Losers(item));
        Assert.Equal("", item.Machine);
    }

    [Fact]
    public void APinnedEntrysOwnNewerEdit_FilesTheRecordsText()
    {
        Is<EntryResult.Carried>(_rig.Apply(Rec("Pinned text.", 10)));
        _rig.Scan.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, Id);
        _rig.Scan.Pin(Id);
        _rig.RunScan();
        Edit("My newer edit.");
        Assert.NotNull(Row().ContentStamp);

        var carried = Is<EntryResult.Carried>(_rig.Apply(Rec("Remote edit.", 20, tier: Tier.Hot)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal("My newer edit.", _rig.Entry(Id)!.Row.Content);
        Assert.True(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        var item = Item()!;
        Assert.Equal([$"remote|Remote edit.|{StampText(20)}|{Other}"], Losers(item));
        Assert.Equal(Shown("My newer edit.", "Remote edit."), item.RecordHash);
        Assert.Empty(_rig.PinItems(Id));                       // FC4, not a held pin-edit
    }

    [Theory]
    [InlineData(Later)]
    [InlineData(Earlier)]
    public void ARepeatedPull_ChangesNothing_AndOpensNoTransaction(long editAt)
    {
        SyncedThenEditedHere("Local edit.", editAt);
        var record = Rec("Their edit.", 20);
        Is<EntryResult.Carried>(_rig.Apply(record));
        var before = _rig.Full();
        var transactions = _rig.Applier.TransactionCount;

        Is<EntryResult.NoOp>(_rig.Apply(record));

        Assert.Equal(transactions, _rig.Applier.TransactionCount);
        Assert.Equal(before, _rig.Full());
    }

    // ---- the arrival on an existing item ----

    [Fact]
    public void ASecondConflict_AppendsTheText_AndMarksTheItemUnseen()
    {
        SyncedThenEditedHere("Local edit.", Earlier);
        var firstPending = SyncStamp.StampModule.format(Row().ContentStamp!);
        Is<EntryResult.Carried>(_rig.Apply(Rec("Their edit.", 20)));
        var arrived = _rig.Now;
        _rig.Now = Earlier + 60_000;
        Edit("Second local edit.");
        var secondPending = SyncStamp.StampModule.format(Row().ContentStamp!);

        Is<EntryResult.Carried>(_rig.Apply(Rec("Third edit.", 30)));

        Assert.Equal("Third edit.", _rig.Entry(Id)!.Row.Content);
        var item = Item()!;
        Assert.Equal("unseen", item.State);
        Assert.Equal(
            [$"local|Local edit.|{firstPending}|{ThisMachine}", $"local|Second local edit.|{secondPending}|{ThisMachine}"],
            Losers(item));
        Assert.Equal(Shown("Third edit.", "Local edit.", "Second local edit."), item.RecordHash);
        Assert.Equal(arrived, item.ArrivedAt);
    }

    [Fact]
    public void TheSameLosingTextTwice_IsOneLoser()
    {
        SyncedThenEditedHere("Local edit.", Earlier);
        var firstPending = SyncStamp.StampModule.format(Row().ContentStamp!);
        Is<EntryResult.Carried>(_rig.Apply(Rec("Their edit.", 20)));
        _rig.Now = Earlier + 60_000;
        Edit("Local edit.");

        Is<EntryResult.Carried>(_rig.Apply(Rec("Their newer edit.", 30)));

        var item = Item()!;
        Assert.Equal([$"local|Local edit.|{firstPending}|{ThisMachine}"], Losers(item));
        Assert.Equal(Shown("Their newer edit.", "Local edit."), item.RecordHash);
        Assert.Equal("unseen", item.State); // the applied text changed
    }

    // ---- the row drop ----

    [Fact]
    public void ApplyDrop_RemovesTheConflictItem()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id);
        _rig.RunScan();
        Assert.Null(Row().BaseJson);
        GitSyncReviews.UpsertConflictItem(
            _rig.Scan.Connection,
            new ConflictItem(Id, "open", Shown("x", "y"), Other, _rig.Now,
                GitSyncReviews.ConflictPayload([new SyncConflict.Loser(SyncMerge.Side.Remote, "y", StampText(20), Other)])),
            keepArrivedAt: false);
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        var steps = new List<string>();
        _rig.Applier.BeforeEntryStep = (_, step) => steps.Add(step);

        var carried = Is<EntryResult.Carried>(_rig.Apply(Id, null));

        Assert.Equal(CarriedKind.Dropped, carried.Kind);
        Assert.Contains("pins", steps);
        Assert.False(_rig.Scan.Rows().ContainsKey(Id));
        Assert.Null(Item());
        Assert.Equal(0, _rig.ReviewRows());
    }

    // ---- the pull ----

    private const string A = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string B = "3f9c2a7e-0000-4000-8000-0000000000b2";

    /// <summary>A pull rig joined and cloned with a manifest, and an applier over it at <paramref name="now"/>.</summary>
    private static (PullRig Rig, GitSyncApplier Applier) NewPull(Func<long> now)
    {
        var rig = new PullRig();
        rig.Join();
        rig.PutManifest();
        rig.Commit("manifest");
        rig.Clone();
        return (rig, new GitSyncApplier(rig.Client, rig.Store, new CountingEmbedder(), new ApplyOptions(0), now));
    }

    private static async Task<ApplySummary> Pull(PullRig rig, GitSyncApplier applier, long now)
    {
        var fetch = await rig.Fetcher().FetchAsync();
        var result = fetch switch
        {
            FetchOutcome.Fetched f => f.Result,
            FetchOutcome.NothingNew n => n.Result,
            _ => throw new Xunit.Sdk.XunitException("the fetch did not succeed: " + fetch),
        };
        var scan = Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(rig.Connection, () => now).Scan()).Summary;
        return Assert.IsType<ApplyOutcome.Done>(await applier.ApplyAsync(result, scan)).Summary;
    }

    private static string Content(PullRig rig, string id)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (rig.Store.Get(tier, ContentType.Memory, id) is { } e)
                return e.Content;
        throw new Xunit.Sdk.XunitException("no such entry " + id);
    }

    [GitFact]
    public async Task TheVisitSet_ReachesAConflictId_ThatNothingElseVisits()
    {
        var now = Earlier;
        var (rig, applier) = NewPull(() => now);
        using var pullRig = rig;
        rig.PutRecord(ApplyRig.Rec(A, "Synced text."));
        var head = rig.Commit("records");
        Assert.True((await Pull(rig, applier, now)).Complete);
        Assert.Equal(head, rig.Last);
        GitSyncReviews.UpsertConflictItem(
            rig.Connection,
            new ConflictItem(A, "open", Shown("Synced text.", "Lost text."), Other, now,
                GitSyncReviews.ConflictPayload([new SyncConflict.Loser(SyncMerge.Side.Remote, "Lost text.", StampText(20), Other)])),
            keepArrivedAt: false);
        var visited = new List<string>();
        applier.BeforeEntry = visited.Add;

        var summary = await Pull(rig, applier, now);

        Assert.Equal([A], visited);
        Assert.True(summary.Complete);
        Assert.NotNull(GitSyncReviews.ReadConflictItem(rig.Connection, A));
    }

    [GitFact]
    public async Task AFullPull_WithConflicts_IsComplete_AndAdvancesL()
    {
        var now = Earlier;
        var (rig, applier) = NewPull(() => now);
        using var pullRig = rig;
        rig.PutRecord(ApplyRig.Rec(A, "Synced text.", minute: 10));
        rig.Commit("records");
        Assert.True((await Pull(rig, applier, now)).Complete);
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "Local A." }); // FC5: older than minute 20
        rig.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "Local B.", Id: B)); // NC4
        rig.PutRecord(ApplyRig.Rec(A, "Their A.", minute: 20));
        rig.PutRecord(ApplyRig.Rec(B, "Their B.", minute: 20));
        var head = rig.Commit("conflicts");

        var summary = await Pull(rig, applier, now);

        Assert.True(summary.Complete);
        Assert.Empty(summary.Postponed);
        Assert.Empty(summary.Deferred);
        Assert.Equal(head, summary.Head);
        Assert.Equal(head, rig.Last);
        Assert.Equal("Their A.", Content(rig, A));
        Assert.Equal("Their B.", Content(rig, B));
        Assert.Equal(
            ["Local A."],
            GitSyncReviews.ReadConflictLosers(GitSyncReviews.ReadConflictItem(rig.Connection, A)!.PayloadJson).Select(l => l.Text).ToList());
        Assert.Equal(
            ["Local B."],
            GitSyncReviews.ReadConflictLosers(GitSyncReviews.ReadConflictItem(rig.Connection, B)!.PayloadJson).Select(l => l.Text).ToList());
    }
}
