using System.Text;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-iii part 3 (pin review, #163): the whole path over two machines on one local bare repository
/// (<see cref="TwoMachineRig"/>). A pin from A is an ordinary hot entry on B with an open <c>new-pin</c> item and B's cycle is
/// complete; an approval pins it with no push; a decline never unpins A; a local pin closes the item and pushes as an
/// ordinary pin. A remote edit of a memory pinned on B is held as a <c>pin-edit</c> item: a tag edit on B pushes the tags
/// only, an approval pushes nothing, a decline pushes B's text (held back by the push's secret check when it holds a
/// token), a local unpin applies the held edit and pushes only the unpin, and a local text or project edit pushes B's
/// content group. A new pin that is also a conflict still postpones the entry. Answers go through
/// <see cref="ReviewService"/> over B's database file (<see cref="ReviewServices.For"/>).
/// </summary>
public sealed class PinReviewCycleTests : IDisposable
{
    private const string Id = "3f9c2a7e";
    private const string SharedText = "Use bun, not npm.";
    private const string EditOnA = "Use pnpm, not npm.";

    private static readonly FakeSecrets Fakes = new(163);

    private readonly TwoMachineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ---- helpers ----

    private static CycleSummary Done(CycleOutcome outcome) => Assert.IsType<CycleOutcome.Done>(outcome).Summary;

    private static PushSummary Pushed(CycleSummary summary) => Assert.IsType<PushOutcome.Pushed>(summary.Push).Summary;

    private static ReviewService Service(CycleRig machine) => ReviewServices.For(machine.Pull.DbPath, () => machine.Push.Now);

    private static PinItem? Item(CycleRig machine, string kind) => GitSyncReviews.ReadPinItem(machine.Pull.Connection, Id, kind);

    private static GitSyncRow RowOf(CycleRig machine) => GitSyncRows.ReadAll(machine.Pull.Connection)[Id];

    private static bool Sticky(CycleRig machine) => machine.Pull.Store.IsSticky(ContentType.Memory, Id);

    private static (Tier Tier, Entry Entry) Found(CycleRig machine)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (machine.Pull.Store.Get(tier, ContentType.Memory, Id) is { } entry)
                return (tier, entry);
        throw new Xunit.Sdk.XunitException("the memory is not stored");
    }

    private static IReadOnlyList<Entry> PinnedBlock(CycleRig machine) =>
        machine.Pull.Store.List(Tier.Hot, ContentType.Memory, new ListEntriesOpts { StickyOnly = true });

    private static FSharpOption<string>? Opt(string? value) => value is null ? null : FSharpOption<string>.Some(value);

    private static string Shown(string text, string? project = null, string? scope = null) =>
        SyncPin.shownHash(text, Opt(project), Opt(scope));

    private BareRemote Remote => _rig.A.Push.Remote;

    private SyncRecordModule.SyncRecord RemoteRecord() =>
        RecordReader.Parse(
            Encoding.UTF8.GetBytes(_rig.A.Push.RemoteFile(SyncValidation.recordPath(Id))), SyncValidation.recordPath(Id)).Parsed!.Record;

    private string EveryObject() => Remote.Git("cat-file", "--batch-all-objects", "--batch");

    /// <summary>The lines the remote's last commit added (its patch's <c>+</c> lines, without the file header).</summary>
    private string[] AddedLines() =>
        [.. Remote.Git("show", "--format=", "main").Split('\n').Where(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal))];

    private static void Tick(CycleRig machine) => machine.Push.Now += 60_000;

    /// <summary>
    /// A adds the memory, pins it and publishes it; B joins and pulls it: an ordinary hot entry on B with an open
    /// <c>new-pin</c> item. Returns B's first cycle.
    /// </summary>
    private async Task<CycleSummary> APinReachesB()
    {
        _rig.Seed();
        _rig.A.Push.AddMemory(Tier.Warm, Id, SharedText);
        _rig.A.Push.Pin(Id);
        Assert.Equal([Id], Pushed(Done(await _rig.RunA())).Written);
        Assert.True(RemoteRecord().Placement.Sticky);
        _rig.JoinB();
        var b = Done(await _rig.RunB());
        Assert.Equal("open", Item(_rig.B, "new-pin")!.State);
        return b;
    }

    /// <summary>
    /// The memory synced on both machines, then pinned on B and published; A pulls the pin (an open <c>new-pin</c> item on
    /// A, A's entry ordinary) and edits the text; B pulls the edit, which is held as an open <c>pin-edit</c> item while B's
    /// pinned text stays active.
    /// </summary>
    private async Task AnEditIsHeldOnB()
    {
        _rig.Seed();
        _rig.A.Push.AddMemory(Tier.Warm, Id, SharedText);
        Done(await _rig.RunA());
        _rig.JoinB();
        Done(await _rig.RunB());
        Tick(_rig.B);
        _rig.B.Push.Pin(Id);
        Assert.Equal([Id], Pushed(Done(await _rig.RunB())).Written);
        Assert.True(RemoteRecord().Placement.Sticky);
        Done(await _rig.RunA());
        Assert.False(Sticky(_rig.A));
        Assert.Equal("open", Item(_rig.A, "new-pin")!.State);

        Tick(_rig.A);
        _rig.A.Push.Edit(Found(_rig.A).Tier, Id, new UpdateEntryOpts { Content = EditOnA });
        Assert.Equal([Id], Pushed(Done(await _rig.RunA())).Written);
        var published = RemoteRecord();
        Assert.Equal(EditOnA, published.Content);
        Assert.True(published.Placement.Sticky);   // the base keeps B's pin: A's edit is a content change only

        var held = Done(await _rig.RunB());
        Assert.True(held.Apply!.Complete);
        Assert.IsType<PushOutcome.NothingToPush>(held.Push);
        Assert.Equal(SharedText, Found(_rig.B).Entry.Content);
        Assert.True(Sticky(_rig.B));
        Assert.Equal(EditOnA, ApplyPlanning.ParseBase(Id, RowOf(_rig.B).BaseJson!).Parsed!.Record.Content);
        var item = Item(_rig.B, "pin-edit")!;
        Assert.Equal(("open", Shown(EditOnA)), (item.State, item.RecordHash));
        Assert.Equal(published.WrittenBy!.Value, item.Machine);
    }

    // ---- a new pin ----

    [GitFact]
    public async Task APinFromA_IsAnOrdinaryHotEntryOnB_WithAnOpenItem_AndBsCycleIsComplete()
    {
        var b = await APinReachesB();

        Assert.True(b.Apply!.Complete);
        Assert.Equal(1, b.Apply.Inserted);
        Assert.Empty(b.Apply.Postponed);
        Assert.Empty(b.Apply.Deferred);
        Assert.IsType<PushOutcome.NothingToPush>(b.Push);
        Assert.Equal(Remote.Head(), _rig.B.Pull.Last);
        var (tier, entry) = Found(_rig.B);
        Assert.Equal(Tier.Hot, tier);
        Assert.Equal(SharedText, entry.Content);
        Assert.False(Sticky(_rig.B));
        Assert.Empty(PinnedBlock(_rig.B));
        var row = RowOf(_rig.B);
        Assert.False(row.LocalSticky);
        Assert.Null(row.StickyStamp);
        Assert.True(ApplyPlanning.ParseBase(Id, row.BaseJson!).Parsed!.Record.Placement.Sticky);
        var item = Item(_rig.B, "new-pin")!;
        Assert.Equal(Shown(SharedText), item.RecordHash);
        Assert.Equal(RemoteRecord().WrittenBy!.Value, item.Machine);
        Assert.Null(Item(_rig.B, "pin-edit"));
        Assert.True(Sticky(_rig.A));
    }

    [GitFact]
    public async Task ApprovingANewPinOnB_PinsIt_AndBsNextCyclePushesNothing()
    {
        await APinReachesB();
        var head = Remote.Head();

        var result = await Service(_rig.B).ApproveAsync(Id, "new-pin", Item(_rig.B, "new-pin")!.RecordHash);

        Assert.Equal(0, result.ExitCode);
        Assert.True(Sticky(_rig.B));
        Assert.Single(PinnedBlock(_rig.B));
        Assert.Equal("approved", Item(_rig.B, "new-pin")!.State);
        Tick(_rig.B);
        var next = Done(await _rig.RunB());

        Assert.True(next.Apply!.Complete);
        Assert.IsType<PushOutcome.NothingToPush>(next.Push);
        Assert.Equal(head, Remote.Head());
        Assert.True(Sticky(_rig.B));
        Assert.Equal(Tier.Hot, Found(_rig.B).Tier);
        Assert.Null(RowOf(_rig.B).StickyStamp);
        Assert.Null(Item(_rig.B, "new-pin"));   // its effect is in place: new-pin rule 2 removed it
    }

    [GitFact]
    public async Task DecliningANewPinOnB_DoesNotUnpinA()
    {
        await APinReachesB();
        var head = Remote.Head();
        var record = RemoteRecord();

        var result = await Service(_rig.B).DeclineAsync(Id, "new-pin", Item(_rig.B, "new-pin")!.RecordHash);

        Assert.Equal(0, result.ExitCode);
        Assert.False(Sticky(_rig.B));
        Assert.Null(RowOf(_rig.B).StickyStamp);
        Tick(_rig.B);
        var b = Done(await _rig.RunB());
        Assert.IsType<PushOutcome.NothingToPush>(b.Push);
        Assert.Equal("declined", Item(_rig.B, "new-pin")!.State);
        Tick(_rig.A);
        var a = Done(await _rig.RunA());

        Assert.IsType<PushOutcome.NothingToPush>(a.Push);
        Assert.Equal(head, Remote.Head());
        Assert.Equal(record, RemoteRecord());
        Assert.True(RemoteRecord().Placement.Sticky);
        Assert.True(Sticky(_rig.A));
        Assert.Single(PinnedBlock(_rig.A));
        Assert.False(Sticky(_rig.B));
        Assert.Empty(PinnedBlock(_rig.B));
    }

    [GitFact]
    public async Task PinningOnBWhileANewPinWaits_ClosesTheItem_AndPushesThePinAsAnOrdinaryChange()
    {
        await APinReachesB();
        var before = RemoteRecord();
        Tick(_rig.B);

        _rig.B.Push.Pin(Id);
        var b = Done(await _rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.Null(Item(_rig.B, "new-pin"));
        Assert.Equal([Id], Pushed(b).Written);
        var after = RemoteRecord();
        Assert.True(after.Placement.Sticky);
        Assert.True(SyncStamp.StampModule.compare(after.Placement.StickyStamp, before.Placement.StickyStamp) > 0);
        Assert.Equal(before.Content, after.Content);
        Assert.Equal(before.ContentStamp, after.ContentStamp);
        Assert.True(Sticky(_rig.B));
        Assert.Null(RowOf(_rig.B).StickyStamp);
        Done(await _rig.RunA());
        Assert.True(Sticky(_rig.A));
    }

    // ---- a held edit ----

    [GitFact]
    public async Task ATagEditOnBWhileAnEditIsHeld_PushesOnlyTheTags()
    {
        await AnEditIsHeldOnB();
        var before = RemoteRecord();
        Tick(_rig.B);

        _rig.B.Push.Edit(Found(_rig.B).Tier, Id, new UpdateEntryOpts { Tags = ["from-b"] });
        var b = Done(await _rig.RunB());

        Assert.Equal([Id], Pushed(b).Written);
        var after = RemoteRecord();
        Assert.Equal(EditOnA, after.Content);              // the remote edit is not reverted
        Assert.Equal(before.ContentStamp, after.ContentStamp);
        Assert.Equal(["from-b"], after.Tags.ToArray());
        Assert.True(after.Placement.Sticky);
        Assert.Contains(AddedLines(), line => line.Contains("from-b", StringComparison.Ordinal));
        Assert.All(AddedLines(), line => Assert.DoesNotContain(SharedText, line));   // B's pinned text is not pushed
        Assert.Equal(SharedText, Found(_rig.B).Entry.Content);
        Assert.True(Sticky(_rig.B));
        Assert.Equal("open", Item(_rig.B, "pin-edit")!.State);   // a tag edit does not close the item
    }

    [GitFact]
    public async Task ApprovingAHeldEditOnB_PushesNothing()
    {
        await AnEditIsHeldOnB();
        var head = Remote.Head();

        var result = await Service(_rig.B).ApproveAsync(Id, "pin-edit", Item(_rig.B, "pin-edit")!.RecordHash);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditOnA, Found(_rig.B).Entry.Content);
        Assert.True(Sticky(_rig.B));
        Tick(_rig.B);
        var b = Done(await _rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.IsType<PushOutcome.NothingToPush>(b.Push);
        Assert.Equal(head, Remote.Head());
        Assert.Equal(EditOnA, Found(_rig.B).Entry.Content);
        Assert.True(Sticky(_rig.B));
        Assert.Null(RowOf(_rig.B).ContentStamp);
        Assert.Null(Item(_rig.B, "pin-edit"));   // pin-edit rule 2: nothing is held any more
    }

    [GitFact]
    public async Task DecliningAHeldEditOnB_PushesBsText_AndAThenHoldsIt()
    {
        await AnEditIsHeldOnB();
        var before = RemoteRecord();

        var result = await Service(_rig.B).DeclineAsync(Id, "pin-edit", Item(_rig.B, "pin-edit")!.RecordHash);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(ReviewService.RevertWarning, string.Join("\n", result.Lines));
        Assert.Equal("declined", Item(_rig.B, "pin-edit")!.State);
        Assert.NotNull(RowOf(_rig.B).ContentStamp);
        Tick(_rig.B);
        var b = Done(await _rig.RunB());

        Assert.Equal([Id], Pushed(b).Written);
        var after = RemoteRecord();
        Assert.Equal(SharedText, after.Content);
        Assert.True(SyncStamp.StampModule.compare(after.ContentStamp, before.ContentStamp) > 0);
        Assert.True(after.Placement.Sticky);
        Assert.Null(Item(_rig.B, "pin-edit"));   // pin-edit rule 3: the pending stamp closed the declined item
        Assert.True(Sticky(_rig.B));

        // the memory is pinned on B only: on A the record's text arrives as an ordinary edit
        Tick(_rig.A);
        var a = Done(await _rig.RunA());

        Assert.True(a.Apply!.Complete);
        Assert.IsType<PushOutcome.NothingToPush>(a.Push);
        Assert.Equal(SharedText, Found(_rig.A).Entry.Content);
        Assert.False(Sticky(_rig.A));
        Assert.Equal(SharedText, RemoteRecord().Content);
        Assert.Equal(SharedText, ApplyPlanning.ParseBase(Id, RowOf(_rig.A).BaseJson!).Parsed!.Record.Content);
    }

    /// <summary>
    /// B's pinned text holds a token that its scan never flagged (the row's scanned hash is set to the text, as a scan
    /// with an older detector leaves it): declining the held edit leaves a content stamp pending. B then runs the cycle's
    /// steps with a scan that misses the token (<see cref="PushRig.ScanDetector"/>; in a whole cycle the scan's own secret
    /// pass would block it first), so the push's own secret check is what keeps the text out of every object of the repository.
    /// </summary>
    [GitFact]
    public async Task ADeclinedEditWithAToken_IsHeldBackByThePushsSecretCheck()
    {
        await AnEditIsHeldOnB();
        var token = Fakes.Get("github-token");
        var tainted = SharedText + " The deploy token is " + token;
        _rig.B.Push.Edit(Found(_rig.B).Tier, Id, new UpdateEntryOpts { Content = tainted });
        var view = Assert.IsType<LocalRead.Present>(LocalEntryReader.Read(
            t => _rig.B.Pull.Store.Get(t, ContentType.Memory, Id), () => Sticky(_rig.B))).View;
        GitSyncRows.Write(_rig.B.Pull.Connection, RowOf(_rig.B) with { ScannedContentHash = SyncScan.contentHash(view.Content) });
        var head = Remote.Head();
        var before = RemoteRecord();

        var result = await Service(_rig.B).DeclineAsync(Id, "pin-edit", Item(_rig.B, "pin-edit")!.RecordHash);

        Assert.Equal(0, result.ExitCode);
        Tick(_rig.B);
        _rig.B.Push.ScanDetector = _ => [];
        var (apply, scan) = await _rig.B.Push.PullAsync();
        Assert.Empty(scan.Blocked);
        Assert.True(apply.Complete);
        Assert.Null(Item(_rig.B, "pin-edit"));   // pin-edit rule 3: the pending stamp closed the declined item
        var outcome = await _rig.B.Push.Push(apply, scan);

        var push = outcome switch
        {
            PushOutcome.Pushed p => p.Summary,
            PushOutcome.NothingToPush n => n.Summary,
            var other => throw new Xunit.Sdk.XunitException("the push did not finish: " + other),
        };
        Assert.DoesNotContain(Id, push.Written);
        Assert.Equal([new PushItem(Id, SyncSecret.blockReason)], push.Blocked);   // found by the push's own check
        Assert.Equal(head, Remote.Head());
        Assert.Equal(before, RemoteRecord());
        Assert.DoesNotContain(token, EveryObject());
        Assert.Equal(SyncSecret.blockReason, RowOf(_rig.B).BlockedReason);
        Assert.NotNull(RowOf(_rig.B).ContentStamp);   // still pending: pushed once the token is gone
        Assert.Equal(tainted, Found(_rig.B).Entry.Content);
    }

    [GitFact]
    public async Task UnpinningOnBWhileAnEditIsHeld_AppliesIt_AndBsNextCyclePushesOnlyTheUnpin()
    {
        await AnEditIsHeldOnB();
        var before = RemoteRecord();
        var calls = _rig.B.Push.Embedder.Calls;
        Tick(_rig.B);

        _rig.B.Push.Unpin(Id);
        var b = Done(await _rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.Null(Item(_rig.B, "pin-edit"));
        var (tier, entry) = Found(_rig.B);
        Assert.Equal(EditOnA, entry.Content);   // D5: the held edit is applied
        Assert.False(Sticky(_rig.B));
        Assert.True(_rig.B.Push.Embedder.Calls > calls);
        Assert.Equal([Id], Pushed(b).Written);
        var after = RemoteRecord();
        Assert.False(after.Placement.Sticky);
        Assert.True(SyncStamp.StampModule.compare(after.Placement.StickyStamp, before.Placement.StickyStamp) > 0);
        Assert.Equal(before.Content, after.Content);
        Assert.Equal(before.ContentStamp, after.ContentStamp);
        Assert.Equal(before.Tags, after.Tags);
        Assert.Equal(before.Placement.Tier, after.Placement.Tier);
        Assert.Equal(before.Placement.TierStamp, after.Placement.TierStamp);
        var row = RowOf(_rig.B);
        Assert.Null(row.ContentStamp);
        Assert.Null(row.StickyStamp);
        Assert.Equal(tier, row.LocalTier);

        // a following cycle has nothing left to push
        Tick(_rig.B);
        Assert.IsType<PushOutcome.NothingToPush>(Done(await _rig.RunB()).Push);
    }

    [GitTheory]
    [InlineData("text")]
    [InlineData("project")]
    public async Task ALocalTextOrProjectEditOnBWhileHeld_PushesBsContentGroup(string field)
    {
        await AnEditIsHeldOnB();
        var before = RemoteRecord();
        Tick(_rig.B);

        _rig.B.Push.Edit(
            Found(_rig.B).Tier, Id,
            field == "text" ? new UpdateEntryOpts { Content = "B's own edit." } : new UpdateEntryOpts { Project = "b/own" });
        var b = Done(await _rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.Null(Item(_rig.B, "pin-edit"));   // pin-edit rule 3: the local edit closes the item
        Assert.Equal([Id], Pushed(b).Written);
        var after = RemoteRecord();
        var expectedText = field == "text" ? "B's own edit." : SharedText;
        Assert.Equal(expectedText, after.Content);
        Assert.Equal(field == "project" ? "b/own" : null, after.Project is null ? null : after.Project.Value);
        Assert.True(SyncStamp.StampModule.compare(after.ContentStamp, before.ContentStamp) > 0);
        Assert.True(after.Placement.Sticky);
        Assert.Equal(expectedText, Found(_rig.B).Entry.Content);
        Assert.True(Sticky(_rig.B));

        Tick(_rig.A);
        Done(await _rig.RunA());
        Assert.Equal(expectedText, Found(_rig.A).Entry.Content);
    }

    // ---- a conflict still waits ----

    [GitFact]
    public async Task ANewPinWithAConflict_StillMakesBsCycleIncomplete()
    {
        _rig.Seed();
        Done(await _rig.RunA());
        _rig.JoinB();
        Done(await _rig.RunB());
        var last = _rig.B.Pull.Last;
        Assert.Equal(Remote.Head(), last);
        Tick(_rig.A);
        _rig.A.Push.AddMemory(Tier.Warm, Id, "A's text, pinned.");
        _rig.A.Push.Pin(Id);
        Assert.Equal([Id], Pushed(Done(await _rig.RunA())).Written);
        Tick(_rig.B);
        _rig.B.Push.AddMemory(Tier.Warm, Id, "B's own text.");

        var b = Done(await _rig.RunB());

        Assert.False(b.Apply!.Complete);
        Assert.Equal([new PostponedEntry(Id, "review")], b.Apply.Postponed);
        Assert.Equal("pull-incomplete", b.PushSkipped);
        Assert.Null(b.Push);
        Assert.Equal(last, _rig.B.Pull.Last);
        Assert.Equal("B's own text.", Found(_rig.B).Entry.Content);
        Assert.False(Sticky(_rig.B));
        Assert.Null(Item(_rig.B, "new-pin"));
        Assert.Null(Item(_rig.B, "pin-edit"));
    }
}
