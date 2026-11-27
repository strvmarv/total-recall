using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-iii part 4 (conflict review, #164): the whole path over two machines on one local bare repository
/// (<see cref="TwoMachineRig"/>). Two text edits of one memory leave the later one on both machines and the losing text
/// in a <c>conflict</c> item on the machine that met the conflict, whichever side wins; <c>keep</c>, <c>swap</c> and
/// <c>merge</c> each leave the stated text on both machines after the next cycles (A has taken B's winning text first, so
/// a swap or merge has to change it again); a swapped text reaching a machine where the memory is pinned is held there as a
/// <c>pin-edit</c> item; a second conflict keeps both losing texts; an unanswered conflict blocks nothing; a swapped text holding a token is held back by the push. Answers go
/// through <see cref="ReviewService"/> over B's database file (<see cref="ReviewServices.For"/>).
/// </summary>
public sealed class ConflictReviewCycleTests : IDisposable
{
    private const string Id = "3f9c2a7e";
    private const string SharedText = "Use bun, not npm.";
    private const string TextOnA = "Use pnpm, not npm.";
    private const string TextOnB = "Use yarn, not npm.";

    private static readonly FakeSecrets Fakes = new(164);

    private readonly TwoMachineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ---- helpers ----

    private static CycleSummary Done(CycleOutcome outcome)
    {
        var summary = Assert.IsType<CycleOutcome.Done>(outcome).Summary;
        Assert.True(summary.Apply!.Complete);
        Assert.Null(summary.PushSkipped);
        return summary;
    }

    private static PushSummary Pushed(CycleSummary summary) => Assert.IsType<PushOutcome.Pushed>(summary.Push).Summary;

    private static PushSummary Finished(CycleSummary summary) => summary.Push switch
    {
        PushOutcome.Pushed p => p.Summary,
        PushOutcome.NothingToPush n => n.Summary,
        var other => throw new Xunit.Sdk.XunitException("the push did not finish: " + other),
    };

    private static ReviewService Service(CycleRig machine) => ReviewServices.For(machine.Pull.DbPath, () => machine.Push.Now);

    private static ConflictItem? Item(CycleRig machine) => GitSyncReviews.ReadConflictItem(machine.Pull.Connection, Id);

    /// <summary>The item's losers, oldest first, as <c>side|text|machine</c>.</summary>
    private static List<string> Losers(CycleRig machine) =>
        [.. GitSyncReviews.ReadConflictLosers(Item(machine)!.PayloadJson).Select(l => $"{SyncConflict.sideName(l.Side)}|{l.Text}|{l.Machine}")];

    private static GitSyncRow RowOf(CycleRig machine, string id = Id) => GitSyncRows.ReadAll(machine.Pull.Connection)[id];

    private static string TextOf(CycleRig machine, string id = Id)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (machine.Pull.Store.Get(tier, ContentType.Memory, id) is { } entry)
                return entry.Content;
        throw new Xunit.Sdk.XunitException("the memory is not stored");
    }

    private static void Forget(CycleRig machine, string id)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (machine.Pull.Store.Get(tier, ContentType.Memory, id) is not null)
                machine.Pull.Store.Delete(tier, ContentType.Memory, id);
    }

    private static void Edit(CycleRig machine, string text) =>
        machine.Push.Edit(Tier.Warm, Id, new UpdateEntryOpts { Content = text });

    private BareRemote Remote => _rig.A.Push.Remote;

    private SyncRecordModule.SyncRecord RemoteRecord(string id = Id) =>
        RecordReader.Parse(
            Encoding.UTF8.GetBytes(_rig.A.Push.RemoteFile(SyncValidation.recordPath(id))), SyncValidation.recordPath(id)).Parsed!.Record;

    private string EveryObject() => Remote.Git("cat-file", "--batch-all-objects", "--batch");

    /// <summary>The live shown hash of B's item: of B's stored text and the item's losers (D2).</summary>
    private string LiveHash() =>
        SyncConflict.shownHash(
            Microsoft.FSharp.Core.FSharpOption<string>.Some(TextOf(_rig.B)),
            Microsoft.FSharp.Collections.ListModule.OfSeq(GitSyncReviews.ReadConflictLosers(Item(_rig.B)!.PayloadJson).Select(l => l.Text)));

    /// <summary>The memory with <see cref="SharedText"/> added on A and synced to B; both clocks are at the start.</summary>
    private async Task SyncedOnBoth()
    {
        _rig.Seed();
        _rig.A.Push.AddMemory(Tier.Warm, Id, SharedText);
        Done(await _rig.RunA());
        _rig.JoinB();
        Done(await _rig.RunB());
        Assert.Equal(SharedText, TextOf(_rig.B));
        Assert.Null(Item(_rig.B));
    }

    /// <summary>
    /// A edits the text and pushes it at start + 1 minute; B edits it at start + 2 minutes and cycles: FC4, B's later text is
    /// kept and pushed, A's is filed on B as a <c>remote</c> loser. Returns B's cycle.
    /// </summary>
    private async Task<CycleSummary> BsLaterEditMeetsAs(string textOnA = TextOnA, string textOnB = TextOnB)
    {
        _rig.A.Push.Now += 60_000;
        Edit(_rig.A, textOnA);
        Assert.Equal([Id], Pushed(Done(await _rig.RunA())).Written);
        Assert.Equal(textOnA, RemoteRecord().Content);
        _rig.B.Push.Now += 120_000;
        Edit(_rig.B, textOnB);
        return Done(await _rig.RunB());
    }

    // ---- the later edit wins, the loser is listed ----

    [GitFact]
    public async Task TwoTextEdits_LeaveTheLaterOnBothMachines_AndTheLoserListedOnB()
    {
        await SyncedOnBoth();

        var b = await BsLaterEditMeetsAs();

        Assert.Empty(b.Apply!.Deferred);
        Assert.Equal(Remote.Head(), _rig.B.Pull.Last);
        Assert.Equal([Id], Pushed(b).Written);
        Assert.Equal(TextOnB, RemoteRecord().Content);
        Assert.Equal(TextOnB, TextOf(_rig.B));
        var item = Item(_rig.B)!;
        Assert.Equal("open", item.State);
        Assert.Equal(LiveHash(), item.RecordHash);
        Assert.Equal(PullRig.MachineId, item.Machine);
        var loser = Assert.Single(GitSyncReviews.ReadConflictLosers(item.PayloadJson));
        Assert.Equal((SyncConflict.sideName(loser.Side), loser.Text, loser.Machine), ("remote", TextOnA, PullRig.MachineId));
        Assert.NotEqual("", loser.Stamp);
        var listed = Assert.Single(Service(_rig.B).List().Items, i => i.Kind == "conflict");
        Assert.Equal((Id, TextOnB), (listed.EntryId, listed.Text));
        Assert.Equal(TextOnA, Assert.Single(listed.Losers).Text);

        _rig.A.Push.Now += 120_000;
        var a = Done(await _rig.RunA());

        Assert.IsType<PushOutcome.NothingToPush>(a.Push);
        Assert.Equal(TextOnB, TextOf(_rig.A));
        Assert.Null(Item(_rig.A));
        Assert.Equal(TextOnB, TextOf(_rig.B));
    }

    [GitFact]
    public async Task ARecordNewerThanBsEdit_IsAppliedOnB_AndBsTextIsTheLoser()
    {
        await SyncedOnBoth();
        _rig.B.Push.Now += 60_000;
        Edit(_rig.B, TextOnB);
        _rig.A.Push.Now += 120_000;
        Edit(_rig.A, TextOnA);
        Assert.Equal([Id], Pushed(Done(await _rig.RunA())).Written);
        var head = Remote.Head();

        var b = Done(await _rig.RunB());

        Assert.Empty(b.Apply!.Deferred);
        Assert.IsType<PushOutcome.NothingToPush>(b.Push);
        Assert.Equal(head, Remote.Head());
        Assert.Equal(head, _rig.B.Pull.Last);
        Assert.Equal(TextOnA, TextOf(_rig.B));
        Assert.Null(RowOf(_rig.B).ContentStamp);
        Assert.Equal("open", Item(_rig.B)!.State);
        Assert.Equal(LiveHash(), Item(_rig.B)!.RecordHash);
        Assert.Equal([$"local|{TextOnB}|{TwoMachineRig.MachineB}"], Losers(_rig.B));

        _rig.A.Push.Now += 60_000;
        Assert.IsType<PushOutcome.NothingToPush>(Done(await _rig.RunA()).Push);
        Assert.Equal(TextOnA, TextOf(_rig.A));
        Assert.Equal(TextOnA, RemoteRecord().Content);
    }

    // ---- the answers ----

    [GitTheory]
    [InlineData("keep")]
    [InlineData("swap")]
    [InlineData("merge")]
    public async Task KeepSwapAndMerge_EachLeaveTheStatedTextOnBothMachines(string answer)
    {
        const string merged = "Use pnpm or yarn, never npm.";
        await SyncedOnBoth();
        await BsLaterEditMeetsAs();
        // A takes B's winning text first, so for swap and merge the text on A has to change again to the answered text
        _rig.A.Push.Now += 120_000;
        Assert.IsType<PushOutcome.NothingToPush>(Done(await _rig.RunA()).Push);
        Assert.Equal(TextOnB, TextOf(_rig.A));
        var hash = Item(_rig.B)!.RecordHash;
        var service = Service(_rig.B);

        var result = answer switch
        {
            "keep" => await service.KeepAsync(Id, hash),
            "swap" => await service.SwapAsync(Id, hash, null),
            _ => await service.MergeAsync(Id, hash, merged),
        };

        Assert.Equal(0, result.ExitCode);
        Assert.Null(Item(_rig.B));
        var expected = answer switch { "keep" => TextOnB, "swap" => TextOnA, _ => merged };
        Assert.Equal(expected, TextOf(_rig.B));
        _rig.B.Push.Now += 60_000;
        var b = Done(await _rig.RunB());
        if (answer == "keep")
            Assert.IsType<PushOutcome.NothingToPush>(b.Push);
        else
            Assert.Equal([Id], Pushed(b).Written);
        Assert.Equal(TextOnB, TextOf(_rig.A));   // unchanged until A's next cycle
        _rig.A.Push.Now += 180_000;
        var a = Done(await _rig.RunA());

        Assert.IsType<PushOutcome.NothingToPush>(a.Push);
        Assert.Equal(expected, RemoteRecord().Content);
        foreach (var machine in new[] { _rig.A, _rig.B })
        {
            Assert.Equal(expected, TextOf(machine));
            Assert.Null(Item(machine));
            Assert.Null(RowOf(machine).ContentStamp);
        }
    }

    /// <summary>
    /// Criterion 7's parenthetical: on a machine where the memory is pinned, a swapped text arrives as an ordinary remote edit
    /// and is held as a <c>pin-edit</c> item. A pins the memory and later writes a newer text; B's earlier edit loses on B
    /// (filed as a local loser beside the <c>new-pin</c> item the pin raises there), B swaps it back and pushes it, and A's
    /// next cycle holds B's text in an open <c>pin-edit</c> item while A's pinned text stays.
    /// </summary>
    [GitFact]
    public async Task ASwapPushedToAMachineWhereTheMemoryIsPinned_IsHeldThereAsAPinEdit()
    {
        await SyncedOnBoth();
        _rig.B.Push.Now += 60_000;
        Edit(_rig.B, TextOnB);
        _rig.A.Push.Now += 120_000;
        _rig.A.Push.Pin(Id);
        _rig.A.Push.Edit(Tier.Hot, Id, new UpdateEntryOpts { Content = TextOnA });
        Assert.Equal([Id], Pushed(Done(await _rig.RunA())).Written);
        Assert.True(RemoteRecord().Placement.Sticky);

        Done(await _rig.RunB());
        Assert.Equal(TextOnA, TextOf(_rig.B));
        Assert.Equal([$"local|{TextOnB}|{TwoMachineRig.MachineB}"], Losers(_rig.B));
        Assert.Equal("open", GitSyncReviews.ReadPinItem(_rig.B.Pull.Connection, Id, "new-pin")!.State);
        Assert.False(_rig.B.Pull.Store.IsSticky(ContentType.Memory, Id));

        Assert.Equal(0, (await Service(_rig.B).SwapAsync(Id, Item(_rig.B)!.RecordHash, 1)).ExitCode);
        Assert.Equal(TextOnB, TextOf(_rig.B));
        _rig.B.Push.Now += 60_000;
        Assert.Equal([Id], Pushed(Done(await _rig.RunB())).Written);
        var published = RemoteRecord();
        Assert.Equal(TextOnB, published.Content);
        Assert.True(published.Placement.Sticky);   // B pushed the content group only: A's pin stays in the record

        _rig.A.Push.Now += 180_000;
        var a = Done(await _rig.RunA());

        Assert.IsType<PushOutcome.NothingToPush>(a.Push);
        Assert.Equal(TextOnA, TextOf(_rig.A));
        Assert.True(_rig.A.Pull.Store.IsSticky(ContentType.Memory, Id));
        Assert.Null(Item(_rig.A));
        var pinEdit = GitSyncReviews.ReadPinItem(_rig.A.Pull.Connection, Id, "pin-edit")!;
        Assert.Equal("open", pinEdit.State);
        Assert.Equal(published.WrittenBy!.Value, pinEdit.Machine);
        Assert.Equal(TextOnB, ApplyPlanning.ParseBase(Id, RowOf(_rig.A).BaseJson!).Parsed!.Record.Content);
        var listed = Assert.Single(Service(_rig.A).List().Items, i => i.Kind == "pin-edit");
        Assert.Equal((TextOnB, TextOnA), (listed.Text, listed.CurrentText));
    }

    [GitFact]
    public async Task ASecondConflict_KeepsBothLosingTexts()
    {
        const string secondOnA = "Use deno, not npm.";
        const string secondOnB = "Use bun 2, not npm.";
        await SyncedOnBoth();
        await BsLaterEditMeetsAs();
        _rig.A.Push.Now += 120_000;
        Done(await _rig.RunA());
        Assert.Equal(TextOnB, TextOf(_rig.A));
        var first = Item(_rig.B)!;

        _rig.A.Push.Now += 60_000;
        _rig.B.Push.Now += 180_000;
        var b = await BsLaterEditMeetsAs(secondOnA, secondOnB);

        Assert.Equal([Id], Pushed(b).Written);
        Assert.Equal(secondOnB, TextOf(_rig.B));
        var item = Item(_rig.B)!;
        Assert.Equal("unseen", item.State);
        Assert.Equal(first.ArrivedAt, item.ArrivedAt);
        Assert.Equal(LiveHash(), item.RecordHash);
        Assert.Equal([$"remote|{TextOnA}|{PullRig.MachineId}", $"remote|{secondOnA}|{PullRig.MachineId}"], Losers(_rig.B));
        var listed = Assert.Single(Service(_rig.B).List().Items, i => i.Kind == "conflict");
        Assert.Equal([secondOnA, TextOnA], listed.Losers.Select(l => l.Text));   // newest first
    }

    // ---- an unanswered conflict ----

    [GitFact]
    public async Task AnUnansweredConflict_BlocksNothing()
    {
        const string pinId = "4a5b6c7d";
        const string heldId = "5b6c7d8e";
        const string newId = "6c7d8e9f";
        _rig.Seed();
        _rig.A.Push.AddMemory(Tier.Warm, Id, SharedText);
        _rig.A.Push.AddMemory(Tier.Warm, pinId, "Pinned on A.");
        _rig.A.Push.Pin(pinId);
        _rig.A.Push.AddMemory(Tier.Warm, heldId, "Deleted on B, held.");
        Done(await _rig.RunA());
        _rig.JoinB();
        Done(await _rig.RunB());
        Forget(_rig.B, heldId);
        _rig.B.DeleteClone();
        var unproven = Done(await _rig.RunB());
        Assert.Equal([new HeldEntry(heldId, "local", "unproven")], unproven.Apply!.Held);
        var held = GitSyncReviews.ReadHeldItem(_rig.B.Pull.Connection, heldId)!;
        var pin = GitSyncReviews.ReadPinItem(_rig.B.Pull.Connection, pinId, "new-pin")!;
        Assert.Equal("open", pin.State);

        _rig.A.Push.Now += 60_000;
        Edit(_rig.A, TextOnA);
        Assert.Equal([Id], Pushed(Done(await _rig.RunA())).Written);
        _rig.B.Push.Now += 120_000;
        Edit(_rig.B, TextOnB);
        _rig.B.Push.AddMemory(Tier.Warm, newId, "Added on B beside the conflict.");
        var b = Done(await _rig.RunB());

        Assert.Empty(b.Apply!.Deferred);
        Assert.Equal(Remote.Head(), _rig.B.Pull.Last);
        Assert.Equal([Id, newId], Pushed(b).Written.Order(StringComparer.Ordinal));
        Assert.Equal("Added on B beside the conflict.", RemoteRecord(newId).Content);
        Assert.Equal("open", Item(_rig.B)!.State);
        Assert.Equal(held, GitSyncReviews.ReadHeldItem(_rig.B.Pull.Connection, heldId));
        Assert.Equal(pin, GitSyncReviews.ReadPinItem(_rig.B.Pull.Connection, pinId, "new-pin"));
        var items = Service(_rig.B).List().Items;
        Assert.Contains(items, i => i.Kind == "conflict" && i.EntryId == Id);
        Assert.Contains(items, i => i.Kind == "held-delete" && i.EntryId == heldId);
        Assert.Contains(items, i => i.Kind == "new-pin" && i.EntryId == pinId);

        // a following cycle still has nothing to wait for
        _rig.B.Push.Now += 60_000;
        var next = Done(await _rig.RunB());
        Assert.IsType<PushOutcome.NothingToPush>(next.Push);
        Assert.Equal("open", Item(_rig.B)!.State);
    }

    // ---- a secret ----

    /// <summary>
    /// B writes a text holding a token (its scan blocks it) while A's later clean edit is published: B's pull applies A's text
    /// and files B's token text as a local loser, and B's next scan lifts the block. Swapping the loser back makes it B's text
    /// again with a pending stamp. Returns the token, the swapped text, the remote record and the head before the swap's cycle.
    /// </summary>
    private async Task<(string Token, string Tainted, SyncRecordModule.SyncRecord Before, string Head)> SwapInATokenText()
    {
        var token = Fakes.Get("github-token");
        var tainted = SharedText + " The deploy token is " + token;
        await SyncedOnBoth();
        _rig.B.Push.Now += 60_000;
        Edit(_rig.B, tainted);
        _rig.A.Push.Now += 120_000;
        Edit(_rig.A, TextOnA);
        Assert.Equal([Id], Pushed(Done(await _rig.RunA())).Written);
        var met = Done(await _rig.RunB());
        Assert.Equal(TextOnA, TextOf(_rig.B));
        Assert.Equal([$"local|{tainted}|{TwoMachineRig.MachineB}"], Losers(_rig.B));
        Assert.DoesNotContain(Id, Finished(met).Written);
        Assert.Equal([Id], met.Scan.Blocked);   // B's scan blocked the token text before the pull replaced it
        _rig.B.Push.Now += 60_000;
        Assert.Empty(Done(await _rig.RunB()).Scan.Blocked);
        Assert.Null(RowOf(_rig.B).BlockedReason);   // B's next scan lifts the block: A's clean text is what B holds now
        var before = RemoteRecord();
        var head = Remote.Head();

        var result = await Service(_rig.B).SwapAsync(Id, Item(_rig.B)!.RecordHash, 1);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(tainted, TextOf(_rig.B));
        Assert.NotNull(RowOf(_rig.B).ContentStamp);
        _rig.B.Push.Now += 60_000;
        return (token, tainted, before, head);
    }

    /// <summary>
    /// After the swap of a token text (<see cref="SwapInATokenText"/>) B's next cycle: the scan's secret pass blocks the
    /// swapped text and the push skips the blocked row, so the record keeps A's text, the token is in no object of the
    /// repository and the stamp stays pending. The push's own re-check is proven by the blind-scan variant below and by
    /// <c>GitSyncPusherSecretTests</c>.
    /// </summary>
    [GitFact]
    public async Task ASwapOfATextHoldingAToken_IsBlockedByTheScanAndSkippedByThePush()
    {
        var (token, tainted, before, head) = await SwapInATokenText();

        var b = Done(await _rig.RunB());

        var push = Finished(b);
        Assert.DoesNotContain(Id, push.Written);
        Assert.Equal([Id], b.Scan.Blocked);
        Assert.Contains(new PushItem(Id, SyncSecret.blockReason), push.Skipped);
        Assert.Equal(head, Remote.Head());
        Assert.Equal(before, RemoteRecord());
        Assert.Equal(TextOnA, RemoteRecord().Content);
        Assert.DoesNotContain(token, EveryObject());
        Assert.Equal(SyncSecret.blockReason, RowOf(_rig.B).BlockedReason);
        Assert.NotNull(RowOf(_rig.B).ContentStamp);   // still pending: pushed once the token is gone
        Assert.Equal(tainted, TextOf(_rig.B));
    }

    /// <summary>
    /// As above, but B runs the cycle's steps with a scan that misses the token (<see cref="PushRig.ScanDetector"/>), so the
    /// push's own secret check is what holds the swapped text back.
    /// </summary>
    [GitFact]
    public async Task ASwapOfATextHoldingAToken_WithABlindScan_IsHeldBackByThePushsSecretCheck()
    {
        var (token, tainted, before, head) = await SwapInATokenText();
        _rig.B.Push.ScanDetector = _ => [];

        var (apply, scan) = await _rig.B.Push.PullAsync();
        Assert.Empty(scan.Blocked);
        Assert.True(apply.Complete);
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
        Assert.Equal(tainted, TextOf(_rig.B));
    }
}
