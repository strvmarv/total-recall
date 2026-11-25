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
    private void PinnedHere(ApplyRig? rig = null)
    {
        var r = rig ?? _rig;
        Is<EntryResult.Carried>(r.Apply(ApplyRig.Rec(Id, "Pinned text.", minute: 10)));
        r.Scan.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, Id);
        r.Scan.Pin(Id);
        r.RunScan();
        Assert.NotNull(Row(r).StickyStamp);
        Assert.NotNull(Row(r).TierStamp);
        Assert.Null(Row(r).ContentStamp);
    }

    private static SyncRecord.SyncRecord RemoteEdit(string text, int minute, string writtenBy = MachineA, Tier? tier = null) =>
        ApplyRig.Rec(
            Id, text, tier: tier ?? Tier.Hot, tags: ["r"], project: "o/r", scope: "s", minute: minute, stickyMinute: 10, writtenBy: writtenBy);

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

    /// <summary>
    /// A fault at the <c>pins</c> step (before any item write) or at <c>commit</c> (after the item was written) rolls the
    /// entry, the row and the item back.
    /// </summary>
    [Theory]
    [InlineData("new-pin", "pins")]
    [InlineData("pin-edit", "pins")]
    [InlineData("new-pin", "commit")]
    [InlineData("pin-edit", "commit")]
    public void AFaultAtThePinsStepOrTheCommit_RollsBackTheEntryAndTheItem(string kind, string faultStep)
    {
        if (kind == "pin-edit")
            PinnedHere();
        var record = kind == "new-pin" ? ApplyRig.Rec(Id, sticky: true, writtenBy: MachineA) : RemoteEdit("Remote edit.", 20);
        var before = _rig.Full(issues: false);
        var steps = new List<string>();
        _rig.Applier.BeforeEntryStep = (_, step) =>
        {
            steps.Add(step);
            if (step == faultStep)
                throw new InvalidOperationException("fault at " + faultStep);
        };

        var failed = Is<EntryResult.Failed>(_rig.Apply(record));

        Assert.Equal(1, failed.Attempts);
        Assert.Contains("row", steps);
        Assert.Contains("pins", steps);
        Assert.Equal(before, _rig.Full(issues: false));
        Assert.Null(Item(kind));
        Assert.Contains("fault at " + faultStep, Assert.Single(_rig.Scan.Issues()).Detail);
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

    // ---- D4: the reconcile, planned before the transaction ----

    private static string StampText(int minute) => SyncStamp.StampModule.format(ApplyRig.St(minute));

    private SyncMerge.LocalEntry Stored(ApplyRig? rig = null)
    {
        var r = rig ?? _rig;
        var read = LocalEntryReader.Read(t => r.Scan.Store.Get(t, ContentType.Memory, Id), () => r.Scan.Store.IsSticky(ContentType.Memory, Id));
        return Assert.IsType<LocalRead.Present>(read).View;
    }

    /// <summary>A memory pinned here (<see cref="PinnedHere"/>) with a held remote edit (text, project <c>o/r</c>, scope <c>s</c>).</summary>
    private void HeldEdit(ApplyRig? rig = null, string text = "Remote edit.", Tier? tier = null)
    {
        var r = rig ?? _rig;
        PinnedHere(r);
        Is<EntryResult.Carried>(r.Apply(RemoteEdit(text, 20, tier: tier)));
        Assert.Equal("open", Item("pin-edit", r)!.State);
        Assert.Equal("Pinned text.", r.Entry(Id)!.Row.Content);
    }

    /// <summary>
    /// The entry <see cref="ApplyPlanning.ExpectedEntry"/> predicts for a plan equals the entry the store holds after the
    /// plan is carried out, for every plan shape; an empty project or scope in the record reads back as none.
    /// </summary>
    [Theory]
    [InlineData("insert")]
    [InlineData("insert-colder")]
    [InlineData("take-theirs")]
    [InlineData("adopt")]
    [InlineData("tags")]
    [InlineData("unpin")]
    [InlineData("move")]
    [InlineData("delete")]
    public void ExpectedEntry_EqualsTheStoredEntry_ForEveryPlanShape(string shape)
    {
        var record = ApplyRig.Rec(
            Id, "Their text.", tier: Tier.Hot, tags: ["z", "a"], project: "", scope: "", createdAt: 1_710_000_000_000L);
        SyncMerge.LocalEntry? local = null;
        Tier? current = null;
        if (!shape.StartsWith("insert", StringComparison.Ordinal))
        {
            _rig.Scan.AddMemory(
                Tier.Hot, Id, "Local text.", tags: ["b", "a"], project: "p", scope: "q", entryType: EntryType.Preference,
                sourceTool: SourceTool.ClaudeCode);
            if (shape is "unpin" or "move")
                _rig.Scan.Pin(Id);
            local = Stored();
            current = Tier.Hot;
        }
        var plan = shape switch
        {
            "insert" => new ApplyPlanning.StorePlan(true, Tier.Hot, false, null, null, true, false, false),
            "insert-colder" => new ApplyPlanning.StorePlan(true, Tier.Warm, false, null, null, true, true, false),
            "take-theirs" => new ApplyPlanning.StorePlan(
                false, null, false,
                new UpdateEntryOpts
                {
                    Content = record.Content, Project = "", ClearProject = false, Scope = "", EntryType = record.EntryType,
                    SourceTool = null, ClearSourceTool = true, CreatedAt = record.CreatedAtMs,
                },
                null, true, false, false),
            "adopt" => new ApplyPlanning.StorePlan(false, null, false, new UpdateEntryOpts { CreatedAt = 1_720_000_000_000L }, null, false, false, false),
            "tags" => new ApplyPlanning.StorePlan(false, null, false, new UpdateEntryOpts { Tags = ["c", "b", "c"] }, null, false, false, false),
            "unpin" => new ApplyPlanning.StorePlan(false, null, true, null, null, false, false, false),
            "move" => new ApplyPlanning.StorePlan(false, null, false, null, Tier.Warm, false, false, false),
            "delete" => new ApplyPlanning.StorePlan(false, null, false, null, null, false, false, true),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var vector = plan.NeedsVector ? _rig.Embedder.Embed(record.Content) : null;

        using (var tx = _rig.Scan.Store.BeginImmediateTransaction())
        {
            if (plan.Insert)
                ApplyPlanning.InsertRecord(tx, Id, plan.InsertTier!, record, vector);
            else if (plan.Delete)
                tx.Delete(current!, ContentType.Memory, Id);
            else
                ApplyPlanning.WriteStore(tx, Id, current!, plan, vector);
            tx.Commit();
        }

        var expected = ApplyPlanning.ExpectedEntry(plan, record, local);
        var read = LocalEntryReader.Read(t => _rig.Scan.Store.Get(t, ContentType.Memory, Id), () => _rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        if (shape == "delete")
        {
            Assert.Null(expected);
            Assert.IsType<LocalRead.Absent>(read);
            return;
        }
        Assert.Equal(Assert.IsType<LocalRead.Present>(read).View, expected);
        if (shape.StartsWith("insert", StringComparison.Ordinal) || shape == "take-theirs")
        {
            Assert.Null(expected!.Content.Project);
            Assert.Null(expected.Content.Scope);
        }
    }

    /// <summary>Review Focus 1: a pinned record whose project and scope are <c>""</c> is carried out, and the next pull of it is a no-op.</summary>
    [Fact]
    public void AnEmptyProjectInAPinnedRecord_IsCarriedOut_AndARepeatedPullIsANoOp()
    {
        var record = ApplyRig.Rec(Id, "Pin this.", sticky: true, project: "", scope: "", writtenBy: MachineA);

        var carried = Is<EntryResult.Carried>(_rig.Apply(record));

        Assert.Equal(CarriedKind.Inserted, carried.Kind);
        Assert.Null(Stored().Content.Project); // stored as "", read as none
        Assert.Null(Stored().Content.Scope);
        Assert.Equal([$"new-pin|open|{Shown("Pin this.")}|{MachineA}"], _rig.PinItems(Id));
        var transactions = _rig.Applier.TransactionCount;
        var before = _rig.Full();

        Is<EntryResult.NoOp>(_rig.Apply(record));

        Assert.Equal(transactions, _rig.Applier.TransactionCount);
        Assert.Equal(before, _rig.Full());
    }

    [Theory]
    [InlineData("new-pin")]
    [InlineData("pin-edit")]
    public void ARepeatedPullOfTheSameRecord_IsANoOp_AndOpensNoTransaction(string kind)
    {
        SyncRecord.SyncRecord record;
        if (kind == "new-pin")
        {
            record = ApplyRig.Rec(Id, "Pin this.", sticky: true, writtenBy: MachineA);
            Is<EntryResult.Carried>(_rig.Apply(record));
        }
        else
        {
            HeldEdit();
            record = RemoteEdit("Remote edit.", 20);
        }
        var items = _rig.PinItems(Id);
        Assert.Single(items);
        var transactions = _rig.Applier.TransactionCount;
        var calls = _rig.Embedder.Calls;
        var before = _rig.Full();
        _rig.Now += 60_000;

        Is<EntryResult.NoOp>(_rig.Apply(record));

        Assert.Equal(transactions, _rig.Applier.TransactionCount);
        Assert.Equal(calls, _rig.Embedder.Calls);
        Assert.Equal(items, _rig.PinItems(Id));
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public void TheRecordChangesWhileANewPinWaits_MarksItUnseen_WithTheNewHash()
    {
        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, "Pin this.", sticky: true, minute: 10, writtenBy: MachineA)));
        var arrived = _rig.Now;
        _rig.Now += 60_000;

        var carried = Is<EntryResult.Carried>(_rig.Apply(
            ApplyRig.Rec(Id, "Pin this, edited.", sticky: true, project: "o/r", minute: 20, stickyMinute: 10, writtenBy: "machine-b")));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal("Pin this, edited.", _rig.Entry(Id)!.Row.Content);
        Assert.False(Sticky());
        Assert.Equal(
            new PinItem(Id, "new-pin", "unseen", Shown("Pin this, edited.", "o/r"), "machine-b", arrived),
            Item("new-pin"));
    }

    /// <summary>
    /// A remote unpin removes a waiting <c>new-pin</c> item (new-pin rule 3). For a memory pinned here with a held edit, a
    /// remote unpin that also edits the text unpins the entry and takes the content (FS6 with no pending sticky stamp, FS3
    /// with one, each with FC3), so the item closes by pin-edit rule 2.
    /// </summary>
    [Theory]
    [InlineData("new-pin")]
    [InlineData("FS6")]
    [InlineData("FS3")]
    public void ARemoteUnpin_RemovesTheItems_AndUnpinsAnEntryPinnedHere(string rule)
    {
        if (rule == "new-pin")
        {
            Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, sticky: true, minute: 10, writtenBy: MachineA)));
            Assert.NotNull(Item("new-pin"));

            Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10, stickyMinute: 20, writtenBy: MachineA)));

            Assert.False(Base().Placement.Sticky);
            Assert.False(Sticky());
            Assert.Empty(_rig.PinItems(Id));
            return;
        }

        HeldEdit();
        _rig.Scan.Exec(
            $"UPDATE git_sync_entry SET sticky_stamp = {(rule == "FS6" ? "NULL" : $"'{StampText(15)}'")} WHERE entry_id = '{Id}'");
        var calls = _rig.Embedder.Calls;

        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(
            Id, "Remote edit, unpinned.", tier: Tier.Hot, tags: ["r"], project: "o/r", scope: "s", minute: 30, stickyMinute: 30,
            writtenBy: MachineA)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.False(Sticky());
        var found = _rig.Entry(Id)!;
        Assert.Equal("Remote edit, unpinned.", found.Row.Content);
        Assert.Equal(calls + 1, _rig.Embedder.Calls);
        Assert.Null(Row().StickyStamp);
        Assert.Empty(_rig.PinItems(Id));
    }

    [Theory]
    [InlineData("deleted-here")]
    [InlineData("deleted-by-the-apply")]
    [InlineData("forgotten")]
    public void TheEntryDeletedHereOrForgotten_RemovesTheItem(string how)
    {
        var record = ApplyRig.Rec(Id, sticky: true, writtenBy: MachineA);
        Is<EntryResult.Carried>(_rig.Apply(record));
        Assert.Single(_rig.PinItems(Id));

        switch (how)
        {
            case "deleted-here":
                _rig.Scan.Store.Delete(Tier.Hot, ContentType.Memory, Id);
                _rig.RunScan();
                Assert.True(Is<EntryResult.Carried>(_rig.Apply(record)).Effects.PushDeletion);
                break;
            case "deleted-by-the-apply":
                Assert.True(Is<EntryResult.Carried>(_rig.Apply(Id, null)).Effects.Deleted);
                break;
            default:
                Assert.True(Is<EntryResult.Carried>(_rig.Apply(Id, null)).Effects.Deleted);
                GitSyncReviews.UpsertPinItem(
                    _rig.Scan.Connection, new PinItem(Id, "new-pin", "open", Shown("Use bun, not npm."), MachineA, _rig.Now));
                Assert.Equal(SyncMerge.RowState.Forgotten, Row().State);
                Is<EntryResult.Carried>(_rig.Apply(Id, null));
                break;
        }

        Assert.Null(_rig.Entry(Id));
        Assert.Empty(_rig.PinItems(Id));
    }

    [Fact]
    public void PinningHere_RemovesTheNewPinItem()
    {
        var record = ApplyRig.Rec(Id, sticky: true, writtenBy: MachineA);
        Is<EntryResult.Carried>(_rig.Apply(record));
        _rig.Scan.Pin(Id);
        _rig.RunScan();
        var stamp = Row().StickyStamp;
        Assert.NotNull(stamp);

        Is<EntryResult.Carried>(_rig.Apply(record));

        Assert.True(Sticky());
        Assert.Equal(stamp, Row().StickyStamp); // the local pin is still pushed as an ordinary local pin
        Assert.Empty(_rig.PinItems(Id));
    }

    [Fact]
    public void ARegressionToNonSticky_WhileTheBaseKeepsThePin_LeavesTheItem()
    {
        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, sticky: true, minute: 10, writtenBy: MachineA)));
        var item = Item("new-pin");

        // the same stamps with the pin dropped: the base keeps the pin (the push republishes it)
        _rig.Apply(ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10, writtenBy: MachineA));

        Assert.True(Base().Placement.Sticky);
        Assert.Equal(item, Item("new-pin"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("created")]
    public void AHeldEditWhoseTextComesBackEqual_ClosesTheItem_EvenWithAnotherSourceOrCreatedAt(string other)
    {
        HeldEdit();

        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(
            Id, "Pinned text.", tier: Tier.Hot, tags: ["r"], minute: 30, stickyMinute: 10,
            source: other == "source" ? SourceTool.Cursor : null,
            createdAt: other == "created" ? 1_710_000_000_000L : 1_700_000_000_000L,
            writtenBy: MachineA)));

        Assert.Equal("Pinned text.", _rig.Entry(Id)!.Row.Content);
        Assert.True(Sticky());
        Assert.Empty(_rig.PinItems(Id));
    }

    /// <summary>
    /// D5: unpinning a memory whose remote edit is held applies the held content (text, project, scope, a vector of the held
    /// text), records the comparison columns from the stored entry, issues no content stamp and removes the item; with a
    /// hot limit the held text is over, the entry lands warm, as a TakeTheirs would, and a pending tier stamp the warm
    /// tier no longer exceeds the base's (warm) for is dropped (the held record is warm there; the local hot tier was kept by FR3).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void UnpinningHereWhileAnEditIsHeld_AppliesTheHeldContent_AndRemovesTheItem(int hotCap)
    {
        using var capped = hotCap > 0 ? new ApplyRig(hotMaxContentChars: hotCap) : null;
        var rig = capped ?? _rig;
        var text = hotCap > 0 ? new string('x', 300) : "Remote edit.";
        var recordTier = hotCap > 0 ? Tier.Warm : Tier.Hot;
        HeldEdit(rig, text, recordTier);
        Assert.Equal(hotCap > 0, Row(rig).TierStamp is not null);
        rig.Scan.Store.SetSticky(ContentType.Memory, Id, false);
        rig.RunScan();
        var unpinStamp = Row(rig).StickyStamp;
        Assert.NotNull(unpinStamp);
        var calls = rig.Embedder.Calls;

        var carried = Is<EntryResult.Carried>(rig.Apply(RemoteEdit(text, 20, tier: recordTier)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal(hotCap > 0, carried.Colder);
        Assert.Null(Row(rig).TierStamp);
        var found = rig.Entry(Id)!;
        Assert.Equal(text, found.Row.Content);
        Assert.Equal("o/r", found.Row.Project!.Value);
        Assert.Equal("s", found.Row.Scope);
        Assert.Equal(hotCap > 0 ? Tier.Warm : Tier.Hot, found.Tier);
        Assert.Equal(1, rig.VectorCount(found.Tier));
        Assert.Equal(calls + 1, rig.Embedder.Calls);
        Assert.False(Sticky(rig));
        var stored = Stored(rig);
        var row = Row(rig);
        Assert.Equal(SyncScan.contentHash(stored.Content), row.ScannedContentHash);
        Assert.Equal(SyncScan.tagsHash(stored.Tags), row.ScannedTagsHash);
        Assert.Equal(found.Tier, row.LocalTier);
        Assert.False(row.LocalSticky);
        Assert.Null(row.ContentStamp);
        Assert.Equal(unpinStamp, row.StickyStamp); // the unpin is still pushed
        Assert.Empty(rig.PinItems(Id));
        AssertScanQuiet(rig);

        var transactions = rig.Applier.TransactionCount;
        Is<EntryResult.NoOp>(rig.Apply(RemoteEdit(text, 20, tier: recordTier)));
        Assert.Equal(transactions, rig.Applier.TransactionCount);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("project")]
    public void ALocalTextOrProjectEditWhileHeld_RemovesTheItem_AndKeepsTheLocalContent(string field)
    {
        HeldEdit();
        _rig.Scan.Store.Update(
            Tier.Hot, ContentType.Memory, Id,
            field == "text" ? new UpdateEntryOpts { Content = "My own edit." } : new UpdateEntryOpts { Project = "mine" });
        _rig.RunScan();
        var stamp = Row().ContentStamp;
        Assert.NotNull(stamp);
        var calls = _rig.Embedder.Calls;

        Is<EntryResult.Carried>(_rig.Apply(RemoteEdit("Remote edit.", 20)));

        var found = _rig.Entry(Id)!;
        Assert.Equal(field == "text" ? "My own edit." : "Pinned text.", found.Row.Content);
        Assert.Equal(field == "text" ? null : "mine", found.Row.Project is null ? null : found.Row.Project.Value);
        Assert.True(Sticky());
        Assert.Equal(stamp, Row().ContentStamp);
        Assert.Equal(calls, _rig.Embedder.Calls);
        Assert.Empty(_rig.PinItems(Id));
    }

    [Fact]
    public void UnpinAndEditTogether_TheEditStands()
    {
        HeldEdit();
        _rig.Scan.Store.SetSticky(ContentType.Memory, Id, false);
        _rig.Scan.Store.Update(Tier.Hot, ContentType.Memory, Id, new UpdateEntryOpts { Content = "My own edit." });
        _rig.RunScan();
        var calls = _rig.Embedder.Calls;

        Is<EntryResult.Carried>(_rig.Apply(RemoteEdit("Remote edit.", 20)));

        Assert.Equal("My own edit.", _rig.Entry(Id)!.Row.Content);
        Assert.False(Sticky());
        Assert.NotNull(Row().ContentStamp);
        Assert.Equal(calls, _rig.Embedder.Calls);
        Assert.Empty(_rig.PinItems(Id));
    }

    [Fact]
    public void ADeclinedPinEdit_IsRemovedByTheNextPull()
    {
        HeldEdit();
        GitSyncReviews.ResolvePinItem(_rig.Scan.Connection, Id, "pin-edit", "declined");
        _rig.Scan.Exec($"UPDATE git_sync_entry SET content_stamp = '{StampText(25)}' WHERE entry_id = '{Id}'");

        Is<EntryResult.Carried>(_rig.Apply(RemoteEdit("Remote edit.", 20)));

        Assert.Equal("Pinned text.", _rig.Entry(Id)!.Row.Content);
        Assert.Equal(StampText(25), SyncStamp.StampModule.format(Row().ContentStamp!));
        Assert.Empty(_rig.PinItems(Id));
    }

    [Fact]
    public void ApplyDrop_RemovesThePinItems()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id);
        _rig.RunScan();
        Assert.Null(Row().BaseJson);
        foreach (var kind in new[] { "new-pin", "pin-edit" })
            GitSyncReviews.UpsertPinItem(_rig.Scan.Connection, new PinItem(Id, kind, "open", Shown("x"), MachineA, _rig.Now));
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        var steps = new List<string>();
        _rig.Applier.BeforeEntryStep = (_, step) => steps.Add(step);

        var carried = Is<EntryResult.Carried>(_rig.Apply(Id, null));

        Assert.Equal(CarriedKind.Dropped, carried.Kind);
        Assert.Contains("pins", steps);
        Assert.False(_rig.Scan.Rows().ContainsKey(Id));
        Assert.Empty(_rig.PinItems(Id));
    }

    /// <summary>
    /// Review Focus 3: a decision with a restamp (tier FR3 local-wins, or a sticky NS2) and a pin item is planned with the
    /// placeholder issuer and carried out once with one real stamp, neither failed nor deferred.
    /// </summary>
    [Theory]
    [InlineData("FR3")]
    [InlineData("NS2")]
    public void APinEditItem_WithATierRestamp_IsCarriedOut_NotFailedOrDeferred(string rule)
    {
        if (rule == "NS2")
        {
            _rig.Scan.AddMemory(Tier.Hot, Id, "My pinned text.");
            _rig.Scan.Pin(Id);

            var ns2 = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, "Their text.", tier: Tier.Hot, writtenBy: MachineA)));

            Assert.Equal(CarriedKind.Updated, ns2.Kind);
            Assert.NotNull(Row().StickyStamp); // NS2: the local pin is restamped, to push
            Assert.True(Sticky());
            Assert.Equal([$"pin-edit|open|{Shown("Their text.")}|{MachineA}"], _rig.PinItems(Id));
            return;
        }

        HeldEdit();
        _rig.Scan.Exec($"UPDATE git_sync_entry SET tier_stamp = '{StampText(15)}' WHERE entry_id = '{Id}'");
        var transactions = _rig.Applier.TransactionCount;

        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(
            Id, "A second remote edit.", tier: Tier.Warm, tags: ["r"], project: "o/r", scope: "s", minute: 30, stickyMinute: 10,
            writtenBy: "machine-b")));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal(transactions + 1, _rig.Applier.TransactionCount);
        var tierStamp = Row().TierStamp;
        Assert.NotNull(tierStamp);
        Assert.NotEqual(StampText(15), SyncStamp.StampModule.format(tierStamp!));
        Assert.True(SyncStamp.StampModule.format(tierStamp!).CompareTo(StampText(30)) > 0);
        Assert.Equal(Tier.Hot, _rig.Entry(Id)!.Tier);
        Assert.True(Sticky());
        Assert.Equal([$"pin-edit|unseen|{Shown("A second remote edit.", "o/r", "s")}|machine-b"], _rig.PinItems(Id));
    }

    [Fact]
    public void AnItemChangedBeforeTheTransaction_DefersTheEntry()
    {
        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, "Pin this.", sticky: true, minute: 10, writtenBy: MachineA)));
        string? snapshot = null;
        _rig.Applier.BeforeTransaction = id =>
        {
            GitSyncReviews.MarkPinUnseen(_rig.Scan.Connection, id, "new-pin", Shown("Changed meanwhile."), "machine-z");
            snapshot = _rig.Full();
        };
        var calls = _rig.Embedder.Calls;

        var deferred = Is<EntryResult.Deferred>(_rig.Apply(ApplyRig.Rec(Id, "Pin this, edited.", sticky: true, minute: 20, stickyMinute: 10, writtenBy: MachineA)));

        Assert.False(deferred.Busy);
        Assert.NotNull(snapshot);
        Assert.Equal(snapshot, _rig.Full());
        Assert.Equal("Pin this.", _rig.Entry(Id)!.Row.Content);
        Assert.Equal(calls + 1, _rig.Embedder.Calls); // the record's text was embedded before the transaction
    }

    [Fact]
    public void AnUnchangedItem_IsNotRewrittenByANoOpPull()
    {
        HeldEdit();
        GitSyncReviews.MarkPinUnseen(_rig.Scan.Connection, Id, "pin-edit", Shown("Remote edit.", "o/r", "s"), MachineA);
        var item = Item("pin-edit");
        var transactions = _rig.Applier.TransactionCount;
        _rig.Now += 60_000;

        Is<EntryResult.NoOp>(_rig.Apply(RemoteEdit("Remote edit.", 20)));

        Assert.Equal(item, Item("pin-edit"));
        Assert.Equal(transactions, _rig.Applier.TransactionCount);
    }

    /// <summary>
    /// Every pin item id is visited, in each of the four states (<c>unseen</c> as open), by a proven pull with no record
    /// change: an item whose entry was pinned here is removed (new-pin rule 2) and the pull is complete.
    /// </summary>
    [GitFact]
    public async Task TheVisitSet_ContainsEveryPinItemId_InEachOfTheFourStates()
    {
        using var rig = new PullRig();
        var embedder = new CountingEmbedder();
        var now = 1_780_000_000_000L;
        var applier = new GitSyncApplier(rig.Client, rig.Store, embedder, new ApplyOptions(0), () => now);
        rig.Join();
        rig.PutManifest();
        rig.Commit("manifest");
        rig.Clone();
        var ids = Enumerable.Range(1, 4).Select(i => $"3f9c2a7e-0000-4000-8000-00000000010{i}").ToArray();
        foreach (var id in ids)
            rig.PutRecord(ApplyRig.Rec(id, "Pin " + id, sticky: true, writtenBy: MachineA));
        rig.Commit("records");

        async Task<ApplySummary> Pull()
        {
            var scan = Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(rig.Connection, () => now).Scan()).Summary;
            var fetch = await rig.Fetcher().FetchAsync();
            var result = fetch switch
            {
                FetchOutcome.Fetched f => f.Result,
                FetchOutcome.NothingNew n => n.Result,
                _ => throw new Xunit.Sdk.XunitException("the fetch did not succeed: " + fetch),
            };
            return Assert.IsType<ApplyOutcome.Done>(await applier.ApplyAsync(result, scan)).Summary;
        }

        var first = await Pull();
        Assert.Equal(4, first.Inserted);
        Assert.Equal(4, GitSyncReviews.ReadPinItems(rig.Connection).Count);
        GitSyncReviews.MarkPinUnseen(rig.Connection, ids[1], "new-pin", Shown("Pin " + ids[1]), MachineA);
        GitSyncReviews.ResolvePinItem(rig.Connection, ids[2], "new-pin", "approved");
        GitSyncReviews.ResolvePinItem(rig.Connection, ids[3], "new-pin", "declined");
        Assert.Equal(
            ["open", "unseen", "approved", "declined"],
            GitSyncReviews.ReadPinItems(rig.Connection).Select(i => i.State).ToArray());
        foreach (var id in ids)
            rig.Store.SetSticky(ContentType.Memory, id, true);

        var second = await Pull();

        Assert.True(second.Proven);
        Assert.Empty(second.Deferred);
        Assert.Empty(second.Postponed);
        Assert.True(second.Complete);
        Assert.Equal(4, second.Updated);
        Assert.Empty(GitSyncReviews.ReadPinItems(rig.Connection));
        Assert.All(ids, id => Assert.True(rig.Store.IsSticky(ContentType.Memory, id)));
    }
}
