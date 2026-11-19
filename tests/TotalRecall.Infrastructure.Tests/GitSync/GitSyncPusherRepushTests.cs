using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// The push of the groups a reset repository reverted (milestone 1b-ii, D16): a row with a <c>repush_groups</c> marker is a
/// candidate, each flagged group with no pending stamp is patched into the record at <c>R</c> from the row's base, with the
/// base's stamp and no clock call, and the finalize of a pushed (or already equal) record clears the marker.
/// </summary>
public sealed class GitSyncPusherRepushTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";
    private const string C = "c3d4e5f6";
    private const string All = "content,sticky,tier";

    private readonly PushRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private GitSyncRow Row(string id) => GitSyncRows.ReadAll(_rig.Pull.Connection)[id];

    private string RemoteText(string id) => _rig.RemoteFile(SyncValidation.recordPath(id));

    private static string Text(SyncRecordModule.SyncRecord record) => Encoding.UTF8.GetString(RecordWriter.Write(record).Bytes!);

    private static SyncRecordModule.SyncRecord ReadRecord(string text, string id) =>
        RecordReader.Parse(Encoding.UTF8.GetBytes(text), SyncValidation.recordPath(id)).Parsed!.Record;

    private string? Clock => _rig.Store.GetMeta(SyncIdentity.MetaKeys.Hlc);

    /// <summary>
    /// Each record is synced at its older version, then at its newer one (the rows' bases), and the repository is reset by
    /// hand to the older commit and pulled: the pull is unproven and marks the groups the reset reverted. Returns the pull's
    /// summaries.
    /// </summary>
    private async Task<(ApplySummary Apply, ScanSummary Scan)> RevertedAsync(
        (SyncRecordModule.SyncRecord Older, SyncRecordModule.SyncRecord Newer)[] records, Action? beforeReset = null)
    {
        var old = _rig.SeedWith(pull =>
        {
            foreach (var (older, _) in records)
                pull.PutRecord(older);
        });
        await _rig.PullAsync();
        foreach (var (_, newer) in records)
            _rig.Pull.PutRecord(newer);
        _rig.Pull.Commit("another machine");
        _rig.Pull.Source.PushTo(_rig.Remote.Path);
        _rig.Now += 1000;
        await _rig.PullAsync();
        beforeReset?.Invoke();
        _rig.Remote.AddRef("refs/heads/main", old);   // a hand reset of the branch
        _rig.Now += 1000;
        var pulled = await _rig.PullAsync();
        Assert.False(pulled.Apply.Proven);
        Assert.True(pulled.Apply.Complete);
        return pulled;
    }

    // ---- what is written ----

    [GitFact]
    public async Task RepushOfOneTwoAndThreeGroups_WritesTheBaseValueAndStamp_WithNoClockCall()
    {
        var newerA = Rec(A, "a", minute: 10, tier: Tier.Hot, tierMinute: 20);                   // tier
        var newerB = Rec(B, "b", minute: 10, tier: Tier.Hot, tierMinute: 20, stickyMinute: 20);  // sticky and tier
        var newerC = Rec(C, "c, newer", minute: 20, tier: Tier.Hot);                             // all three
        var (apply, scan) = await RevertedAsync([(Rec(A, "a"), newerA), (Rec(B, "b"), newerB), (Rec(C, "c"), newerC)]);
        Assert.Equal([new RepushEntry(A, "tier"), new RepushEntry(B, "sticky,tier"), new RepushEntry(C, All)], apply.RepushMarked);
        // the machine's clock as a scan leaves it: a stamp issued by the push would move it
        _rig.Store.SetMeta(SyncIdentity.MetaKeys.Hlc, SyncStamp.ClockModule.serialize(new SyncStamp.Clock(_rig.Now, 3)));
        var clock = Clock;

        var outcome = await _rig.Push(apply, scan);

        var pushed = Assert.IsType<PushOutcome.Pushed>(outcome).Summary;
        Assert.Equal([A, B, C], pushed.Written);
        Assert.Equal(0, pushed.New);
        Assert.Equal(3, pushed.Changed);
        Assert.Empty(pushed.Skipped);
        Assert.Equal("sync: testbox +0 ~3 -0", _rig.Remote.Git("log", "-1", "--format=%s", "main").Trim());
        Assert.Equal(clock, Clock);   // no stamp was issued
        Assert.Equal(Text(newerA), RemoteText(A));
        Assert.Equal(Text(newerB), RemoteText(B));
        Assert.Equal(Text(newerC), RemoteText(C));
        foreach (var id in new[] { A, B, C })
        {
            var row = Row(id);
            Assert.Null(row.RepushGroups);
            Assert.Null(row.PushingStamps);
            Assert.Equal(RemoteText(id), row.BaseJson);
        }
    }

    [GitFact]
    public async Task RepushKeepsLocalOnlyState_OutOfTheRecord()
    {
        var newerA = Rec(A, "a, newer", minute: 20, tier: Tier.Hot);
        var pinnedB = Rec(B, "b, newer", minute: 20, sticky: true);
        var pinnedC = Rec(C, "c, newer", minute: 20, sticky: true);
        var (apply, scan) = await RevertedAsync(
            [(Rec(A, "a"), newerA), (Rec(B, "b"), Rec(B, "b, newer", minute: 20)), (Rec(C, "c"), Rec(C, "c, newer", minute: 20))],
            beforeReset: () =>
            {
                // A: a local demotion (recorded by the scan, never stamped: tiers only go up)
                _rig.Store.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, A);
                _rig.Now += 1000;
                _rig.Scan();
                Assert.Null(Row(A).TierStamp);
                // B: a declined pin (the base pins, the entry stays unpinned; the state an answered new-pin review leaves)
                GitSyncRows.Write(_rig.Pull.Connection, Row(B) with { BaseJson = Text(pinnedB) });
                // C: a held pin-edit (the entry is pinned with its own text; the base holds the record's text; nothing pending)
                _rig.Pin(C);
                _rig.Edit(Tier.Hot, C, new UpdateEntryOpts { Content = "My pinned text." });
                _rig.Scan();
                GitSyncRows.Write(_rig.Pull.Connection, Row(C) with
                {
                    BaseJson = Text(pinnedC),
                    ContentStamp = null,
                    StickyStamp = null,
                    TierStamp = null,
                });
            });
        Assert.Equal([new RepushEntry(A, All), new RepushEntry(B, All), new RepushEntry(C, All)], apply.RepushMarked);

        var outcome = await _rig.Push(apply, scan);

        Assert.Equal([A, B, C], Assert.IsType<PushOutcome.Pushed>(outcome).Summary.Written);
        var recordA = ReadRecord(RemoteText(A), A);
        Assert.Equal(Tier.Hot, recordA.Placement.Tier);         // the base's tier, not the local demotion
        Assert.Equal(St(20), recordA.Placement.TierStamp);
        Assert.Equal(Text(newerA), RemoteText(A));
        var recordB = ReadRecord(RemoteText(B), B);
        Assert.True(recordB.Placement.Sticky);                  // the base's pin, not the local unpinned entry
        Assert.Equal(St(20), recordB.Placement.StickyStamp);
        Assert.Equal(Text(pinnedB), RemoteText(B));
        Assert.Equal(Text(pinnedC), RemoteText(C));             // the base's text, not the held pin-edit
        // the local entries keep their local-only state
        Assert.NotNull(_rig.Store.Get(Tier.Warm, ContentType.Memory, A));
        Assert.Null(_rig.Store.Get(Tier.Hot, ContentType.Memory, A));
        Assert.False(_rig.Store.IsSticky(ContentType.Memory, B));
        Assert.Equal("My pinned text.", _rig.Store.Get(Tier.Hot, ContentType.Memory, C)!.Content);
    }

    [GitFact]
    public async Task APlannedRepush_KeepsThePendingGroupsInThePlan_AndTheRepushedOnesBesideIt()
    {
        SyncStamp.Stamp? edited = null;
        var (apply, scan) = await RevertedAsync(
            [(Rec(A, "a"), Rec(A, "a, newer", minute: 20, tier: Tier.Hot))],
            beforeReset: () =>
            {
                _rig.Edit(Tier.Hot, A, new UpdateEntryOpts { Content = "edited here" });
                _rig.Now += 1000;
                _rig.Scan();
                edited = Row(A).ContentStamp;
            });
        var pusher = _rig.Pusher();
        var prepared = Assert.IsType<PrepareResult.Ready>(await pusher.PrepareAsync(apply, scan, default)).Value;

        var entry = Assert.Single(Assert.IsType<PlanResult.Planned>(pusher.Plan(prepared, default)).Entries);

        Assert.Equal(edited, entry.Plan!.Content.Value.Stamp);          // the normal plan: the pending group only
        Assert.True(entry.Plan.Sticky is null && entry.Plan.Tier is null);
        Assert.True(entry.Repush!.Content is null);                     // the repush: the flagged groups with nothing pending
        Assert.Equal(St(20), entry.Repush.Sticky.Value.Stamp);
        Assert.Equal((Tier.Hot, St(20)), (entry.Repush.Tier.Value.Tier, entry.Repush.Tier.Value.Stamp));
        Assert.Equal(new SyncMerge.GroupStamps(edited, St(20), St(20)), entry.Pushed);
        Assert.Equal(SyncApply.PushingStamps.encode(entry.Pushed).Value, Row(A).PushingStamps);
        Assert.Equal("sticky,tier", Row(A).RepushGroups);                // nothing is cleared before the push
    }

    [GitFact]
    public async Task GroupWithAPendingStampIsNotRepushed_ButItsPendingChangeIsPushed()
    {
        SyncStamp.Stamp? edited = null;
        var (apply, scan) = await RevertedAsync(
            [(Rec(A, "a"), Rec(A, "a, newer", minute: 20, tier: Tier.Hot))],
            beforeReset: () =>
            {
                _rig.Edit(Tier.Hot, A, new UpdateEntryOpts { Content = "edited here" });
                _rig.Now += 1000;
                _rig.Scan();
                edited = Row(A).ContentStamp;
            });
        Assert.NotNull(edited);
        Assert.Equal([new RepushEntry(A, "sticky,tier")], apply.RepushMarked);
        // even a marker that names the content group does not repush it while a content stamp is pending
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { RepushGroups = All });

        var outcome = await _rig.Push(apply, scan);

        Assert.Equal([A], Assert.IsType<PushOutcome.Pushed>(outcome).Summary.Written);
        var record = ReadRecord(RemoteText(A), A);
        Assert.Equal("edited here", record.Content);            // the pending change, with its own stamp
        Assert.Equal(edited, record.ContentStamp);
        Assert.Equal(Tier.Hot, record.Placement.Tier);          // the repushed groups, from the base
        Assert.Equal(St(20), record.Placement.TierStamp);
        Assert.Equal(St(20), record.Placement.StickyStamp);
        var row = Row(A);
        Assert.Null(row.ContentStamp);
        Assert.Null(row.RepushGroups);
        Assert.Null(row.PushingStamps);
        Assert.Equal(RemoteText(A), row.BaseJson);
    }

    [GitFact]
    public async Task RepushedRecord_HasNoUnknownFieldLoss()
    {
        var path = SyncValidation.recordPath(A);
        var older = RecordFixtures.Unknowns.Replace("3f9c2a7e-1111", A);
        var newer = older.Replace("Use bun, not npm, in this repo.", "Use pnpm.").Replace("2026-11-12T09:14:03.512Z", "2026-11-12T09:20:00.000Z");
        var old = _rig.SeedWith(pull => pull.PutText(path, older));
        await _rig.PullAsync();
        _rig.Pull.PutText(path, newer);
        _rig.Pull.Commit("another machine");
        _rig.Pull.Source.PushTo(_rig.Remote.Path);
        _rig.Now += 1000;
        await _rig.PullAsync();
        _rig.Remote.AddRef("refs/heads/main", old);
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        Assert.Equal([new RepushEntry(A, All)], apply.RepushMarked);

        var outcome = await _rig.Push(apply, scan);

        Assert.Equal([A], Assert.IsType<PushOutcome.Pushed>(outcome).Summary.Written);
        var written = RemoteText(A);
        var record = ReadRecord(written, A);
        Assert.Equal("Use pnpm.", record.Content);
        Assert.Equal(ReadRecord(newer, A).ContentStamp, record.ContentStamp);
        Assert.True(record.Source is null);   // a source this version cannot parse ...
        foreach (var unknown in new[] { "\"source\": \"windsurf\"", "\"future\": true", "\"alpha-top\": \"z\"", "\"note\": \"p\"", "\"extra\": 7", "\"n2\": 1.50" })
            Assert.Contains(unknown, written);
        Assert.Equal(written, Row(A).BaseJson);
    }

    // ---- the marker ----

    [GitFact]
    public async Task MarkerSurvivesAFailedPush_AndANonFastForwardRetry()
    {
        var newer = Rec(A, "a, newer", minute: 20, tier: Tier.Hot);
        var (apply, scan) = await RevertedAsync([(Rec(A, "a"), newer)]);
        var reset = _rig.Remote.Head();
        var baseText = Row(A).BaseJson;
        var baseStamps = SyncApply.PushingStamps.encode(new SyncMerge.GroupStamps(St(20), St(20), St(20))).Value;

        // a push the host refuses
        _rig.Pull.Fault = c => c.IsPush ? FaultingGitProcess.Failure(SyncGit.FailureKind.Auth, "authentication failed") : null;
        Assert.IsType<PushOutcome.Failed>(await _rig.Push(apply, scan));
        _rig.Pull.Fault = _ => null;

        Assert.Equal(reset, _rig.Remote.Head());
        Assert.Equal(All, Row(A).RepushGroups);
        Assert.Equal(baseText, Row(A).BaseJson);
        Assert.Equal(baseStamps, Row(A).PushingStamps);   // the pushed stamps of a repush are the base's

        // the next pull has nothing new and keeps the marker; its push loses the race for the branch
        _rig.Now += 1000;
        var (apply2, scan2) = await _rig.PullAsync();
        Assert.Equal([new RepushEntry(A, All)], apply2.RepushMarked);
        var pusher = _rig.Pusher();
        pusher.BeforePush = () => _rig.Remote.PushCommit("unrelated.txt");
        Assert.IsType<PushOutcome.NonFastForward>(await pusher.PushAsync(apply2, scan2));

        Assert.Equal(All, Row(A).RepushGroups);
        Assert.Equal(baseText, Row(A).BaseJson);

        // the retry's pull is proven and still repushes
        _rig.Now += 1000;
        var (apply3, scan3) = await _rig.PullAsync();
        Assert.True(apply3.Proven);
        Assert.Equal([new RepushEntry(A, All)], apply3.RepushMarked);
        Assert.Equal([A], Assert.IsType<PushOutcome.Pushed>(await _rig.Push(apply3, scan3)).Summary.Written);
        Assert.Equal(Text(newer), RemoteText(A));
        Assert.Null(Row(A).RepushGroups);
    }

    [GitFact]
    public async Task FinalizeClearsTheMarker_AndSetsTheBaseToTheWrittenRecord()
    {
        var (apply, scan) = await RevertedAsync([(Rec(A, "a"), Rec(A, "a, newer", minute: 20, tier: Tier.Hot))]);
        var before = Row(A);

        var pushed = Assert.IsType<PushOutcome.Pushed>(await _rig.Push(apply, scan)).Summary;

        Assert.Equal([A], pushed.Written);
        Assert.Equal(before with { BaseJson = RemoteText(A), RepushGroups = null, PushingStamps = null }, Row(A));
        Assert.Equal(_rig.Remote.Head(), _rig.Pull.Last);

        // the next pull finds the record equal to the base: no marker, nothing to push
        _rig.Now += 1000;
        var (apply2, scan2) = await _rig.PullAsync();
        Assert.Empty(apply2.RepushMarked);
        Assert.Null(Row(A).RepushGroups);
        Assert.IsType<PushOutcome.NothingToPush>(await _rig.Push(apply2, scan2));
    }

    [GitFact]
    public async Task ARepushWhoseRecordIsAlreadyAtR_WritesNothing_AndClearsTheMarker()
    {
        _rig.Seed(Rec(A, "a", tier: Tier.Hot));
        var (apply, scan) = await _rig.PullAsync();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { RepushGroups = All });
        var head = _rig.Remote.Head();
        var before = Row(A);

        var outcome = await _rig.Push(apply, scan);

        var nothing = Assert.IsType<PushOutcome.NothingToPush>(outcome).Summary;
        Assert.Empty(nothing.Written);
        Assert.Equal(1, nothing.Unchanged);
        Assert.Equal(head, _rig.Remote.Head());
        Assert.Equal(before with { BaseJson = RemoteText(A), RepushGroups = null }, Row(A));
    }

    [GitFact]
    public async Task MarkerRowWithAnAbsentRecord_WritesNothing()
    {
        // a row with a base, no record in R, nothing pending and a marker: nothing to patch, so the normal exclusion applies
        _rig.Seed(Rec(B, "b"));
        _rig.AddMemory(Tier.Warm, A, content: "a");
        var (apply, _) = await _rig.PullAsync();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with
        {
            BaseJson = Text(Rec(A, "a")),
            ContentStamp = null,
            StickyStamp = null,
            TierStamp = null,
            RepushGroups = All,
        });
        var head = _rig.Remote.Head();
        var rows = _rig.RowDump();

        var outcome = await _rig.Push(apply, ScanSummary.Empty);

        var nothing = Assert.IsType<PushOutcome.NothingToPush>(outcome).Summary;
        Assert.Equal([new PushItem(A, "record-missing")], nothing.Skipped);
        Assert.Empty(nothing.Written);
        Assert.Equal(head, _rig.Remote.Head());
        Assert.Equal(rows, _rig.RowDump());
    }
}
