using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncPusherPrepareTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";
    private const string Sibling = "a1ffffff";

    private readonly PushRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private PushPrepared Ready(PrepareResult result) => Assert.IsType<PrepareResult.Ready>(result).Value;

    private static PushOutcome Stopped(PrepareResult result) => Assert.IsType<PrepareResult.Stop>(result).Outcome;

    private Task<PrepareResult> Prepare(ApplySummary apply, ScanSummary? scan = null, CancellationToken ct = default) =>
        _rig.Pusher().PrepareAsync(apply, scan ?? ScanSummary.Empty, ct);

    /// <summary>The state right after a pull: <c>L</c> is the seed head and the rows exist.</summary>
    private async Task<(string Head, ScanSummary Scan)> Pulled()
    {
        var (apply, scan) = await _rig.PullAsync();
        Assert.True(apply.Complete);
        return (apply.Head, scan);
    }

    /// <summary>The seed head as <c>L</c>, with no pull: rows come only from the scan.</summary>
    private string Joined(string head)
    {
        _rig.Pull.SetLast(head);
        return head;
    }

    // ---- decision 1: ready ----

    [GitFact]
    public async Task NotReady_ForAnIncompletePull_ADifferentLastSyncedCommit_AndNoLastSyncedCommit_AndNothingIsRunOrWritten()
    {
        var head = _rig.Seed(Rec(A));
        await _rig.PullAsync();
        var calls = _rig.Pull.Recorder.Calls.Count;
        var rows = _rig.RowDump();

        var incomplete = Stopped(await Prepare(PushRig.Summary(head, complete: false)));
        Assert.Equal("the pull did not complete", Assert.IsType<PushOutcome.NotReady>(incomplete).Reason);

        _rig.Pull.SetLast(new string('b', 40));
        Assert.IsType<PushOutcome.NotReady>(Stopped(await Prepare(PushRig.Summary(head))));

        _rig.Pull.SetLast(null);
        Assert.IsType<PushOutcome.NotReady>(Stopped(await Prepare(PushRig.Summary(head))));

        Assert.IsType<PushOutcome.NotReady>(Stopped(await Prepare(PushRig.Summary("not a sha"))));
        Assert.Equal(calls, _rig.Pull.Recorder.Calls.Count);   // no git command ran
        Assert.Equal(rows.Replace("last=" + head, "last="), _rig.RowDump().Replace("last=" + head, "last="));
    }

    [GitFact]
    public async Task ACancelledToken_IsCancelled_BeforeAnyGitCommand()
    {
        var head = _rig.Seed();
        await _rig.PullAsync();
        var calls = _rig.Pull.Recorder.Calls.Count;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var failed = Assert.IsType<PushOutcome.Failed>(Stopped(await Prepare(PushRig.Summary(head), ct: cts.Token)));

        Assert.Equal("Cancelled", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Equal(calls, _rig.Pull.Recorder.Calls.Count);
    }

    // ---- decision 2: the clone is reset first ----

    [GitFact]
    public async Task ALeftOverCommit_AnUntrackedFile_AreRemoved_AndTheIgnoredFileStays_AndTheAttributesFileIsExact()
    {
        var head = _rig.Seed(Rec(A));
        await _rig.PullAsync();
        var clone = _rig.Paths.CloneDirectory;
        File.AppendAllText(Path.Combine(clone, ".git", "info", "exclude"), "ignored.tmp\n");
        File.WriteAllText(Path.Combine(clone, "left-over.txt"), "from a failed push");
        File.WriteAllText(Path.Combine(clone, "ignored.tmp"), "ignored");
        _rig.Pull.CloneGit("add", "left-over.txt");
        _rig.Pull.CloneGit("commit", "-m", "left over by a failed push");
        File.WriteAllText(Path.Combine(clone, "untracked.txt"), "x");
        Assert.NotEqual(head, _rig.CloneHead());

        Ready(await Prepare(PushRig.Summary(head)));

        Assert.Equal(head, _rig.CloneHead());
        Assert.Equal("", _rig.Status().Trim());
        Assert.False(File.Exists(Path.Combine(clone, "left-over.txt")));
        Assert.False(File.Exists(Path.Combine(clone, "untracked.txt")));
        Assert.True(File.Exists(Path.Combine(clone, "ignored.tmp")));   // `clean -fd` keeps ignored files; only written paths are ever staged
        Assert.Equal(GitSyncPusher.InfoAttributes, File.ReadAllText(Path.Combine(clone, ".git", "info", "attributes")));
        Assert.Equal("* -text -eol -ident -filter -working-tree-encoding\n", GitSyncPusher.InfoAttributes);
    }

    [GitFact]
    public async Task TheAttributesFileIsWrittenBeforeTheReset()
    {
        var head = _rig.Seed();
        await _rig.PullAsync();
        var path = Path.Combine(_rig.Paths.CloneDirectory, ".git", "info", "attributes");
        File.Delete(path);
        string? seen = null;
        _rig.Pull.Fault = c =>
        {
            if (c.IsResetHard)
                seen = File.Exists(path) ? File.ReadAllText(path) : null;
            return null;
        };

        Ready(await Prepare(PushRig.Summary(head)));

        Assert.Equal(GitSyncPusher.InfoAttributes, seen);
    }

    [GitFact]
    public async Task ATrackedHostileGitattributes_DoesNotStopThePreparation()
    {
        var head = _rig.SeedWith(pull =>
        {
            pull.PutText("memories/.gitattributes", "* working-tree-encoding=UTF-16\n* ident\n");
            pull.PutRecord(Rec(A));
        });
        await _rig.PullAsync();

        Ready(await Prepare(PushRig.Summary(head)));
    }

    [GitFact]
    public async Task AFailingReset_AFailingClean_AFailingHeadCheck_AndAMissingCommit_AreFailedClone_WithNothingPublished()
    {
        var head = _rig.Seed();
        await _rig.PullAsync();

        foreach (var kind in new[] { "reset", "clean", "head" })
        {
            _rig.Pull.Fault = c =>
                (kind, c.IsResetHard, c.IsClean, c.IsRevParse) switch
                {
                    ("reset", true, _, _) or ("clean", _, true, _) or ("head", _, _, true) =>
                        FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "canned " + kind),
                    _ => null,
                };
            var failed = Assert.IsType<PushOutcome.Failed>(Stopped(await Prepare(PushRig.Summary(head))));
            Assert.Equal("Clone", failed.Kind);
            Assert.Equal(SyncPush.Published.No, failed.Published);
        }

        _rig.Pull.Fault = _ => null;
        var absent = new string('a', 40);
        _rig.Pull.SetLast(absent);
        var missing = Assert.IsType<PushOutcome.Failed>(Stopped(await Prepare(PushRig.Summary(absent))));
        Assert.Equal("Clone", missing.Kind);
    }

    [GitFact]
    public async Task AnUnreadableStore_IsFailedStore()
    {
        var head = _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        await _rig.PullAsync();
        using (var cmd = _rig.Pull.Connection.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE git_sync_issue";
            cmd.ExecuteNonQuery();
        }

        var failed = Assert.IsType<PushOutcome.Failed>(Stopped(await Prepare(PushRig.Summary(head))));

        Assert.Equal("Store", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
    }

    // ---- decision 4: candidates ----

    [GitFact]
    public async Task Candidates_AreNewRows_PendingGroups_AndATagsChange_AndNothingElse()
    {
        var head = _rig.Seed(Rec(A, content: "old"), Rec(B, content: "same", tags: ["x"]), Rec("c3d4e5f6", content: "tagged", tags: ["x"]));
        await _rig.PullAsync();
        _rig.AddMemory(Tier.Warm, "d4e5f6a7");
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "new text" });
        _rig.Edit(Tier.Warm, "c3d4e5f6", new UpdateEntryOpts { Tags = ["x", "y"] });
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();

        var prepared = Ready(await Prepare(apply, scan));

        Assert.Equal(["a1b2c3d4", "c3d4e5f6", "d4e5f6a7"], prepared.Candidates.Keys);
        Assert.Empty(prepared.Excluded);
        Assert.NotNull(prepared.Candidates[A].Record);
        Assert.NotNull(prepared.Candidates[A].RecordBytes);
        Assert.Null(prepared.Candidates["d4e5f6a7"].Record);   // a new entry has no record
        Assert.DoesNotContain(B, prepared.Candidates.Keys);
    }

    [GitFact]
    public async Task ADeletedMemory_IsNeverACandidate_AndAForgottenOrLeftRowIsNot()
    {
        var head = _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        _rig.AddMemory(Tier.Warm, B);
        await _rig.PullAsync();
        var rows = GitSyncRows.ReadAll(_rig.Pull.Connection);
        GitSyncRows.Write(_rig.Pull.Connection, rows[B] with { State = SyncMerge.RowState.Left });
        var scan = ScanSummary.Empty with { LocalDeletions = [new LocalDeletion(A, false, false)] };

        var prepared = Ready(await Prepare(PushRig.Summary(head), scan));

        Assert.Empty(prepared.Candidates);
        Assert.Empty(prepared.Excluded);
    }

    // ---- decision 5: exclusions ----

    private async Task<PushPrepared> NewEntryPrepared(string id = A, ScanSummary? scan = null)
    {
        _rig.AddMemory(Tier.Warm, id);
        var (_, scanned) = await _rig.PullAsync();
        return Ready(await Prepare(PushRig.Summary(_rig.Pull.Last!), scan ?? scanned));
    }

    [GitFact]
    public async Task AnOpenApplyFailure_AndAScanSkip_ExcludeTheEntry_AsIssue()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        _rig.AddMemory(Tier.Warm, B);
        var (apply, scan) = await _rig.PullAsync();
        GitSyncIssues.Upsert(_rig.Pull.Connection, A, GitSyncIssues.ApplyFailed, null, 1, "x");
        var skipped = ScanSummary.Empty with { Skipped = [new SkippedItem(B, "tags", "r")] };

        var prepared = Ready(await Prepare(apply, skipped));

        Assert.Equal("issue", SyncPush.exclusionName(prepared.Excluded[A]));
        Assert.Equal("issue", SyncPush.exclusionName(prepared.Excluded[B]));
    }

    [GitFact]
    public async Task ARejectedPathThatNamesTheId_ExcludesTheEntry_AsRejectedRecord()
    {
        _rig.SeedWith(pull => pull.PutRaw($"memories/a1/{A}.json", Bytes("../../etc/passwd"), "120000"));
        _rig.AddMemory(Tier.Warm, A);
        _rig.Pull.SetLast(_rig.Remote.Head());

        var prepared = Ready(await Prepare(PushRig.Summary(_rig.Remote.Head()), _rig.Scan()));

        Assert.Equal(SyncPush.Exclusion.RejectedRecord, prepared.Excluded[A]);
    }

    [GitFact]
    public async Task ANewRowWhileTheRecordIsAccepted_IsNotReconciled()
    {
        var head = _rig.Seed(Rec(A));
        Joined(head);
        _rig.AddMemory(Tier.Warm, A);

        var prepared = Ready(await Prepare(PushRig.Summary(head), _rig.Scan()));

        Assert.Equal(SyncPush.Exclusion.NotReconciled, prepared.Excluded[A]);
    }

    [GitFact]
    public async Task ARowWithABaseWhileThePathIsGone_IsRecordMissing()
    {
        _rig.Seed(Rec(A));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "edited" });
        _rig.Now += 1000;
        var scan = _rig.Scan();
        var path = SyncValidation.recordPath(A);
        _rig.Pull.CloneGit("rm", "-q", path);
        _rig.Pull.CloneGit("commit", "-m", "remote deletion");
        var head = _rig.CloneHead()!;
        _rig.Pull.SetLast(head);

        var prepared = Ready(await Prepare(PushRig.Summary(head), scan));

        Assert.Equal(SyncPush.Exclusion.RecordMissing, prepared.Excluded[A]);
    }

    [GitFact]
    public async Task ARecordThatCannotBeParsed_IsRecordUnreadable()
    {
        _rig.Seed(Rec(A));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "edited" });
        _rig.Now += 1000;
        var scan = _rig.Scan();
        File.WriteAllText(Path.Combine(_rig.Paths.CloneDirectory, SyncValidation.recordPath(A)), "{not json");
        _rig.Pull.CloneGit("add", "-A");
        _rig.Pull.CloneGit("commit", "-m", "corrupt");
        var head = _rig.CloneHead()!;
        _rig.Pull.SetLast(head);

        var prepared = Ready(await Prepare(PushRig.Summary(head), scan));

        Assert.Equal(SyncPush.Exclusion.RecordUnreadable, prepared.Excluded[A]);
        Assert.True(prepared.Candidates[A].RecordUnreadable);
    }

    [GitFact]
    public async Task AnUnparsableBase_WithAPendingStamp_IsUnreadableBase()
    {
        _rig.Seed(Rec(A));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "edited" });
        _rig.Now += 1000;
        var scan = _rig.Scan();
        var row = GitSyncRows.ReadAll(_rig.Pull.Connection)[A];
        GitSyncRows.Write(_rig.Pull.Connection, row with { BaseJson = "garbage" });

        var prepared = Ready(await Prepare(PushRig.Summary(_rig.Pull.Last!), scan));

        Assert.Equal(SyncPush.Exclusion.UnreadableBase, prepared.Excluded[A]);
        Assert.True(prepared.Candidates[A].BaseUnreadable);
    }

    [GitFact]
    public async Task ANonWriterBlockReason_IsBlockedElsewhere_AWriterOneIsNot()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        _rig.AddMemory(Tier.Warm, B);
        var (apply, scan) = await _rig.PullAsync();
        var rows = GitSyncRows.ReadAll(_rig.Pull.Connection);
        GitSyncRows.Write(_rig.Pull.Connection, rows[A] with { BlockedReason = "secret", BlockedHash = "h" });
        GitSyncRows.Write(_rig.Pull.Connection, rows[B] with { BlockedReason = "too-large", BlockedHash = "h" });

        var prepared = Ready(await Prepare(apply, scan));

        Assert.Equal(SyncPush.Exclusion.BlockedElsewhere, prepared.Excluded[A]);
        Assert.False(prepared.Excluded.ContainsKey(B));
    }

    [GitFact]
    public async Task APathInTheWay_IsPathOccupied_ASiblingIsNot()
    {
        // a case-folded shard directory, a case variant of the file, a directory where the record goes, and a sibling record
        _rig.SeedWith(pull =>
        {
            pull.PutText("memories/B2/x.json", "{}");                       // folded shard of B
            pull.PutText($"memories/c3/{"C3D4E5F6"}.json", "{}");            // case variant of the file for c3d4e5f6
            pull.PutText("memories/d4/d4e5f6a7.json/x", "{}");               // a directory at the record of d4e5f6a7
            pull.PutRecord(Rec(Sibling));                                    // a sibling in A's shard
        });
        _rig.Pull.SetLast(_rig.Remote.Head());
        foreach (var id in new[] { A, B, "c3d4e5f6", "d4e5f6a7" })
            _rig.AddMemory(Tier.Warm, id);

        var prepared = Ready(await Prepare(PushRig.Summary(_rig.Remote.Head()), _rig.Scan()));

        Assert.False(prepared.Excluded.ContainsKey(A));
        foreach (var id in new[] { B, "c3d4e5f6", "d4e5f6a7" })
            Assert.Equal(SyncPush.Exclusion.PathOccupied, prepared.Excluded[id]);
    }

    [GitFact]
    public async Task ABlobAtTheShardDirectory_AndABlobNamedMemories_OccupyThePath()
    {
        var head = _rig.SeedWith(pull => pull.Source.Add("100644", pull.Source.Blob(Bytes("x")), "memories/a1"));
        _rig.Pull.SetLast(head);
        _rig.AddMemory(Tier.Warm, A);
        Assert.Equal(SyncPush.Exclusion.PathOccupied, Ready(await Prepare(PushRig.Summary(head), _rig.Scan())).Excluded[A]);
    }

    [GitFact]
    public async Task ACancelledBlobRead_StopsThePreparation_AsCancelled()
    {
        _rig.Seed(Rec(A));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "edited" });
        _rig.Now += 1000;
        var scan = _rig.Scan();
        _rig.Pull.Fault = c => c.IsCatBlob ? FaultingGitProcess.Failure(SyncGit.FailureKind.Cancelled, "") : null;

        var failed = Assert.IsType<PushOutcome.Failed>(Stopped(await Prepare(PushRig.Summary(_rig.Pull.Last!), scan)));

        Assert.Equal("Cancelled", failed.Kind);
    }

    [GitFact]
    public async Task AnIneligibleIdNeverReachesAPath_AndABlobThatCannotBeReadIsUnreadable()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        var (apply, scan) = await _rig.PullAsync();
        // a hand-made row with an ineligible id (the scan never makes one)
        GitSyncRows.Write(_rig.Pull.Connection, GitSyncRows.ReadAll(_rig.Pull.Connection)[A] with { EntryId = "XYZ" });

        var prepared = Ready(await Prepare(apply, scan));

        Assert.True(prepared.Candidates.ContainsKey("XYZ"));
        Assert.False(prepared.Excluded.ContainsKey("XYZ"));   // no path, no exclusion: the limit check blocks it later
    }
}
