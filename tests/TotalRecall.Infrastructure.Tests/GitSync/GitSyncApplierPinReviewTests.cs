using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Pin review D3 (the arrival): the pull no longer postpones a decision with a <c>NewPin</c> or <c>PinEdit</c> request. It
/// carries the decision out as for any entry (a sticky record is inserted non-sticky, FS4, FS7 and NS3 leave the entry
/// unpinned, FC2 and NC2 hold the content) and writes the <c>new-pin</c> or <c>pin-edit</c> item in the entry's transaction
/// by the arrival rule. A decision with a <c>Conflict</c> still postpones and writes nothing.
/// </summary>
public sealed class GitSyncApplierPinReviewTests : IDisposable
{
    private const string Id = "3f9c2a7e-0000-4000-8000-000000000001";
    private const string MachineA = "machine-a";

    private readonly ApplyRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static T Is<T>(EntryResult r) where T : EntryResult =>
        r is EntryResult.Failed f && typeof(T) != typeof(EntryResult.Failed)
            ? throw new Xunit.Sdk.XunitException(f.Detail)
            : Assert.IsType<T>(r);

    private static FSharpOption<string>? Opt(string? value) => value is null ? null : FSharpOption<string>.Some(value);

    private static string Shown(string text, string? project = null, string? scope = null) =>
        SyncPin.shownHash(text, Opt(project), Opt(scope));

    private GitSyncRow Row(ApplyRig? rig = null) => (rig ?? _rig).Scan.Rows()[Id];

    private SyncRecord.SyncRecord Base(ApplyRig? rig = null) =>
        ApplyPlanning.ParseBase(Id, Row(rig).BaseJson!).Parsed!.Record;

    private PinItem? Item(string kind, ApplyRig? rig = null) => GitSyncReviews.ReadPinItem((rig ?? _rig).Scan.Connection, Id, kind);

    private bool Sticky(ApplyRig? rig = null) => (rig ?? _rig).Scan.Store.IsSticky(ContentType.Memory, Id);

    private IReadOnlyList<Entry> PinnedBlock(ApplyRig? rig = null) =>
        (rig ?? _rig).Scan.Store.List(Tier.Hot, ContentType.Memory, new ListEntriesOpts { StickyOnly = true });

    /// <summary>The raw direction and payload of the id's item of <paramref name="kind"/>.</summary>
    private (string? Direction, string Payload) Raw(string kind, ApplyRig? rig = null)
    {
        using var cmd = (rig ?? _rig).Scan.Connection.CreateCommand();
        cmd.CommandText = "SELECT direction, payload_json FROM git_sync_review WHERE entry_id = $id AND kind = $kind";
        cmd.Parameters.AddWithValue("$id", Id);
        cmd.Parameters.AddWithValue("$kind", kind);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1));
    }

    private void AssertScanQuiet(ApplyRig? rig = null)
    {
        var scan = (rig ?? _rig).RunScan();
        Assert.Equal(0, scan.New + scan.Content + scan.Tags + scan.Sticky + scan.Tier + scan.Left + scan.Reactivated);
    }

    // ---- a new pin ----

    [Fact]
    public void ANewPin_IsAnOrdinaryHotEntry_WithAnOpenItem()
    {
        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, "Pin this.", sticky: true, writtenBy: MachineA)));

        Assert.Equal(CarriedKind.Inserted, carried.Kind);
        var found = _rig.Entry(Id)!;
        Assert.Equal(Tier.Hot, found.Tier);
        Assert.Equal("Pin this.", found.Row.Content);
        Assert.False(Sticky());
        Assert.False(Row().LocalSticky);
        Assert.True(Base().Placement.Sticky);
        Assert.Null(Row().StickyStamp);
        Assert.Equal(new PinItem(Id, "new-pin", "open", Shown("Pin this."), MachineA, _rig.Now), Item("new-pin"));
        Assert.Equal((null, "{}"), Raw("new-pin"));
        Assert.Null(Item("pin-edit"));
        Assert.Empty(PinnedBlock());
        AssertScanQuiet();
    }

    [Fact]
    public void ANewPinOverTheHotCap_IsInsertedWarm_WithAnItem()
    {
        using var rig = new ApplyRig(hotMaxContentChars: 100);
        var text = new string('x', 300);

        var carried = Is<EntryResult.Carried>(rig.Apply(ApplyRig.Rec(Id, text, sticky: true, writtenBy: MachineA)));

        Assert.Equal(CarriedKind.Inserted, carried.Kind);
        Assert.True(carried.Colder);
        Assert.Equal(Tier.Warm, rig.Entry(Id)!.Tier);
        Assert.False(Sticky(rig));
        Assert.Equal(new PinItem(Id, "new-pin", "open", Shown(text), MachineA, rig.Now), Item("new-pin", rig));
        Assert.Empty(PinnedBlock(rig));
    }

    /// <summary>
    /// FS4: a base, a pending sticky stamp that is not newer than the record's, the record pins, the entry does not. FS7: the
    /// same with nothing pending. NS3: no base. Each keeps the entry unpinned and applies the tags (merged) and the tier (promoted).
    /// </summary>
    [Theory]
    [InlineData("FS4")]
    [InlineData("FS7")]
    [InlineData("NS3")]
    public void FS4_FS7_NS3_KeepTheEntryUnpinned_AndApplyTheOtherGroups(string rule)
    {
        if (rule == "NS3")
        {
            _rig.Scan.AddMemory(Tier.Warm, Id, tags: ["l"], entryType: EntryType.Correction);
        }
        else
        {
            Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, tags: ["l"], minute: 10)));
            if (rule == "FS4")
                _rig.Scan.Exec($"UPDATE git_sync_entry SET sticky_stamp = '{SyncStamp.StampModule.format(ApplyRig.St(15))}' WHERE entry_id = '{Id}'");
            Assert.Equal(rule == "FS4", Row().StickyStamp is not null);
        }
        var calls = _rig.Embedder.Calls;

        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(
            Id, sticky: true, tags: ["l", "r"], minute: 10, tierMinute: 20, stickyMinute: 20, writtenBy: MachineA)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        var found = _rig.Entry(Id)!;
        Assert.Equal(Tier.Hot, found.Tier);
        Assert.Equal(["l", "r"], found.Row.Tags.ToArray());
        Assert.False(Sticky());
        Assert.Empty(PinnedBlock());
        Assert.False(Row().LocalSticky);
        Assert.Null(Row().StickyStamp);
        Assert.True(Base().Placement.Sticky);
        Assert.Equal(calls, _rig.Embedder.Calls);
        Assert.Equal(new PinItem(Id, "new-pin", "open", Shown(found.Row.Content), MachineA, _rig.Now), Item("new-pin"));
        Assert.Null(Item("pin-edit"));
    }

    // ---- a held edit of a pinned memory ----

    /// <summary>A synced warm memory (base at minute 10, not sticky) that this machine moved to hot and pinned; the scan stamped both.</summary>
    private void PinnedHere()
    {
        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, "Pinned text.", minute: 10)));
        _rig.Scan.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, Id);
        _rig.Scan.Pin(Id);
        _rig.RunScan();
        Assert.NotNull(Row().StickyStamp);
        Assert.NotNull(Row().TierStamp);
        Assert.Null(Row().ContentStamp);
    }

    private static SyncRecord.SyncRecord RemoteEdit(string text, int minute, string writtenBy = MachineA) =>
        ApplyRig.Rec(
            Id, text, tier: Tier.Hot, tags: ["r"], project: "o/r", scope: "s", minute: minute, stickyMinute: 10, writtenBy: writtenBy);

    [Fact]
    public void ARemoteEditToAPinnedMemory_IsHeld_TagsAndTierApply_AndAPinEditItemOpens()
    {
        PinnedHere();
        var calls = _rig.Embedder.Calls;

        var carried = Is<EntryResult.Carried>(_rig.Apply(RemoteEdit("Remote edit.", 20)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        var found = _rig.Entry(Id)!;
        Assert.Equal("Pinned text.", found.Row.Content);       // the pin's text stays active
        Assert.Null(found.Row.Project);
        Assert.Equal(["r"], found.Row.Tags.ToArray());          // the tags apply
        Assert.Equal(Tier.Hot, found.Tier);
        Assert.True(Sticky());
        Assert.Equal(calls, _rig.Embedder.Calls);
        var row = Row();
        Assert.Null(row.ContentStamp);
        Assert.Null(row.TierStamp);                             // the tier group converged on the record's hot
        Assert.NotNull(row.StickyStamp);                        // the local pin is still to push
        var baseRecord = Base();
        Assert.Equal("Remote edit.", baseRecord.Content);       // the base's content group is the record's
        Assert.Equal("o/r", baseRecord.Project!.Value);
        Assert.Equal(Tier.Hot, baseRecord.Placement.Tier);
        Assert.Equal(new PinItem(Id, "pin-edit", "open", Shown("Remote edit.", "o/r", "s"), MachineA, _rig.Now), Item("pin-edit"));
        Assert.Equal((null, "{}"), Raw("pin-edit"));
        Assert.Null(Item("new-pin"));
        Assert.Single(PinnedBlock());
    }

    [Fact]
    public void ANoBaseEditToAPinnedMemory_IsHeldAsAPinEdit()
    {
        _rig.Scan.AddMemory(Tier.Hot, Id, "My pinned text.");
        _rig.Scan.Pin(Id);

        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, "Their text.", tier: Tier.Hot, writtenBy: MachineA)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal("My pinned text.", _rig.Entry(Id)!.Row.Content);
        Assert.True(Sticky());
        Assert.Equal("Their text.", Base().Content);
        Assert.Null(Row().ContentStamp);
        Assert.Equal(new PinItem(Id, "pin-edit", "open", Shown("Their text."), MachineA, _rig.Now), Item("pin-edit"));
        Assert.Null(Item("new-pin"));
    }

    [Fact]
    public void ASecondRemoteEdit_MarksThePinEditItemUnseen_WithTheNewHashAndMachine()
    {
        PinnedHere();
        Is<EntryResult.Carried>(_rig.Apply(RemoteEdit("Remote edit.", 20)));
        var arrived = _rig.Now;
        _rig.Now += 60_000;

        Is<EntryResult.Carried>(_rig.Apply(RemoteEdit("A second remote edit.", 30, writtenBy: "machine-b")));

        Assert.Equal("Pinned text.", _rig.Entry(Id)!.Row.Content);
        Assert.Equal("A second remote edit.", Base().Content);
        Assert.Equal(
            new PinItem(Id, "pin-edit", "unseen", Shown("A second remote edit.", "o/r", "s"), "machine-b", arrived),
            Item("pin-edit"));
        Assert.Equal((null, "{}"), Raw("pin-edit"));
    }

    // ---- the arrival rule on an existing item ----

    [Fact]
    public void ALaterNewPin_ReopensADeclinedItem()
    {
        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, sticky: true, minute: 10, writtenBy: MachineA)));
        GitSyncReviews.UpsertPinItem(
            _rig.Scan.Connection, new PinItem(Id, "new-pin", "declined", Shown("An older text."), "machine-z", 1_700_000_000_000L));
        Assert.Null(Row().StickyStamp); // FS7: nothing pending
        _rig.Now += 60_000;

        // A unpinned and pinned again: a newer sticky stamp that pins, a genuinely new pin
        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, sticky: true, minute: 10, stickyMinute: 30, writtenBy: MachineA)));

        Assert.False(Sticky());
        Assert.Equal(new PinItem(Id, "new-pin", "open", Shown("Use bun, not npm."), MachineA, _rig.Now), Item("new-pin"));
    }

    [Fact]
    public void AnOpenItemWithTheSameHash_IsLeftAsItIs()
    {
        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, sticky: true, minute: 10, writtenBy: MachineA)));
        var item = Item("new-pin");
        Assert.Equal("open", item!.State);
        _rig.Now += 60_000;

        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, sticky: true, minute: 10, stickyMinute: 30, writtenBy: "machine-b")));

        Assert.Equal(item, Item("new-pin")); // arrived_at, hash and machine kept
        Assert.True(Base().Placement.Sticky);
    }

    // ---- the item is written in the entry's transaction ----

    [Theory]
    [InlineData("new-pin")]
    [InlineData("pin-edit")]
    public void AFaultAtThePinsStep_RollsBackTheEntryAndTheItem(string kind)
    {
        if (kind == "pin-edit")
            PinnedHere();
        var record = kind == "new-pin" ? ApplyRig.Rec(Id, sticky: true, writtenBy: MachineA) : RemoteEdit("Remote edit.", 20);
        var before = _rig.Full(issues: false);
        var steps = new List<string>();
        _rig.Applier.BeforeEntryStep = (_, step) =>
        {
            steps.Add(step);
            if (step == "pins")
                throw new InvalidOperationException("fault at pins");
        };

        var failed = Is<EntryResult.Failed>(_rig.Apply(record));

        Assert.Equal(1, failed.Attempts);
        Assert.Contains("row", steps);
        Assert.Equal(before, _rig.Full(issues: false));
        Assert.Null(Item(kind));
        Assert.Contains("fault at pins", Assert.Single(_rig.Scan.Issues()).Detail);
    }

    // ---- a conflict still postpones ----

    private void AssertPostponed(SyncRecord.SyncRecord record)
    {
        var before = _rig.Full();
        var calls = _rig.Embedder.Calls;

        var result = _rig.Apply(record);

        Assert.Equal("review", Is<EntryResult.Postponed>(result).Reason);
        Assert.Equal(before, _rig.Full());
        Assert.Equal(calls, _rig.Embedder.Calls);
        Assert.Equal(0, _rig.ReviewRows());
    }

    [Fact]
    public void ANewPinWithAConflict_StillPostpones_AndWritesNothing()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, "Local text.");

        AssertPostponed(ApplyRig.Rec(Id, "Their text.", sticky: true, writtenBy: MachineA)); // NS3 and NC4
    }

    [Fact]
    public void AnNC4Conflict_StillPostpones()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, "Local text.");
        _rig.RunScan();

        AssertPostponed(ApplyRig.Rec(Id, "Their text.", writtenBy: MachineA));
    }
}
