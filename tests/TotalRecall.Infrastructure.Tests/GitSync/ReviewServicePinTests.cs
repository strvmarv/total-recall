using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Pin review D6, D7, D8 (the list) and D9.2: <c>approve</c> and <c>decline</c> of a <c>new-pin</c> or <c>pin-edit</c> item in
/// the review service, with their checks in the spec's order (<c>no-pin-item</c>, <c>failed</c>/<c>unreadable-entry</c>,
/// <c>shown-hash-required</c>, the state test, <c>edited-locally</c>, the live hash, <c>unshowable-text</c> and
/// <c>over-pin-cap</c>), a stale hash that commits only the item's update, the write sets of each answer, and the list of
/// the open and unseen pin items. Every item is made by a pull over an <see cref="ApplyRig"/>.
/// </summary>
public sealed class ReviewServicePinTests : IDisposable
{
    private const string Id = "3f9c2a7e-0000-4000-8000-000000000001";
    private const string Id2 = "3f9c2a7e-0000-4000-8000-000000000002";
    private const string MachineA = "machine-a";
    private const string Warning =
        "this replaces that change in the repository and on your other machines where the memory is not pinned; "
        + "where it is pinned it is held there as an edit";

    private readonly ApplyRig _rig = new();

    public void Dispose()
    {
        _rig.Dispose();
        MsSqliteConnection.ClearAllPools();
    }

    private ReviewService Service(ApplyRig? rig = null, IEmbedder? embedder = null, int hotMax = 0, int pinnedMax = 500)
    {
        var r = rig ?? _rig;
        return ReviewServices.For(r.Scan.DbPath, () => r.Now, embedder, hotMaxContentChars: hotMax, pinnedMaxContentChars: pinnedMax);
    }

    private static FSharpOption<string>? Opt(string? value) => value is null ? null : FSharpOption<string>.Some(value);

    private static string Shown(string text, string? project = null, string? scope = null) =>
        SyncPin.shownHash(text, Opt(project), Opt(scope));

    private static T Is<T>(EntryResult r) where T : EntryResult =>
        r is EntryResult.Failed f && typeof(T) != typeof(EntryResult.Failed)
            ? throw new Xunit.Sdk.XunitException(f.Detail)
            : Assert.IsType<T>(r);

    private GitSyncRow Row(ApplyRig? rig = null, string id = Id) => (rig ?? _rig).Scan.Rows()[id];

    private SyncRecord.SyncRecord Base(ApplyRig? rig = null) => ApplyPlanning.ParseBase(Id, Row(rig).BaseJson!).Parsed!.Record;

    private PinItem? Item(string kind, ApplyRig? rig = null, string id = Id) =>
        GitSyncReviews.ReadPinItem((rig ?? _rig).Scan.Connection, id, kind);

    private bool Sticky(ApplyRig? rig = null, string id = Id) => (rig ?? _rig).Scan.Store.IsSticky(ContentType.Memory, id);

    private IReadOnlyList<Entry> PinnedBlock(ApplyRig? rig = null) =>
        (rig ?? _rig).Scan.Store.List(Tier.Hot, ContentType.Memory, new ListEntriesOpts { StickyOnly = true });

    /// <summary>The full dump without the <c>git_sync_review</c> lines: everything an answer may not write when only the item may change.</summary>
    private string NonReview(ApplyRig? rig = null) =>
        string.Join('\n', (rig ?? _rig).Full().Split('\n').Where(line => !line.StartsWith("review ", StringComparison.Ordinal)));

    private static void AssertRefused(SetupResult result, SyncSetup.Refusal refusal)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(SyncSetup.refusalName(refusal), SyncSetup.refusalName(result.Refusal!));
        Assert.Empty(result.Lines);
    }

    private void AssertScanQuiet(ApplyRig? rig = null)
    {
        var scan = (rig ?? _rig).RunScan();
        Assert.Equal(0, scan.New + scan.Content + scan.Tags + scan.Sticky + scan.Tier + scan.Left + scan.Reactivated);
    }

    // ---- making items ----

    /// <summary>A pinned record from machine A pulled here: an ordinary hot (or, over the hot cap, warm) entry with an open new-pin item.</summary>
    private void NewPin(ApplyRig? rig = null, string text = "Pin this.", string id = Id, string? project = null, string? scope = null,
        string writtenBy = MachineA)
    {
        var r = rig ?? _rig;
        Is<EntryResult.Carried>(r.Apply(ApplyRig.Rec(id, text, sticky: true, project: project, scope: scope, writtenBy: writtenBy)));
        Assert.Equal("open", Item("new-pin", r, id)!.State);
        Assert.False(Sticky(r, id));
    }

    private static SyncRecord.SyncRecord RemoteEdit(string text, int minute, string writtenBy = MachineA, string id = Id) =>
        ApplyRig.Rec(id, text, tier: Tier.Hot, tags: ["r"], project: "o/r", scope: "s", minute: minute, stickyMinute: 10, writtenBy: writtenBy);

    /// <summary>A synced memory pinned here (moved to hot, pinned, scanned), then a remote edit held as an open pin-edit item.</summary>
    private void HeldEdit(ApplyRig? rig = null, string text = "A remote edit, held.", string id = Id)
    {
        var r = rig ?? _rig;
        Is<EntryResult.Carried>(r.Apply(ApplyRig.Rec(id, "Pinned text.", minute: 10)));
        r.Scan.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, id);
        r.Scan.Pin(id);
        r.RunScan();
        Is<EntryResult.Carried>(r.Apply(RemoteEdit(text, 20, id: id)));
        Assert.Equal("open", Item("pin-edit", r, id)!.State);
        Assert.Equal("Pinned text.", r.Entry(id)!.Row.Content);
        Assert.True(Sticky(r, id));
    }

    /// <summary>The kind's item made by a pull, and the hash the list shows for it.</summary>
    private string Make(string kind)
    {
        if (kind == "new-pin")
        {
            NewPin();
            return Shown("Pin this.");
        }
        HeldEdit();
        return Shown("A remote edit, held.", "o/r", "s");
    }

    private Task<SetupResult> Answer(string verb, string kind, string? shown, ReviewService? service = null)
    {
        var s = service ?? Service();
        return verb == "approve" ? s.ApproveAsync(Id, kind, shown) : s.DeclineAsync(Id, kind, shown);
    }

    // ---- approve a new pin ----

    [Fact]
    public async Task ApprovingANewPin_PinsIt_InHot_WithDecayOne_AndRowLocalStickyAndNoStamp()
    {
        NewPin();
        _rig.Scan.Store.Update(Tier.Hot, ContentType.Memory, Id, new UpdateEntryOpts { DecayScore = 0.25 });
        var vectors = _rig.VectorCount(Tier.Hot);
        var rowBefore = Row();

        var result = await Service().ApproveAsync(Id, "new-pin", Shown("Pin this."));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"approved {Id}: the memory is pinned here; nothing is pushed"], result.Lines);
        var found = _rig.Entry(Id)!;
        Assert.Equal(Tier.Hot, found.Tier);
        Assert.True(Sticky());
        Assert.Equal(1.0, found.Row.DecayScore);
        Assert.Equal(vectors, _rig.VectorCount(Tier.Hot));
        Assert.Single(PinnedBlock());
        Assert.Equal(rowBefore with { LocalSticky = true, LocalTier = Tier.Hot, StickyStamp = null }, Row());
        Assert.Equal("approved", Item("new-pin")!.State);
        AssertScanQuiet();
    }

    /// <summary>Review Focus 5: an approved warm new pin is moved to hot and pinned; the next pull removes only the item, the one after is a no-op.</summary>
    [Fact]
    public async Task ApprovingAWarmNewPinOverTheHotCap_PinsItInHot_AndNothingUndoesIt()
    {
        using var rig = new ApplyRig(hotMaxContentChars: 100);
        var text = new string('x', 300);
        var record = ApplyRig.Rec(Id, text, sticky: true, writtenBy: MachineA);
        Is<EntryResult.Carried>(rig.Apply(record));
        Assert.Equal(Tier.Warm, rig.Entry(Id)!.Tier);
        Assert.Equal(1, rig.VectorCount(Tier.Warm));

        var result = await Service(rig, hotMax: 100).ApproveAsync(Id, "new-pin", Shown(text));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Tier.Hot, rig.Entry(Id)!.Tier);
        Assert.True(Sticky(rig));
        Assert.Equal(1, rig.VectorCount(Tier.Hot));
        Assert.Equal(0, rig.VectorCount(Tier.Warm));
        Assert.Equal(Tier.Hot, Row(rig).LocalTier);
        Assert.True(Row(rig).LocalSticky);
        Assert.Null(Row(rig).StickyStamp);
        AssertScanQuiet(rig);

        var first = rig.Apply(record);
        Assert.IsNotType<EntryResult.Failed>(first);
        Assert.IsNotType<EntryResult.Deferred>(first);
        Assert.Empty(rig.PinItems(Id));
        Assert.Equal(Tier.Hot, rig.Entry(Id)!.Tier);
        Assert.True(Sticky(rig));
        Assert.Equal(text, rig.Entry(Id)!.Row.Content);

        var transactions = rig.Applier.TransactionCount;
        var before = rig.Full();
        Is<EntryResult.NoOp>(rig.Apply(record));
        Assert.Equal(transactions, rig.Applier.TransactionCount);
        Assert.Equal(before, rig.Full());
    }

    [Theory]
    [InlineData(50, 51)]
    [InlineData(0, 501)]
    public async Task OverThePinnedCap_IsOverPinCap_WithTheCapInTheMessage_AndTheItemOpen(int cap, int length)
    {
        var text = new string('x', length);
        NewPin(text: text);
        var before = _rig.Full();

        var result = await Service(pinnedMax: cap).ApproveAsync(Id, "new-pin", Shown(text));

        AssertRefused(result, SyncSetup.Refusal.OverPinCap);
        Assert.Equal(PinnedTierLimits.ContentLimitMessage(cap <= 0 ? 500 : cap, length), result.RefusalText);
        Assert.Equal(before, _rig.Full());
        Assert.Equal("open", Item("new-pin")!.State);
    }

    // ---- the checks ----

    /// <summary>
    /// Review Focus 4: an answer with a hash that is not the item's live hash is <c>stale-item</c> with what the item shows
    /// now, and the only write is the item's update to that hash and <c>unseen</c> (machine: the base's writer).
    /// </summary>
    [Theory]
    [InlineData("approve", "new-pin", "record")]
    [InlineData("approve", "new-pin", "entry")]
    [InlineData("approve", "pin-edit", "record")]
    [InlineData("decline", "new-pin", "record")]
    [InlineData("decline", "new-pin", "entry")]
    [InlineData("decline", "pin-edit", "record")]
    public async Task AStaleHash_IsStaleItem_AndOnlyTheItemIsWritten(string verb, string kind, string change)
    {
        var shown = Make(kind);
        string live;
        if (change == "entry")
        {
            _rig.Scan.Store.Update(Tier.Hot, ContentType.Memory, Id, new UpdateEntryOpts { Content = "Edited here." });
            live = Shown("Edited here.");
        }
        else if (kind == "new-pin")
        {
            Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, "A newer text.", sticky: true, minute: 30, writtenBy: "machine-b")));
            live = Shown("A newer text.");
        }
        else
        {
            Is<EntryResult.Carried>(_rig.Apply(RemoteEdit("A second remote edit.", 30, writtenBy: "machine-b")));
            live = Shown("A second remote edit.", "o/r", "s");
        }
        var machine = change == "entry" ? MachineA : "machine-b";
        var before = NonReview();
        var arrived = Item(kind)!.ArrivedAt;

        var result = await Answer(verb, kind, shown);

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.EndsWith($"now: unseen {kind} {live}", result.RefusalText);
        Assert.Equal(new PinItem(Id, kind, "unseen", live, machine, arrived), Item(kind));
        Assert.Equal(before, NonReview());
    }

    /// <summary>
    /// Review Focus 4 (the commit itself): a freshly made OPEN item answered with a hash that is not its live one goes
    /// open to <c>unseen</c> with the live hash and the base's machine, and nothing else changes. Unlike a pull that made
    /// the item unseen already, this can fail if the stale refusal is not committed.
    /// </summary>
    [Theory]
    [InlineData("approve", "new-pin")]
    [InlineData("approve", "pin-edit")]
    [InlineData("decline", "new-pin")]
    [InlineData("decline", "pin-edit")]
    public async Task AWrongHashOnAnOpenItem_MarksItUnseenWithTheLiveHash_AndNothingElseChanges(string verb, string kind)
    {
        var live = Make(kind);
        var before = NonReview();
        var open = Item(kind)!;
        Assert.Equal("open", open.State);
        Assert.Equal(MachineA, open.Machine);

        var result = await Answer(verb, kind, Shown("A hash that is not this item's."));

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.EndsWith($"now: unseen {kind} {live}", result.RefusalText);
        Assert.Equal(new PinItem(Id, kind, "unseen", live, MachineA, open.ArrivedAt), Item(kind));
        Assert.Equal(before, NonReview());
    }

    [Theory]
    [InlineData("approve", null)]
    [InlineData("approve", "")]
    [InlineData("decline", null)]
    [InlineData("decline", "")]
    public async Task NoHash_IsShownHashRequired(string verb, string? shown)
    {
        NewPin();
        var before = _rig.Full();

        AssertRefused(await Answer(verb, "new-pin", shown), SyncSetup.Refusal.ShownHashRequired);
        Assert.Equal(before, _rig.Full());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("decline")]
    public async Task AnEntryAlreadyPinned_IsStaleItem_ForANewPin(string verb)
    {
        NewPin();
        _rig.Scan.Pin(Id);
        var before = _rig.Full();

        var result = await Answer(verb, "new-pin", Shown("Pin this."));

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.Contains("the next sync closes the item", result.RefusalText);
        Assert.Equal(before, _rig.Full());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("decline")]
    public async Task AnUnpinnedEntry_IsStaleItem_ForAPinEdit(string verb)
    {
        var shown = Make("pin-edit");
        _rig.Scan.Store.SetSticky(ContentType.Memory, Id, false);
        var before = _rig.Full();

        var result = await Answer(verb, "pin-edit", shown);

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.Contains("the next sync closes the item", result.RefusalText);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task TextWithACharacterTheListRemoves_IsUnshowableText()
    {
        const string text = "Pin \u202Ethis.";
        NewPin(text: text);
        var before = _rig.Full();
        var service = Service();

        AssertRefused(await service.ApproveAsync(Id, "new-pin", Shown(text)), SyncSetup.Refusal.UnshowableText);
        Assert.Equal(before, _rig.Full());
        Assert.Equal(0, (await service.DeclineAsync(Id, "new-pin", Shown(text))).ExitCode); // a decline has no such check
    }

    /// <summary>Review Focus 2: a record's project, scope and machine are shown through the sanitiser, and approve refuses what the list would change.</summary>
    [Fact]
    public async Task HostileProjectScopeAndMachine_AreSanitisedInTheList_AndApproveRefusesUnshowableText()
    {
        const string project = "o/r\nINJECT\u001b[2J";
        var scope = "s\t\u202Ex" + new string('s', 300);
        const string machine = "m\u2028ach\u001bine\u202E";
        NewPin(project: project, scope: scope, writtenBy: machine);
        var live = Shown("Pin this.", project, scope);
        var before = _rig.Full();

        var item = Assert.Single(Service().List().Items);

        Assert.Equal("o/r INJECT[2J", item.Project);
        Assert.Equal("s x" + new string('s', 197), item.Scope);
        Assert.Equal("machine", item.Machine);
        Assert.Equal("Pin this.", item.Text);
        Assert.Equal(live, item.ShownHash);
        AssertRefused(await Service().ApproveAsync(Id, "new-pin", live), SyncSetup.Refusal.UnshowableText);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public void ShownField_RemovesControlAndBidi_ReplacesNewlineAndTab_AndCutsAt200WithoutSplittingASurrogatePair()
    {
        Assert.Null(ReviewService.ShownField(null));
        Assert.Equal("a b c", ReviewService.ShownField("a\nb\tc\u0007\u2066"));
        var cut = ReviewService.ShownField(new string('x', 199) + "😀");
        Assert.Equal(199, cut!.Length);
        Assert.True(ReviewService.Showable("a\tb\nc", "o/r", null));
        Assert.False(ReviewService.Showable("a", "o/r\n", null));
        Assert.False(ReviewService.Showable("a", null, new string('s', 201)));
        Assert.False(ReviewService.Showable("a\u202E", null, null));
    }

    // ---- decline a new pin ----

    [Fact]
    public async Task DecliningANewPin_ChangesOnlyTheItem()
    {
        NewPin();
        var before = NonReview();
        var item = Item("new-pin")!;

        var result = await Service().DeclineAsync(Id, "new-pin", Shown("Pin this."));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [$"declined {Id}: the memory stays an ordinary memory here; the pin stays on the other machines"], result.Lines);
        Assert.Equal(before, NonReview());
        Assert.Equal(item with { State = "declined" }, Item("new-pin"));
        Assert.False(Sticky());
        Assert.Null(Row().StickyStamp);
        AssertScanQuiet();
    }

    // ---- approve and decline a held edit ----

    [Fact]
    public async Task ApprovingAPinEdit_TakesTheHeldContent_ReEmbedded_StillSticky_NoStamp()
    {
        var shown = Make("pin-edit");
        var texts = new List<string>();
        var embedder = new CountingEmbedder { Fails = t => { texts.Add(t); return false; } };
        var service = Service(embedder: embedder);
        var rowBefore = Row();

        var result = await service.ApproveAsync(Id, "pin-edit", shown);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"approved {Id}: the held edit is now the pinned text here; nothing is pushed"], result.Lines);
        Assert.Equal(["A remote edit, held."], texts);
        var found = _rig.Entry(Id)!;
        Assert.Equal("A remote edit, held.", found.Row.Content);
        Assert.Equal("o/r", found.Row.Project!.Value);
        Assert.Equal("s", found.Row.Scope);
        Assert.Equal(Tier.Hot, found.Tier);
        Assert.Equal(1, _rig.VectorCount(Tier.Hot));
        Assert.True(Sticky());
        Assert.Single(PinnedBlock());
        var stored = LocalEntryReader.Read(t => _rig.Scan.Store.Get(t, ContentType.Memory, Id), () => Sticky()) as LocalRead.Present;
        Assert.Equal(ApplyPlanning.WithComparison(rowBefore, stored!.View), Row());
        Assert.Null(Row().ContentStamp);
        Assert.Equal("approved", Item("pin-edit")!.State);
        AssertScanQuiet();
    }

    [Fact]
    public async Task APinEditOverTheCap_IsOverPinCap()
    {
        var text = new string('y', 60);
        HeldEdit(text: text);
        var before = _rig.Full();

        var result = await Service(pinnedMax: 50).ApproveAsync(Id, "pin-edit", Shown(text, "o/r", "s"));

        AssertRefused(result, SyncSetup.Refusal.OverPinCap);
        Assert.Equal(PinnedTierLimits.ContentLimitMessage(50, 60), result.RefusalText);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task DecliningAPinEdit_LeavesAContentStampAboveTheBase_AndCarriesTheWarning()
    {
        var shown = Make("pin-edit");
        var rowBefore = Row();
        var entryBefore = _rig.Entry(Id)!.Row;

        var result = await Service().DeclineAsync(Id, "pin-edit", shown);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"declined {Id}: the next sync pushes the text pinned here", Warning], result.Lines);
        var row = Row();
        Assert.NotNull(row.ContentStamp);
        Assert.True(SyncStamp.StampModule.compare(row.ContentStamp!, Base().ContentStamp) > 0);
        Assert.Equal(rowBefore with { ContentStamp = row.ContentStamp }, row);
        Assert.Equal(entryBefore, _rig.Entry(Id)!.Row);
        Assert.True(Sticky());
        Assert.Equal("declined", Item("pin-edit")!.State);
    }

    [Theory]
    [InlineData("approve", "text", false)]
    [InlineData("approve", "text", true)]
    [InlineData("approve", "project", false)]
    [InlineData("approve", "project", true)]
    [InlineData("decline", "text", true)]
    [InlineData("decline", "project", false)]
    public async Task APinEditAfterALocalTextOrProjectEdit_IsEditedLocally_AndNothingIsWritten(string verb, string field, bool scanned)
    {
        var shown = Make("pin-edit");
        _rig.Scan.Store.Update(
            Tier.Hot, ContentType.Memory, Id,
            field == "text" ? new UpdateEntryOpts { Content = "My own edit." } : new UpdateEntryOpts { Project = "mine" });
        if (scanned)
        {
            _rig.RunScan();
            Assert.NotNull(Row().ContentStamp);
        }
        var before = _rig.Full();
        var service = Service();

        AssertRefused(await Answer(verb, "pin-edit", shown, service), SyncSetup.Refusal.EditedLocally);
        Assert.Equal(before, _rig.Full());
    }

    /// <summary>
    /// D6 order: the state test and the local-edit test come BEFORE the hash test, so a wrong hash does not hide them and
    /// does not commit the item's update (all rows, the items included, stay as they were).
    /// </summary>
    [Theory]
    [InlineData("approve", "text", false)]
    [InlineData("approve", "text", true)]
    [InlineData("approve", "project", false)]
    [InlineData("approve", "project", true)]
    [InlineData("decline", "text", false)]
    [InlineData("decline", "project", true)]
    public async Task AWrongHashAfterALocalEdit_IsEditedLocally_NotStale_AndNothingIsWritten(string verb, string field, bool scanned)
    {
        Make("pin-edit");
        _rig.Scan.Store.Update(
            Tier.Hot, ContentType.Memory, Id,
            field == "text" ? new UpdateEntryOpts { Content = "My own edit." } : new UpdateEntryOpts { Project = "mine" });
        if (scanned)
            _rig.RunScan();
        var before = _rig.Full();

        AssertRefused(await Answer(verb, "pin-edit", Shown("A hash that is not this item's.")), SyncSetup.Refusal.EditedLocally);
        Assert.Equal(before, _rig.Full());
        Assert.Equal("open", Item("pin-edit")!.State);
    }

    [Theory]
    [InlineData("approve", "new-pin")]
    [InlineData("approve", "pin-edit")]
    [InlineData("decline", "new-pin")]
    [InlineData("decline", "pin-edit")]
    public async Task AWrongHashWithAStateMismatch_IsStaleItemOfTheNextSync_AndTheItemStaysOpen(string verb, string kind)
    {
        Make(kind);
        if (kind == "new-pin")
            _rig.Scan.Pin(Id);
        else
            _rig.Scan.Store.SetSticky(ContentType.Memory, Id, false);
        var before = _rig.Full();

        var result = await Answer(verb, kind, Shown("A hash that is not this item's."));

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.Contains("the next sync closes the item", result.RefusalText);
        Assert.Equal(before, _rig.Full());
        Assert.Equal("open", Item(kind)!.State);
    }

    // ---- unseen, the list ----

    [Fact]
    public async Task AnUnseenItem_IsAnswerable_AndListed()
    {
        Make("pin-edit");
        Is<EntryResult.Carried>(_rig.Apply(RemoteEdit("A second remote edit.", 30)));
        var live = Shown("A second remote edit.", "o/r", "s");
        Assert.Equal("unseen", Item("pin-edit")!.State);

        var item = Assert.Single(Service().List().Items);
        Assert.Equal("unseen", item.State);
        Assert.Equal(live, item.ShownHash);

        var result = await Service().ApproveAsync(Id, "pin-edit", live);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("A second remote edit.", _rig.Entry(Id)!.Row.Content);
        Assert.Equal("approved", Item("pin-edit")!.State);
    }

    [Fact]
    public void List_ShowsANewPinAndAPinEdit_WithKindSourcesCurrentTextMachineAndLiveHash()
    {
        HeldEdit(id: Id2);
        NewPin(project: "o/n", scope: "sc");
        _rig.RemoteHold("3f9c2a7e-0000-4000-8000-000000000003");

        var listing = Service(pinnedMax: 12).List();

        Assert.Null(listing.Failed);
        Assert.Equal(
            [
                new ReviewItem(
                    "new-pin", Id, "open", null, null, Shown("Pin this.", "o/n", "sc"), _rig.Now, MachineA, "o/n", "sc",
                    "Pin this.", false, "local entry", null, false),
                new ReviewItem(
                    "pin-edit", Id2, "open", null, null, Shown("A remote edit, held.", "o/r", "s"), _rig.Now, MachineA, "o/r", "s",
                    "A remote edi", true, "repository record", "Pinned text.", false),
                new ReviewItem(
                    "held-delete", "3f9c2a7e-0000-4000-8000-000000000003", "open", "remote", "unproven", null, _rig.Now, null, null,
                    null, "Kept here.", false, "local entry", null, false),
            ],
            listing.Items);
    }

    [Fact]
    public void List_DoesNotShowAnsweredPinItems()
    {
        NewPin();
        GitSyncReviews.ResolvePinItem(_rig.Scan.Connection, Id, "new-pin", "approved");
        HeldEdit(id: Id2);
        GitSyncReviews.ResolvePinItem(_rig.Scan.Connection, Id2, "pin-edit", "declined");

        Assert.Empty(Service().List().Items);
    }

    // ---- the frame ----

    [Fact]
    public async Task AHeldSyncLock_IsBusy()
    {
        var shown = Make("new-pin");
        var before = _rig.Full();
        var service = Service();
        var paths = GitSyncPaths.ForDatabase(_rig.Scan.DbPath, false);
        using (var held = await SyncLock.TryAcquireAsync(paths.LockPath, TimeSpan.Zero))
        {
            Assert.NotNull(held);
            AssertRefused(await service.ApproveAsync(Id, "new-pin", shown), SyncSetup.Refusal.Busy);
            AssertRefused(await service.DeclineAsync(Id, "new-pin", shown), SyncSetup.Refusal.Busy);
        }
        Assert.Equal(before, _rig.Full());
    }

    [Theory]
    [InlineData("approve", "new-pin")]
    [InlineData("approve", "pin-edit")]
    [InlineData("decline", "new-pin")]
    [InlineData("decline", "pin-edit")]
    public async Task AFaultAtTheLastWrite_RollsTheAnswerBack(string verb, string kind)
    {
        var shown = Make(kind);
        var before = _rig.Full();
        var service = Service();
        var steps = new List<string>();
        service.BeforeStep = step =>
        {
            steps.Add(step);
            if (step == "resolve")
                throw new InvalidOperationException("fault at resolve");
        };

        var result = await Answer(verb, kind, shown, service);

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Contains("fault at resolve", result.RefusalText);
        Assert.Equal("resolve", steps[^1]);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task NoPinItem_ForAnUnknownIdKindOrAnsweredItem()
    {
        NewPin();
        var shown = Shown("Pin this.");
        var service = Service();
        var before = _rig.Full();

        foreach (var (id, kind) in new[] { (Id2, "new-pin"), (Id, "pin-edit"), (Id, "held-delete"), (Id, "nonsense") })
        {
            var result = await service.ApproveAsync(id, kind, shown);
            AssertRefused(result, SyncSetup.Refusal.NoPinItem);
            Assert.Equal(ReviewService.NoPinItemText, result.RefusalText);
        }
        Assert.Equal(before, _rig.Full());

        Assert.Equal(0, (await service.ApproveAsync(Id, "new-pin", shown)).ExitCode);
        AssertRefused(await service.ApproveAsync(Id, "new-pin", shown), SyncSetup.Refusal.NoPinItem);
        AssertRefused(await service.DeclineAsync(Id, "new-pin", shown), SyncSetup.Refusal.NoPinItem);
    }

    [Theory]
    [InlineData("forgotten", "failed")]
    [InlineData("no base", "failed")]
    [InlineData("bad base", "failed")]
    [InlineData("two tiers", "unreadable-entry")]
    public async Task ARowOrEntryThatNoLongerFits_IsRefused_BeforeTheHash(string situation, string refusal)
    {
        NewPin();
        switch (situation)
        {
            case "forgotten":
                _rig.Scan.Exec($"UPDATE git_sync_entry SET state = 'forgotten' WHERE entry_id = '{Id}'");
                break;
            case "no base":
                _rig.Scan.Exec($"UPDATE git_sync_entry SET base_json = NULL WHERE entry_id = '{Id}'");
                break;
            case "bad base":
                _rig.Scan.Exec($"UPDATE git_sync_entry SET base_json = '{{not a record' WHERE entry_id = '{Id}'");
                break;
            case "two tiers":
                _rig.Scan.AddMemory(Tier.Cold, Id, "A second copy.");
                break;
        }
        var before = _rig.Full();

        var result = await Service().ApproveAsync(Id, "new-pin", null); // no hash: the earlier check wins

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(refusal, SyncSetup.refusalName(result.Refusal!));
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AnIdWithAHeldDeletionAndAPinItem_EachVerbAnswersItsOwnKind()
    {
        _rig.RemoteHold(Id, "Kept here.");
        _rig.RemoteHold(Id2, "Also kept.");
        GitSyncReviews.UpsertPinItem(_rig.Scan.Connection, new PinItem(Id, "new-pin", "open", Shown("Kept here."), MachineA, _rig.Now));
        var service = Service();

        AssertRefused(await service.ApproveAsync(Id2, "new-pin", Shown("Also kept.")), SyncSetup.Refusal.NoPinItem);
        AssertRefused(await service.DeclineAsync(Id2, "pin-edit", Shown("Also kept.")), SyncSetup.Refusal.NoPinItem);
        Assert.Equal(["checked"], (await service.CheckForTestsAsync(Id, ReviewOperation.Accept, null)).Lines);

        Assert.Equal(0, (await service.ApproveAsync(Id, "new-pin", Shown("Kept here."))).ExitCode);
        Assert.True(Sticky());
        Assert.Equal("open", GitSyncReviews.ReadHeldItem(_rig.Scan.Connection, Id)!.State); // the held deletion is untouched

        var accepted = await service.AcceptAsync(Id, null);
        Assert.Equal(0, accepted.ExitCode);
        Assert.Null(_rig.Entry(Id));
        Assert.Null(GitSyncReviews.ReadHeldItem(_rig.Scan.Connection, Id));
    }

    // ---- a held record whose project or scope is "" (the base's shown hash reads as the store reads it back) ----

    /// <summary>A synced memory pinned here (project and scope none), then a newer record with <paramref name="field"/> <c>""</c>.</summary>
    private SyncRecord.SyncRecord HeldWithEmpty(string field, string text)
    {
        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, "Pinned text.", minute: 10)));
        _rig.Scan.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, Id);
        _rig.Scan.Pin(Id);
        _rig.RunScan();
        var record = ApplyRig.Rec(
            Id, text, tier: Tier.Hot, project: field == "project" ? "" : null, scope: field == "scope" ? "" : null,
            minute: 20, stickyMinute: 10, writtenBy: MachineA);
        Is<EntryResult.Carried>(_rig.Apply(record));
        Assert.Equal("open", Item("pin-edit")!.State);
        Assert.Equal("Pinned text.", _rig.Entry(Id)!.Row.Content);
        return record;
    }

    [Theory]
    [InlineData("project")]
    [InlineData("scope")]
    public async Task AHeldRecordWithAnEmptyProjectOrScopeAndTheSameText_IsRemovedByTheNextPullAfterApproval(string field)
    {
        var record = HeldWithEmpty(field, "Pinned text.");
        var shown = Shown("Pinned text.");   // "" reads back as none
        Assert.Equal(shown, Item("pin-edit")!.RecordHash);

        var result = await Service().ApproveAsync(Id, "pin-edit", shown);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("approved", Item("pin-edit")!.State);
        Is<EntryResult.Carried>(_rig.Apply(record));   // pin-edit rule 2: the entry shows what the base shows
        Assert.Null(Item("pin-edit"));
        Is<EntryResult.NoOp>(_rig.Apply(record));
        Assert.Null(Item("pin-edit"));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("scope")]
    public async Task AHeldRecordWithAnEmptyProjectOrScope_ShowsOneHashInTheItemTheListAndTheStaleCheck(string field)
    {
        var record = HeldWithEmpty(field, "A remote edit, held.");
        var shown = Shown("A remote edit, held.");   // "" reads back as none

        Assert.Equal(shown, Item("pin-edit")!.RecordHash);
        var listed = Assert.Single(Service().List().Items);
        Assert.Equal(shown, listed.ShownHash);

        var result = await Service().ApproveAsync(Id, "pin-edit", listed.ShownHash);   // not stale-item: the live hash agrees

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("A remote edit, held.", _rig.Entry(Id)!.Row.Content);
        Is<EntryResult.Carried>(_rig.Apply(record));
        Assert.Null(Item("pin-edit"));
        Is<EntryResult.NoOp>(_rig.Apply(record));
    }

    [Fact]
    public async Task AMissingDatabase_IsNoPinItem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tr-review-pin-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = ReviewServices.For(Path.Combine(dir, "total-recall.db"), () => _rig.Now, syncDirectory: false);
            var result = await service.ApproveAsync(Id, "new-pin", Shown("x"));
            AssertRefused(result, SyncSetup.Refusal.NoPinItem);
            Assert.False(Directory.Exists(dir), "something was created");
        }
        finally
        {
            if (Directory.Exists(dir))
                GitTestEnvironment.ForceDelete(dir);
        }
    }
}
