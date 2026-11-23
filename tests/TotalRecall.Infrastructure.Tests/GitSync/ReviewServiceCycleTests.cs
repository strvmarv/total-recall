using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-iii part 2 (held deletions): the whole path of an answer, over real cycles on local bare repositories. A held
/// deletion is made by a cycle, answered through <see cref="ReviewService"/> (built by <see cref="ReviewServices.For"/> over
/// the machine's database file), and carried out by the next cycle: an accepted local hold is removed by a proven cycle, also
/// when more items are approved than the delete guard's limit (D4); a restored remote hold is published as a new record whose
/// stamps are above the old ones, sticky when the memory is pinned, and reaches a machine that never forgot it but does not
/// resurrect it on one that did (D3, W3); a restored local hold of a pinned memory pushes no unpin (D5).
/// </summary>
public sealed class ReviewServiceCycleTests
{
    private const string Id = "3f9c2a7e";

    // ---- helpers ----

    private static CycleSummary Done(CycleOutcome outcome) => Assert.IsType<CycleOutcome.Done>(outcome).Summary;

    private static PushSummary Pushed(CycleSummary summary) => Assert.IsType<PushOutcome.Pushed>(summary.Push).Summary;

    private static string[] Ids(int count) => [.. Enumerable.Range(0, count).Select(i => $"3f9c2a{i:x2}")];

    private static ReviewService Service(CycleRig machine, Func<long>? nowMs = null) =>
        ReviewServices.For(machine.Pull.DbPath, nowMs ?? (() => machine.Push.Now));

    private static HeldItem? Item(CycleRig machine, string id) => GitSyncReviews.ReadHeldItem(machine.Pull.Connection, id);

    private static GitSyncRow RowOf(CycleRig machine, string id) => GitSyncRows.ReadAll(machine.Pull.Connection)[id];

    private static bool Has(CycleRig machine, string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(t => machine.Pull.Store.Get(t, ContentType.Memory, id) is not null);

    private static void Forget(CycleRig machine, string id)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (machine.Pull.Store.Get(tier, ContentType.Memory, id) is not null)
                machine.Pull.Store.Delete(tier, ContentType.Memory, id);
    }

    private static bool RemoteHas(CycleRig machine, string id) =>
        machine.Push.Remote.Git("ls-tree", "--name-only", "main", "--", SyncValidation.recordPath(id)).Trim() != "";

    private static string RemoteBlob(CycleRig machine, string id) =>
        machine.Push.Remote.Git("rev-parse", "main:" + SyncValidation.recordPath(id)).Trim();

    private static SyncRecordModule.SyncRecord RemoteRecord(CycleRig machine, string id) =>
        RecordReader.Parse(Encoding.UTF8.GetBytes(machine.Push.RemoteFile(SyncValidation.recordPath(id))), SyncValidation.recordPath(id))
            .Parsed!.Record;

    private static SyncRecordModule.SyncRecord BaseOf(CycleRig machine, string id) =>
        ApplyPlanning.ParseBase(id, RowOf(machine, id).BaseJson!).Parsed!.Record;

    private static void AssertAbove(SyncRecordModule.SyncRecord newer, SyncRecordModule.SyncRecord older)
    {
        Assert.True(SyncStamp.StampModule.compare(newer.ContentStamp, older.ContentStamp) > 0);
        Assert.True(SyncStamp.StampModule.compare(newer.Placement.StickyStamp, older.Placement.StickyStamp) > 0);
        Assert.True(SyncStamp.StampModule.compare(newer.Placement.TierStamp, older.Placement.TierStamp) > 0);
    }

    /// <summary>
    /// Another machine removes <paramref name="id"/>'s record: a commit on top of the remote's current head, made in the
    /// scratch repository of <paramref name="source"/> (brought up to that head first) and pushed.
    /// </summary>
    private static void RemoveOnRemote(CycleRig source, string id)
    {
        var remote = source.Push.Remote;
        source.Pull.Source.Git("fetch", remote.Path, "main");
        source.Pull.Source.Git("update-ref", "refs/heads/main", "FETCH_HEAD");
        source.Pull.Source.Git("read-tree", "refs/heads/main");
        source.Pull.Remove(SyncValidation.recordPath(id));
        source.Pull.Commit("another machine removes " + id);
        source.Pull.Source.PushTo(remote.Path);
    }

    /// <summary>The machine lost its clone: the next cycle clones again and its pull is unproven.</summary>
    private static async Task<ApplySummary> UnprovenCycle(CycleRig machine)
    {
        machine.DeleteClone();
        var summary = Done(await machine.Run());
        Assert.True(summary.Cloned);
        Assert.False(summary.Apply!.Proven);
        return summary.Apply;
    }

    /// <summary>Accepts <paramref name="id"/>'s held deletion on <paramref name="machine"/> with the hash its item holds now.</summary>
    private static async Task Accept(CycleRig machine, string id)
    {
        var result = await Service(machine).AcceptAsync(id, Item(machine, id)!.RecordHash);
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>Restores <paramref name="id"/>'s held deletion on <paramref name="machine"/> (with the item's hash, which a remote hold ignores).</summary>
    private static async Task Restore(CycleRig machine, string id, Func<long>? nowMs = null)
    {
        var result = await Service(machine, nowMs).RestoreAsync(id, Item(machine, id)!.RecordHash);
        Assert.Equal(0, result.ExitCode);
    }

    // ---- accept, local hold (D4) ----

    [GitFact]
    public async Task AnAcceptedLocalHold_IsRemovedByTheNextProvenCycle()
    {
        using var rig = new CycleRig();
        rig.Push.Seed(Rec(Id, "Deleted here, then accepted."));
        Done(await rig.Run());
        Forget(rig, Id);
        var held = await UnprovenCycle(rig);
        Assert.Equal([new HeldEntry(Id, "local", "unproven")], held.Held);
        Assert.Equal(RemoteBlob(rig, Id), Item(rig, Id)!.RecordHash);

        await Accept(rig, Id);
        Assert.Equal("approved", Item(rig, Id)!.State);
        rig.Push.Now += 60_000;
        var next = Done(await rig.Run());

        Assert.True(next.Apply!.Proven);
        Assert.True(next.Apply.Complete);
        Assert.Equal([Id], next.Apply.PushDeletions);
        Assert.Empty(next.Apply.Held);
        Assert.Equal([Id], Pushed(next).Removed);
        Assert.False(RemoteHas(rig, Id));
        var row = RowOf(rig, Id);
        Assert.Equal(SyncMerge.RowState.Forgotten, row.State);
        Assert.Null(Item(rig, Id));
        Assert.False(Has(rig, Id));
        Assert.Equal(rig.Push.Remote.Head(), rig.Pull.Last);
    }

    [GitFact]
    public async Task MoreApprovedThanTheLimit_AreAllRemoved_WhileUnapprovedDeletionsAreCountedAndHeld()
    {
        using var rig = new CycleRig();
        var ids = Ids(24);
        rig.Push.Seed([.. ids.Select(id => Rec(id, "Text of " + id))]);
        Done(await rig.Run());
        var heldIds = ids[..12];
        var approved = ids[..11];
        var unapproved = ids[11];
        var fresh = ids[12..23];

        // twelve local deletions trip the guard (24 active rows: the limit is max(10, 24 * 20 / 100) = 10): all are held
        foreach (var id in heldIds)
            Forget(rig, id);
        rig.Push.Now += 60_000;
        var first = Done(await rig.Run());
        Assert.Equal([new GuardTrip("local", 12, 10)], first.Apply!.GuardTripped);
        Assert.Equal(heldIds.Select(id => new HeldEntry(id, "local", "guard")), first.Apply.Held);
        Assert.All(heldIds, id => Assert.True(RemoteHas(rig, id), id));

        // eleven are accepted, one is left open beside them; then eleven fresh local deletions
        foreach (var id in approved)
            await Accept(rig, id);
        var openItem = Item(rig, unapproved)!;
        Assert.Equal("open", openItem.State);
        foreach (var id in fresh)
            Forget(rig, id);
        rig.Push.Now += 60_000;
        var cycle = Done(await rig.Run());

        // only the fresh deletions are counted: the guard trips on them, and the approved ones are removed all the same
        Assert.True(cycle.Apply!.Complete);
        Assert.Equal([new GuardTrip("local", 11, 10)], cycle.Apply.GuardTripped);
        Assert.Equal(fresh.Select(id => new HeldEntry(id, "local", "guard")), cycle.Apply.Held);
        Assert.Equal(approved, cycle.Apply.PushDeletions.Order(StringComparer.Ordinal));
        Assert.Equal(approved, Pushed(cycle).Removed.Order(StringComparer.Ordinal));
        Assert.All(approved, id =>
        {
            Assert.False(RemoteHas(rig, id), id);
            Assert.Equal(SyncMerge.RowState.Forgotten, RowOf(rig, id).State);
            Assert.Null(Item(rig, id));
        });
        Assert.Equal(openItem, Item(rig, unapproved));
        Assert.True(RemoteHas(rig, unapproved));
        Assert.All(fresh, id =>
        {
            Assert.True(RemoteHas(rig, id), id);
            Assert.Equal(("local", "guard", "open"), (Item(rig, id)!.Direction, Item(rig, id)!.Cause, Item(rig, id)!.State));
        });
        Assert.True(Has(rig, ids[23]));
        Assert.True(RemoteHas(rig, ids[23]));
    }

    // ---- restore, remote hold (D3) ----

    [GitFact]
    public async Task ARestoredRemoteHold_IsPublishedAsANewRecord()
    {
        using var rig = new CycleRig();
        rig.Push.Seed(Rec(Id, "Removed elsewhere, restored here."));
        Done(await rig.Run());
        var old = RemoteRecord(rig, Id);
        RemoveOnRemote(rig, Id);
        var held = await UnprovenCycle(rig);
        Assert.Equal([new HeldEntry(Id, "remote", "unproven")], held.Held);
        Assert.False(RemoteHas(rig, Id));

        // the service's clock is set behind the old record's stamps: the new ones are issued above them all the same
        await Restore(rig, Id, () => 1_700_000_000_000L);
        rig.Push.Now += 60_000;
        var next = Done(await rig.Run());

        Assert.True(next.Apply!.Complete);
        var push = Pushed(next);
        Assert.Equal([Id], push.Written);
        Assert.Equal(1, push.New);
        var record = RemoteRecord(rig, Id);
        Assert.Equal("Removed elsewhere, restored here.", record.Content);
        Assert.False(record.Placement.Sticky);
        AssertAbove(record, old);
        Assert.Null(Item(rig, Id));
        Assert.True(Has(rig, Id));
        Assert.Equal(SyncMerge.RowState.Active, RowOf(rig, Id).State);
    }

    [GitFact]
    public async Task ARestoredPinnedRemoteHold_IsPublishedSticky()
    {
        using var rig = new CycleRig();
        rig.Push.Seed(Rec(Id, "Pinned here, removed elsewhere."));
        Done(await rig.Run());
        rig.Push.Pin(Id);
        rig.Push.Now += 60_000;
        Assert.Equal([Id], Pushed(Done(await rig.Run())).Written);
        var old = RemoteRecord(rig, Id);
        Assert.True(old.Placement.Sticky);
        RemoveOnRemote(rig, Id);
        rig.Push.Now += 60_000;
        var held = Done(await rig.Run());
        Assert.True(held.Apply!.Proven);
        Assert.Equal([new HeldEntry(Id, "remote", "sticky")], held.Apply.Held);

        await Restore(rig, Id);
        rig.Push.Now += 60_000;
        var next = Done(await rig.Run());

        var push = Pushed(next);
        Assert.Equal([Id], push.Written);
        Assert.Equal(1, push.New);
        var record = RemoteRecord(rig, Id);
        Assert.True(record.Placement.Sticky);
        Assert.Equal(Tier.Hot, record.Placement.Tier);
        AssertAbove(record, old);
        Assert.True(rig.Pull.Store.IsSticky(ContentType.Memory, Id));
        Assert.Null(Item(rig, Id));
    }

    [GitFact]
    public async Task ARestoredRemoteHold_ReachesAMachineThatNeverForgotIt()
    {
        using var rig = new TwoMachineRig();
        rig.Seed(Rec(Id, "Removed elsewhere, restored on A."));
        Done(await rig.RunA());
        rig.JoinB();
        Done(await rig.RunB());
        var old = BaseOf(rig.B, Id);
        RemoveOnRemote(rig.A, Id);
        Assert.Equal([new HeldEntry(Id, "remote", "unproven")], (await UnprovenCycle(rig.A)).Held);

        await Restore(rig.A, Id);
        rig.A.Push.Now += 60_000;
        Assert.Equal([Id], Pushed(Done(await rig.RunA())).Written);
        var published = RemoteRecord(rig.A, Id);
        AssertAbove(published, old);

        // B never pulled the removal: its next pull sees the record changed, keeps the memory and takes the new record as its base
        var b = Done(await rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.Empty(b.Apply.Deleted);
        Assert.Empty(b.Apply.Held);
        Assert.True(Has(rig.B, Id));
        var row = RowOf(rig.B, Id);
        Assert.Equal(SyncMerge.RowState.Active, row.State);
        Assert.Equal(published, BaseOf(rig.B, Id));
        Assert.Equal(rig.A.Push.Remote.Head(), rig.B.Pull.Last);
    }

    [GitFact]
    public async Task ARestoredRemoteHold_IsNotResurrectedOnAMachineThatForgotIt()
    {
        using var rig = new TwoMachineRig();
        rig.Seed(Rec(Id, "Removed elsewhere, restored on A."));
        Done(await rig.RunA());
        rig.JoinB();
        Done(await rig.RunB());
        RemoveOnRemote(rig.A, Id);
        Assert.Equal([Id], Done(await rig.RunB()).Apply!.Deleted);   // B runs first and forgets it
        Assert.Equal([new HeldEntry(Id, "remote", "unproven")], (await UnprovenCycle(rig.A)).Held);

        await Restore(rig.A, Id);
        rig.A.Push.Now += 60_000;
        Assert.Equal([Id], Pushed(Done(await rig.RunA())).Written);
        Assert.True(RemoteHas(rig.A, Id));

        // the same text with newer stamps: W3, B does not resurrect a memory it forgot (the documented limit)
        var b = Done(await rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.Equal(0, b.Apply.Inserted);
        Assert.Empty(b.Apply.Held);
        Assert.False(Has(rig.B, Id));
        Assert.Equal(SyncMerge.RowState.Forgotten, RowOf(rig.B, Id).State);
        Assert.IsType<PushOutcome.NothingToPush>(b.Push);
        Assert.True(RemoteHas(rig.A, Id));
        Assert.Equal(rig.A.Push.Remote.Head(), rig.B.Pull.Last);
    }

    // ---- restore, local hold (D5) ----

    [GitFact]
    public async Task RestoringALocalHoldOfAPinnedMemory_PushesNoUnpin()
    {
        using var rig = new CycleRig();
        rig.Push.Seed(Rec(Id, "Pinned here, then deleted."));
        Done(await rig.Run());
        rig.Push.Pin(Id);
        rig.Push.Now += 60_000;
        Assert.Equal([Id], Pushed(Done(await rig.Run())).Written);
        Assert.True(RemoteRecord(rig, Id).Placement.Sticky);
        Forget(rig, Id);
        Assert.Equal([new HeldEntry(Id, "local", "unproven")], (await UnprovenCycle(rig)).Held);
        var blob = RemoteBlob(rig, Id);
        var head = rig.Push.Remote.Head();

        await Restore(rig, Id);
        Assert.True(Has(rig, Id));
        Assert.False(rig.Pull.Store.IsSticky(ContentType.Memory, Id));
        rig.Push.Now += 60_000;
        var next = Done(await rig.Run());

        Assert.True(next.Apply!.Complete);
        Assert.Empty(next.Apply.Postponed);
        Assert.Null(next.PushSkipped);
        Assert.IsType<PushOutcome.NothingToPush>(next.Push);
        Assert.Equal(head, rig.Push.Remote.Head());
        Assert.Equal(blob, RemoteBlob(rig, Id));   // byte for byte the record it was
        Assert.True(RemoteRecord(rig, Id).Placement.Sticky);
        Assert.True(Has(rig, Id));
        Assert.False(rig.Pull.Store.IsSticky(ContentType.Memory, Id));
        Assert.Null(Item(rig, Id));
    }
}
