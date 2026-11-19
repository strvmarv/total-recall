using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-ii: the acceptance criteria of the deletions design that no earlier test covers end to end, each run as
/// whole cycles of two machines on one bare repository (<see cref="TwoMachineRig"/>). The other criteria are pinned by
/// <see cref="TwoMachineTests"/> (a local deletion pushed, a text edit beating a forget in both orders, a hand-reset
/// remote, a failed push after an unproven apply) and by the applier and pusher tests.
/// </summary>
public sealed class GitSyncCycleDeletionTests : IDisposable
{
    private const string Id = "3f9c2a7e";
    private const string X = "a1b2c3d4";
    private const string Y = "b2c3d4e5";
    private const string Z = "c3d4e5f6";
    private const string P = "d4e5f6a7";

    private readonly TwoMachineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ---- helpers ----

    private BareRemote Remote => _rig.A.Push.Remote;

    private static CycleSummary Done(CycleOutcome outcome) => Assert.IsType<CycleOutcome.Done>(outcome).Summary;

    private static PushSummary Pushed(CycleSummary summary) => Assert.IsType<PushOutcome.Pushed>(summary.Push).Summary;

    private static string[] Ids(int count) => [.. Enumerable.Range(0, count).Select(i => $"3f9c2a{i:x2}")];

    private static GitSyncRow RowOf(CycleRig machine, string id) => GitSyncRows.ReadAll(machine.Pull.Connection)[id];

    private static bool Has(CycleRig machine, string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(t => machine.Pull.Store.Get(t, ContentType.Memory, id) is not null);

    private static void Forget(CycleRig machine, string id)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (machine.Pull.Store.Get(tier, ContentType.Memory, id) is not null)
                machine.Pull.Store.Delete(tier, ContentType.Memory, id);
    }

    private static Dictionary<string, HeldItem> HeldOn(CycleRig machine) => GitSyncReviews.ReadOpenHeld(machine.Pull.Connection);

    private string RemoteText(string id, string rev = "main") => Remote.Git("show", rev + ":" + SyncValidation.recordPath(id));

    private bool RemoteHas(string id, string rev = "main") =>
        Remote.Git("ls-tree", "--name-only", rev, "--", SyncValidation.recordPath(id)).Trim() != "";

    private string LastMessage() => Remote.Git("log", "-1", "--format=%s", "main").Trim();

    private static SyncRecordModule.SyncRecord ReadRecord(string text, string id) =>
        RecordReader.Parse(Encoding.UTF8.GetBytes(text), SyncValidation.recordPath(id)).Parsed!.Record;

    /// <summary>Moves both machines' push clocks on, so a later local change gets a later stamp.</summary>
    private void Tick(long ms = 60_000)
    {
        _rig.A.Push.Now += ms;
        _rig.B.Push.Now += ms;
    }

    /// <summary>A seeds the remote with these records, then one cycle of A, B joins, one cycle of B: both hold every record as its base.</summary>
    private async Task BothSynced(params SyncRecordModule.SyncRecord[] records)
    {
        _rig.Seed(records);
        Done(await _rig.RunA());
        _rig.JoinB();
        Done(await _rig.RunB());
        foreach (var record in records)
        {
            Assert.True(Has(_rig.A, record.Id) && Has(_rig.B, record.Id), record.Id);
            Assert.NotNull(RowOf(_rig.B, record.Id).BaseJson);
        }
        Tick();
    }

    /// <summary>
    /// Another machine commits on top of the remote's current head (through the scratch repository the seed used, brought up
    /// to that head first) and pushes; returns the new head.
    /// </summary>
    private string AnotherMachine(Action<PullRig> change)
    {
        var source = _rig.A.Pull;
        source.Source.Git("fetch", Remote.Path, "main");
        source.Source.Git("update-ref", "refs/heads/main", "FETCH_HEAD");
        source.Source.Git("read-tree", "refs/heads/main");
        change(source);
        var head = source.Commit("another machine");
        source.Source.PushTo(Remote.Path);
        return head;
    }

    // ---- AC 1: a forget beats B's unpushed tag, project or promotion change ----

    [GitTheory]
    [InlineData("tag")]
    [InlineData("project")]
    [InlineData("promotion")]
    public async Task Forget_OnA_DeletesOnB_EvenWithPendingTagProjectOrPromotionOnB(string change)
    {
        await BothSynced(Rec(Id, "Use bun, not npm."));
        switch (change)
        {
            case "tag":
                _rig.B.Push.Edit(Tier.Warm, Id, new UpdateEntryOpts { Tags = ["build"] });
                break;
            case "project":
                _rig.B.Push.Edit(Tier.Warm, Id, new UpdateEntryOpts { Project = "o/r" });
                break;
            default:
                _rig.B.Push.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, Id);
                break;
        }
        Forget(_rig.A, Id);

        var a = Done(await _rig.RunA());
        Assert.Equal([Id], Pushed(a).Removed);
        var b = Done(await _rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.True(b.Apply.Proven);
        Assert.Equal([Id], b.Apply.Deleted);
        Assert.Equal([new NoteEntry(Id, "pending-changes-dropped")], b.Apply.Notes);
        Assert.Empty(b.Apply.Held);
        Assert.False(Has(_rig.B, Id));
        var row = RowOf(_rig.B, Id);
        Assert.Equal(SyncMerge.RowState.Forgotten, row.State);
        Assert.NotNull(row.BaseJson);
        Assert.Null(row.ContentStamp);
        Assert.Null(row.TierStamp);
        Assert.IsType<PushOutcome.NothingToPush>(b.Push);   // the dropped change never writes the record back
        Assert.False(RemoteHas(Id));
        Assert.Equal(Remote.Head(), _rig.B.Pull.Last);
    }

    // ---- AC 2: a forgotten memory is not brought back by the same or older text; newer text brings it back ----

    [GitFact]
    public async Task ForgottenMemory_IsNotReinserted_BySameTextOrOlderTextAfterAReset_AndNewerTextBringsItBackNonSticky()
    {
        var older = _rig.Seed(Rec(X, "Old text of X", minute: 10), Rec(Y, "Old text of Y", minute: 10));
        Done(await _rig.RunA());
        _rig.JoinB();
        Done(await _rig.RunB());
        AnotherMachine(pull =>
        {
            pull.PutRecord(Rec(X, "Text of X", minute: 20));
            pull.PutRecord(Rec(Y, "Text of Y", minute: 20));
        });
        Done(await _rig.RunA());
        Done(await _rig.RunB());
        Tick();
        Forget(_rig.A, X);
        Forget(_rig.A, Y);
        Assert.Equal([X, Y], Pushed(Done(await _rig.RunA())).Removed);
        Assert.Equal([X, Y], Done(await _rig.RunB()).Apply!.Deleted);
        var machines = new[] { _rig.A, _rig.B };

        // a record with the same text (and a newer stamp) comes back: neither machine re-inserts it
        AnotherMachine(pull =>
        {
            pull.PutRecord(Rec(X, "Text of X", minute: 30));
            pull.PutRecord(Rec(Y, "Text of Y", minute: 30));
        });
        foreach (var machine in machines)
        {
            var same = Done(await machine.Run());
            Assert.True(same.Apply!.Complete);
            Assert.Equal(0, same.Apply.Inserted);
            Assert.IsType<PushOutcome.NothingToPush>(same.Push);
        }

        // a hand reset to the first commit brings the older text back: an unproven pull re-inserts nothing either
        Remote.AddRef("refs/heads/main", older);
        foreach (var machine in machines)
        {
            var reset = Done(await machine.Run());
            Assert.False(reset.Apply!.Proven);
            Assert.True(reset.Apply.Complete);
            Assert.Equal(0, reset.Apply.Inserted);
            Assert.Empty(reset.Apply.Held);
            Assert.IsType<PushOutcome.NothingToPush>(reset.Push);
        }
        foreach (var machine in machines)
            foreach (var id in new[] { X, Y })
            {
                Assert.False(Has(machine, id), id);
                Assert.Equal(SyncMerge.RowState.Forgotten, RowOf(machine, id).State);
            }

        // newer text that does not pin brings X back, non-sticky, with a note; newer text that pins Y goes to review
        AnotherMachine(pull =>
        {
            pull.PutRecord(Rec(X, "Newer text of X", minute: 40));
            pull.PutRecord(Rec(Y, "Newer text of Y", sticky: true, minute: 40));
        });
        foreach (var machine in machines)
        {
            var newer = Done(await machine.Run());
            Assert.True(newer.Apply!.Proven);
            Assert.Equal(1, newer.Apply.Inserted);
            Assert.Equal([new NoteEntry(X, "edit-beats-deletion")], newer.Apply.Notes);
            Assert.Equal([new PostponedEntry(Y, "review")], newer.Apply.Postponed);
            Assert.False(newer.Apply.Complete);
            var entry = machine.Pull.Store.Get(Tier.Warm, ContentType.Memory, X);
            Assert.NotNull(entry);
            Assert.Equal("Newer text of X", entry!.Content);
            Assert.False(machine.Pull.Store.IsSticky(ContentType.Memory, X));
            Assert.Equal(SyncMerge.RowState.Active, RowOf(machine, X).State);
            Assert.False(Has(machine, Y));
        }
    }

    // ---- AC 3: a pin on B holds another machine's deletion ----

    [GitTheory]
    [InlineData(true)]    // B pushed the pin before A deleted the memory
    [InlineData(false)]   // the pin is only pending on B
    public async Task PinnedOnB_DeletedOnA_IsHeldWithCauseSticky_AlsoWhenThePinIsOnlyPending(bool pinPushed)
    {
        await BothSynced(Rec(Id, "Use bun, not npm."));
        _rig.B.Push.Pin(Id);
        if (pinPushed)
        {
            Assert.Equal([Id], Pushed(Done(await _rig.RunB())).Written);
            Assert.True(ReadRecord(RemoteText(Id), Id).Placement.Sticky);
        }
        Forget(_rig.A, Id);

        var a = Done(await _rig.RunA());
        Assert.Equal([Id], Pushed(a).Removed);
        var head = Remote.Head();
        var b = Done(await _rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.Empty(b.Apply.Deleted);
        Assert.Equal([new HeldEntry(Id, "remote", "sticky")], b.Apply.Held);
        var item = HeldOn(_rig.B)[Id];
        Assert.Equal(("remote", "sticky", (string?)null), (item.Direction, item.Cause, item.RecordHash));
        Assert.NotNull(_rig.B.Pull.Store.Get(Tier.Hot, ContentType.Memory, Id));   // the entry and its pin remain
        Assert.True(_rig.B.Pull.Store.IsSticky(ContentType.Memory, Id));
        var row = RowOf(_rig.B, Id);
        Assert.Equal(SyncMerge.RowState.Active, row.State);
        Assert.NotNull(row.BaseJson);
        var push = Assert.IsType<PushOutcome.NothingToPush>(b.Push).Summary;
        PushItem[] skipped = pinPushed ? [] : [new PushItem(Id, "held-delete")];   // a pending pin does not write the record back
        Assert.Equal(skipped, push.Skipped);
        Assert.Equal(head, Remote.Head());
        Assert.False(RemoteHas(Id));
        Assert.Equal(head, _rig.B.Pull.Last);
    }

    // ---- AC 4: the delete guard across two machines ----

    [GitFact]
    public async Task OverTheLimit_AllHeld_CycleCompletes_OtherChangesPush()
    {
        var ids = Ids(12);
        await BothSynced([.. ids.Select(id => Rec(id, "Text of " + id))]);
        var gone = ids[..11];
        var kept = ids[11];
        foreach (var id in gone)
            Forget(_rig.A, id);
        var a = Done(await _rig.A.Cycle(_rig.A.Settings() with { DeleteGuardPercent = 100 }).RunAsync());   // A's own limit is 12
        Assert.Equal(gone, Pushed(a).Removed);
        _rig.B.Push.AddMemory(Tier.Warm, X, "A new memory of B.");   // another change on B
        Forget(_rig.B, kept);                                         // and a deletion of the other direction

        var b = Done(await _rig.RunB());

        // 13 active rows at the start of the pull: the limit is max(10, 13 * 20 / 100) = 10, and 11 remote deletions trip it
        Assert.True(b.Apply!.Complete);
        Assert.True(b.Apply.Proven);
        Assert.Equal([new GuardTrip("remote", 11, 10)], b.Apply.GuardTripped);
        Assert.Equal(gone.Select(id => new HeldEntry(id, "remote", "guard")), b.Apply.Held);
        Assert.Empty(b.Apply.Deleted);
        Assert.Equal([kept], b.Apply.PushDeletions);   // the local direction is judged on its own
        Assert.Null(b.PushSkipped);
        var push = Pushed(b);
        Assert.Equal([X], push.Written);
        Assert.Equal([kept], push.Removed);
        Assert.EndsWith(" +1 ~0 -1", LastMessage());
        Assert.Equal(Remote.Head(), _rig.B.Pull.Last);
        Assert.All(gone, id => Assert.True(Has(_rig.B, id), id));
        var held = HeldOn(_rig.B);
        Assert.Equal(gone, held.Keys.Order(StringComparer.Ordinal));
        Assert.All(held.Values, h => Assert.Equal(("remote", "guard"), (h.Direction, h.Cause)));
        Assert.All(gone, id => Assert.Equal(SyncMerge.RowState.Active, RowOf(_rig.B, id).State));
        Assert.Equal(SyncMerge.RowState.Forgotten, RowOf(_rig.B, kept).State);
        Assert.False(RemoteHas(kept));
        Assert.True(RemoteHas(X));
    }

    [GitFact]
    public async Task ExactlyTheLimit_AllApplied()
    {
        var ids = Ids(12);
        await BothSynced([.. ids.Select(id => Rec(id, "Text of " + id))]);
        var gone = ids[..10];
        foreach (var id in gone)
            Forget(_rig.A, id);
        Assert.Equal(gone, Pushed(Done(await _rig.RunA())).Removed);   // 10 local deletions: A's own limit is 10 too

        var b = Done(await _rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.Empty(b.Apply.GuardTripped);
        Assert.Empty(b.Apply.Held);
        Assert.Equal(gone, b.Apply.Deleted);
        Assert.All(gone, id =>
        {
            Assert.False(Has(_rig.B, id), id);
            Assert.Equal(SyncMerge.RowState.Forgotten, RowOf(_rig.B, id).State);
        });
        Assert.True(Has(_rig.B, ids[10]) && Has(_rig.B, ids[11]));
        Assert.Empty(HeldOn(_rig.B));
        Assert.IsType<PushOutcome.NothingToPush>(b.Push);
    }

    // ---- AC 7: a held entry is written back only by a text edit; a record that reappears closes its item ----

    [GitFact]
    public async Task HeldEntryIsNotWrittenBack_ByTagPinOrPromotion_UntilTextEdited_RecordReappearingClosesTheItem()
    {
        await BothSynced(Rec(X, "Text of X"), Rec(Y, "Text of Y"), Rec(Z, "Text of Z"));
        var deletedStamp = ReadRecord(RemoteText(X), X).ContentStamp;
        foreach (var id in new[] { X, Y, Z })
            Forget(_rig.A, id);
        Assert.Equal([X, Y, Z], Pushed(Done(await _rig.RunA())).Removed);

        // B lost its clone: the pull after the new clone is unproven and holds each vanished record
        _rig.B.DeleteClone();
        var first = Done(await _rig.RunB());
        Assert.True(first.Cloned);
        Assert.False(first.Apply!.Proven);
        Assert.Equal(new[] { X, Y, Z }.Select(id => new HeldEntry(id, "remote", "unproven")), first.Apply.Held);
        var zItem = HeldOn(_rig.B)[Z];

        // a tag, a promotion and a pin each leave X's record unwritten while its item is open; Y's record reappears
        var changes = new (string Name, Action Change)[]
        {
            ("tag", () => _rig.B.Push.Edit(Tier.Warm, X, new UpdateEntryOpts { Tags = ["build"] })),
            ("promotion", () => _rig.B.Push.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, X)),
            ("pin", () => _rig.B.Push.Pin(X)),
        };
        foreach (var (name, change) in changes)
        {
            change();
            if (name == "tag")
                AnotherMachine(pull => pull.PutRecord(Rec(Y, "Text of Y")));
            Tick();
            var head = Remote.Head();
            var cycle = Done(await _rig.RunB());

            Assert.True(cycle.Apply!.Complete, name);
            string[] closed = name == "tag" ? [Y] : [];
            Assert.Equal(closed, cycle.Apply.ClosedHeld);
            Assert.Empty(cycle.Apply.Held);
            var push = Assert.IsType<PushOutcome.NothingToPush>(cycle.Push).Summary;
            Assert.Equal([new PushItem(X, "held-delete")], push.Skipped);
            Assert.Equal(head, Remote.Head());
            Assert.False(RemoteHas(X), name);
            Assert.Equal([X, Z], HeldOn(_rig.B).Keys.Order(StringComparer.Ordinal));
        }
        Assert.True(Has(_rig.B, Y));
        Assert.Equal("Text of Y", ReadRecord(RemoteText(Y), Y).Content);

        // a text edit saves X: the item is closed and the record is written back from the base with a newer stamp
        _rig.B.Push.Edit(Tier.Hot, X, new UpdateEntryOpts { Content = "Edited text of X" });
        Tick();
        var saved = Done(await _rig.RunB());

        Assert.Equal([new NoteEntry(X, "edit-beats-deletion")], saved.Apply!.Notes);
        Assert.Equal([X], saved.Apply.ClosedHeld);
        var written = Pushed(saved);
        Assert.Equal([X], written.Written);
        Assert.Equal(1, written.New);
        var record = ReadRecord(RemoteText(X), X);
        Assert.Equal("Edited text of X", record.Content);
        Assert.True(SyncStamp.StampModule.compare(record.ContentStamp, deletedStamp) > 0);
        Assert.True(record.Placement.Sticky);
        Assert.Equal(["build"], record.Tags);
        Assert.Equal(SyncMerge.RowState.Active, RowOf(_rig.B, X).State);

        // the unrelated item is untouched throughout
        Assert.Equal([Z], HeldOn(_rig.B).Keys);
        Assert.Equal(zItem, HeldOn(_rig.B)[Z]);
        Assert.True(Has(_rig.B, Z));
        Assert.False(RemoteHas(Z));
    }

    // ---- AC 10: a record added and deleted between two pulls; sync init; a lost clone ----

    [GitFact]
    public async Task ARecordAddedAndDeletedBetweenTwoPulls_IsDeleted()
    {
        await BothSynced();
        var l0 = _rig.B.Pull.Last!;
        _rig.A.Push.AddMemory(Tier.Warm, X, "Added between two pulls of B.");
        _rig.A.Push.AddMemory(Tier.Warm, P, "A pin of A.");
        _rig.A.Push.Pin(P);
        Assert.Equal([X, P], Pushed(Done(await _rig.RunA())).Written.Order(StringComparer.Ordinal));

        // B takes X, but the pin of P waits for a review: the pull is incomplete and L stays at l0
        var taken = Done(await _rig.RunB());
        Assert.False(taken.Apply!.Complete);
        Assert.Equal([new PostponedEntry(P, "review")], taken.Apply.Postponed);
        Assert.Equal(l0, _rig.B.Pull.Last);
        Assert.True(Has(_rig.B, X));
        Assert.NotNull(RowOf(_rig.B, X).BaseJson);

        Forget(_rig.A, X);
        Tick();
        Assert.Equal([X], Pushed(Done(await _rig.RunA())).Removed);
        Assert.False(RemoteHas(X, l0));   // absent from the trees of both L and R, so absent from their diff
        Assert.False(RemoteHas(X));

        var after = Done(await _rig.RunB());

        Assert.True(after.Apply!.Proven);
        Assert.Equal([X], after.Apply.Deleted);
        Assert.False(Has(_rig.B, X));
        var row = RowOf(_rig.B, X);
        Assert.Equal(SyncMerge.RowState.Forgotten, row.State);
        Assert.NotNull(row.BaseJson);
    }

    [GitFact]
    public async Task SyncInitToANewRepository_OnAMachineWithRows_DeletesNothing()
    {
        var machine = _rig.A;
        _rig.Seed(Rec(X, "Text of X"), Rec(Y, "Text of Y"));
        Done(await machine.Run());
        machine.Push.Now += 60_000;
        machine.Push.AddMemory(Tier.Warm, Z, "A pending memory of A.");
        Forget(machine, Y);   // a pending local deletion of a row with a base
        Done(await machine.Run());   // pushes Z, removes Y: every row of the old repository is settled
        Forget(machine, X);   // and one more local deletion, never pushed
        machine.Push.AddMemory(Tier.Warm, P, "Another pending memory of A.");
        Assert.NotEmpty(GitSyncRows.ReadAll(machine.Pull.Connection));
        var oldHead = Remote.Head();

        using var fresh = BareRemote.Create(commits: 0);
        var init = await new SyncSetupService(SetupEnvironment(machine, fresh.Url)).InitAsync(fresh.Url, null);
        Assert.Equal(0, init.ExitCode);
        Assert.Empty(GitSyncRows.ReadAll(machine.Pull.Connection));

        var summary = Done(await FreshCycle(machine, fresh.Url).RunAsync());

        Assert.False(summary.Apply!.Proven);
        Assert.True(summary.Apply.Complete);
        Assert.Empty(summary.Apply.Deleted);
        Assert.Empty(summary.Apply.Held);
        Assert.Empty(summary.Apply.PushDeletions);
        var push = Pushed(summary);
        Assert.Equal([Z, P], push.Written.Order(StringComparer.Ordinal));
        Assert.Empty(push.Removed);
        Assert.True(Has(machine, Z) && Has(machine, P));
        Assert.False(Has(machine, X));
        Assert.Equal(oldHead, Remote.Head());   // the old repository is untouched
        Assert.True(RemoteHas(X));
    }

    private static SyncSetupEnvironment SetupEnvironment(CycleRig machine, string address) =>
        new(
            machine.Pull.DbPath,
            false,
            GuardSettings.For(null),
            "local",
            null,
            new GitProcess(),
            new GitClientOptions { EnvironmentOverrides = machine.Pull.Source.Env.EnvironmentOverrides },
            new StubProbe(() => 404),
            new FakeUserConfig(Path.Combine(machine.Pull.Root, "data", "config.toml")),
            machine.Pull.Host,
            () => CycleRig.StartTime,
            n => [.. Enumerable.Range(0, n).Select(i => (byte)(0x50 + i))],
            "9.9.9",
            _ => { },
            null,
            null,
            null);

    /// <summary>The cycle of <paramref name="machine"/>'s database against another repository (its own paths, client and guard).</summary>
    private static GitSyncCycle FreshCycle(CycleRig machine, string address)
    {
        var paths = GitSyncPaths.For(machine.Pull.DbPath, address, caseInsensitive: false);
        var client = new GitClient(
            new GitProcess(), GitExecutable.TryFind() ?? "git", paths,
            new GitClientOptions { EnvironmentOverrides = machine.Pull.Source.Env.EnvironmentOverrides });
        var guard = new RepoGuard(
            GuardSettings.For(address), machine.Pull.Store, client, machine.Pull.Host, new StubProbe(() => 404), () => DateTimeOffset.UtcNow);
        return new GitSyncCycle(
            client, guard, machine.Pull.Store, machine.Push.Embedder, paths,
            new CycleSettings(address, "main", PushRig.Label, 0, TimeSpan.Zero), () => machine.Now, () => machine.Push.Now);
    }

    [GitFact]
    public async Task ABlockedNeverPushedMemory_SurvivesALostClone()
    {
        const string big = "e5f6a7b8";
        await BothSynced(Rec(X, "Text of X"));
        _rig.A.Push.AddMemory(Tier.Warm, big, new string('x', 262_144 + 1));
        var blocked = Assert.IsType<PushOutcome.NothingToPush>(Done(await _rig.RunA()).Push).Summary;
        Assert.Equal([new PushItem(big, "too-large")], blocked.Blocked);

        // B forgets X and adds a memory; then A loses its clone
        Forget(_rig.B, X);
        _rig.B.Push.AddMemory(Tier.Warm, Y, "A new memory of B.");
        var b = Pushed(Done(await _rig.RunB()));
        Assert.Equal([X], b.Removed);
        _rig.A.DeleteClone();
        Tick();

        var after = Done(await _rig.RunA());

        Assert.True(after.Cloned);
        Assert.False(after.Apply!.Proven);
        Assert.Empty(after.Apply.Deleted);
        Assert.Equal([new HeldEntry(X, "remote", "unproven")], after.Apply.Held);
        Assert.True(Has(_rig.A, big));
        Assert.True(Has(_rig.A, X));
        Assert.True(Has(_rig.A, Y));
        var row = RowOf(_rig.A, big);
        Assert.Equal(SyncMerge.RowState.Active, row.State);
        Assert.Null(row.BaseJson);
        Assert.Equal("too-large", row.BlockedReason);
        Assert.Equal([new PushItem(big, "too-large")], Assert.IsType<PushOutcome.NothingToPush>(after.Push).Summary.Blocked);
        Assert.False(RemoteHas(big));
    }

    // ---- AC 9: a local deletion whose record is rejected or invalid ----

    [GitTheory]
    [InlineData("rejected")]
    [InlineData("invalid")]
    public async Task ADeletedMemoryWithAnInvalidRecord_IsSkippedAndHoldsNeitherLNorOtherPushes(string shape)
    {
        await BothSynced(Rec(X, "Text of X"), Rec(Y, "Text of Y"));
        var path = SyncValidation.recordPath(X);
        Forget(_rig.B, X);
        var head = AnotherMachine(pull =>
        {
            if (shape == "rejected")
            {
                pull.Remove(path);
                pull.PutRaw(path, Encoding.UTF8.GetBytes("../x"), "120000");
            }
            else
            {
                pull.PutText(path, "{\"format\":\"total-recall/memory/v9\"}");
            }
        });
        var blob = Remote.Git("rev-parse", $"{head}:{path}").Trim();
        _rig.B.Push.AddMemory(Tier.Warm, Z, "A new memory of B.");
        var rowBefore = RowOf(_rig.B, X);

        var b = Done(await _rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.Empty(b.Apply.Postponed);
        Assert.Contains(new SkippedItem(X, null, shape == "rejected" ? "the record is rejected" : "the record is invalid"), b.Apply.Skipped);
        Assert.Empty(b.Apply.PushDeletions);
        Assert.Empty(b.Apply.Held);
        Assert.Empty(b.Apply.Deleted);
        Assert.Null(b.PushSkipped);
        var push = Pushed(b);
        Assert.Equal([Z], push.Written);
        Assert.Empty(push.Removed);
        Assert.EndsWith(" +1 ~0 -0", LastMessage());
        Assert.Equal(Remote.Head(), _rig.B.Pull.Last);   // L moved on
        Assert.Equal(blob, Remote.Git("rev-parse", $"main:{path}").Trim());   // nothing removed
        Assert.Equal(rowBefore, RowOf(_rig.B, X));
        Assert.Empty(HeldOn(_rig.B));
        Assert.False(Has(_rig.B, X));
        Assert.True(Has(_rig.B, Y));
    }

    // ---- AC 11: deletions, held items and repush markers never make a pull incomplete; a review does ----

    [GitFact]
    public async Task AfterAPullOfOnlyDeletionsHeldItemsAndRepushMarkers_TheCycleIsComplete_AndAReviewStillMakesItIncomplete()
    {
        await BothSynced(Rec(X, "Text of X"), Rec(P, "Text of P"), Rec(Z, "Text of Z", tier: Tier.Hot, minute: 20));
        var zBase = ReadRecord(RemoteText(Z), Z);
        _rig.B.Push.Pin(P);   // a pending pin on B
        // another machine deletes X and P and writes Z with a tier stamp older than the base's (a group to republish)
        AnotherMachine(pull =>
        {
            pull.Remove(SyncValidation.recordPath(X));
            pull.Remove(SyncValidation.recordPath(P));
            pull.PutRecord(Rec(Z, "Text of Z", tier: Tier.Warm, minute: 20, tierMinute: 5));
        });

        var b = Done(await _rig.RunB());

        Assert.True(b.Apply!.Complete);
        Assert.Empty(b.Apply.Postponed);
        Assert.Empty(b.Apply.Deferred);
        Assert.Equal([X], b.Apply.Deleted);
        Assert.Equal([new HeldEntry(P, "remote", "sticky")], b.Apply.Held);
        Assert.Equal([new RepushEntry(Z, "tier")], b.Apply.RepushMarked);
        Assert.Null(b.PushSkipped);
        var push = Pushed(b);
        Assert.Equal([Z], push.Written);
        Assert.EndsWith(" +0 ~1 -0", LastMessage());
        var republished = ReadRecord(RemoteText(Z), Z);
        Assert.Equal(zBase.Placement.Tier, republished.Placement.Tier);
        Assert.Equal(zBase.Placement.TierStamp, republished.Placement.TierStamp);
        Assert.Null(RowOf(_rig.B, Z).RepushGroups);
        var l = Remote.Head();
        Assert.Equal(l, _rig.B.Pull.Last);

        // a record that pins an id B lacks needs a review: the pull is incomplete, the push is skipped and L stays
        AnotherMachine(pull => pull.PutRecord(Rec(Y, "A pin of another machine.", sticky: true, minute: 30)));
        Tick();
        var review = Done(await _rig.RunB());

        Assert.False(review.Apply!.Complete);
        Assert.Equal([new PostponedEntry(Y, "review")], review.Apply.Postponed);
        Assert.All(new[] { b, review }.SelectMany(s => s.Apply!.Postponed), p => Assert.Equal("review", p.Reason));
        Assert.Equal("pull-incomplete", review.PushSkipped);
        Assert.Null(review.Push);
        Assert.Equal(l, _rig.B.Pull.Last);
        Assert.False(Has(_rig.B, Y));
    }
}
