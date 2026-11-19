using System.Text;
using System.Text.Json.Nodes;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncPusherFinalizeTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";
    private const string C = "c3d4e5f6";

    private readonly PushRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private GitSyncRow Row(string id) => GitSyncRows.ReadAll(_rig.Pull.Connection)[id];

    private string? LastRef => _rig.Pull.CloneRef("refs/total-recall/last-synced");

    private async Task<(PushOutcome Outcome, GitSyncPusher Pusher, ApplySummary Apply, ScanSummary Scan)> PullAndPush(
        Action<GitSyncPusher>? configure = null, CancellationToken ct = default)
    {
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        var pusher = _rig.Pusher();
        configure?.Invoke(pusher);
        return (await pusher.PushAsync(apply, scan, ct), pusher, apply, scan);
    }

    private static T Is<T>(PushOutcome outcome) where T : PushOutcome => Assert.IsType<T>(outcome);

    private string RemoteText(string id) => _rig.Remote.Git("show", "main:" + SyncValidation.recordPath(id));

    /// <summary>Another machine pushes a change on top of the remote's head.</summary>
    private void OtherMachine(Action<string> edit)
    {
        var dir = Path.Combine(_rig.Pull.Root, "other-" + Guid.NewGuid().ToString("N"));
        var env = _rig.Pull.Source.Env;
        env.RunGit(_rig.Pull.Root, "clone", _rig.Remote.Url, dir);
        edit(dir);
        env.RunGit(dir, "add", "-A");
        env.RunGit(dir, "-c", "user.name=other", "-c", "user.email=other@example.com", "commit", "-m", "other machine");
        env.RunGit(dir, "push", "origin", "HEAD:main");
    }

    private async Task OneNewOneChanged()
    {
        _rig.Seed(Rec(A, content: "old"));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "changed text" });
        _rig.AddMemory(Tier.Warm, B, content: "brand new");
    }

    // ---- a successful push ----

    [GitFact]
    public async Task AfterAPush_TheRowsSayWhatWasPushed_LIsTheNewCommit_AndAScanStampsNothing()
    {
        await OneNewOneChanged();
        var rowsBefore = GitSyncRows.ReadAll(_rig.Pull.Connection);

        var (outcome, pusher, _, _) = await PullAndPush();

        var pushed = Is<PushOutcome.Pushed>(outcome);
        var head = _rig.Remote.Head();
        Assert.Equal(head, pushed.Summary.Commit);
        Assert.Equal(head, _rig.Pull.Last);
        Assert.Equal(head, LastRef);
        Assert.Equal(1, pushed.Summary.New);
        Assert.Equal(1, pushed.Summary.Changed);
        Assert.Equal([A, B], pushed.Summary.Written);
        Assert.Equal(2, pusher.TransactionCount);   // the plan transaction and the finalize transaction
        foreach (var id in new[] { A, B })
        {
            var row = Row(id);
            Assert.Equal(RemoteText(id), row.BaseJson);
            Assert.Null(row.ContentStamp);
            Assert.Null(row.StickyStamp);
            Assert.Null(row.TierStamp);
            Assert.Null(row.PushingStamps);
            Assert.Null(row.BlockedReason);
            Assert.NotNull(row.ScannedContentHash);
        }
        // the comparison columns describe the entry at the scan and are not touched
        Assert.Equal(Row(A).ScannedContentHash, GitSyncRows.ReadAll(_rig.Pull.Connection)[A].ScannedContentHash);
        Assert.NotEqual(rowsBefore[A].BaseJson, Row(A).BaseJson);

        var dump = _rig.RowDump();
        _rig.Now += 1000;
        var scan = _rig.Scan();
        Assert.Equal(dump, _rig.RowDump());
        Assert.Equal(0, scan.Content);
        Assert.Equal(0, scan.New);
    }

    [GitFact]
    public async Task ADifferentPendingStamp_StaysPending_AgainstTheNewBase()
    {
        await OneNewOneChanged();
        var later = SyncStamp.StampModule.tryParse("2099-01-01T00:00:00.000Z.000000.4f2a9c1e").Value;

        var (outcome, _, _, _) = await PullAndPush(p => p.BeforePush = () =>
            GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { ContentStamp = later }));

        Is<PushOutcome.Pushed>(outcome);
        Assert.Equal(later, Row(A).ContentStamp);
        Assert.Null(Row(B).ContentStamp);
    }

    [GitFact]
    public async Task AnUnchangedEntry_IsFinalized_WithTheCanonicalRecord_AndNothingIsPushedForIt()
    {
        var canonical = Encoding.UTF8.GetString(RecordWriter.Write(Rec(A, content: "old")).Bytes!);
        _rig.SeedWith(pull => pull.PutText(SyncValidation.recordPath(A), canonical.Replace("\n", "\r\n")));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "different" });
        _rig.Now += 1000;
        _rig.Scan();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { ContentStamp = St(5) });   // stale: nothing to write
        var remote = _rig.Remote.Head();
        var last = _rig.Pull.Last;

        var (outcome, _, _, _) = await PullAndPush();

        var nothing = Is<PushOutcome.NothingToPush>(outcome);
        Assert.Equal(1, nothing.Summary.Unchanged);
        Assert.Equal(remote, _rig.Remote.Head());
        Assert.Equal(last, _rig.Pull.Last);                       // L did not move
        Assert.DoesNotContain('\r', Row(A).BaseJson!);            // the canonical form, not the blob's bytes
        Assert.Equal(St(5), Row(A).ContentStamp);                 // the stale group stays pending
        Assert.Contains('\r', RemoteText(A));                     // the repository was not rewritten
    }

    // ---- every group alone ----

    private static void Flatten(JsonNode? node, string path, Dictionary<string, string> into)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                    Flatten(v, path + "/" + k, into);
                break;
            default:
                into[path] = node?.ToJsonString() ?? "null";
                break;
        }
    }

    private static Dictionary<string, string> Flat(string json)
    {
        var d = new Dictionary<string, string>();
        Flatten(JsonNode.Parse(json), "", d);
        return d;
    }

    [GitTheory]
    [InlineData("text")]
    [InlineData("tags")]
    [InlineData("pin")]
    [InlineData("unpin")]
    [InlineData("promote")]
    public async Task OneGroupChanged_ChangesOnlyItsFields_AndKeepsWhatThisVersionDoesNotKnow(string group)
    {
        var record = group switch
        {
            "promote" => Rec(A, content: "base", tier: Tier.Cold),
            _ => Rec(A, content: "base"),
        };
        var original = Encoding.UTF8.GetString(RecordWriter.Write(record).Bytes!);
        var withUnknown = original.TrimEnd('\n')[..^2] + ",\n  \"zz_future\": {\n    \"a\": 1\n  }\n}\n";
        _rig.SeedWith(pull => pull.PutText(SyncValidation.recordPath(A), withUnknown));
        await _rig.PullAsync();
        var baseline = withUnknown;
        if (group == "unpin")
        {
            // an incoming pin waits for the review of a later part, so the pin is made here and pushed first
            _rig.Pin(A);
            Is<PushOutcome.Pushed>((await PullAndPush()).Outcome);
            baseline = RemoteText(A);
        }
        switch (group)
        {
            case "text": _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "edited" }); break;
            case "tags": _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Tags = ["z", "a"] }); break;
            case "pin": _rig.Pin(A); break;
            case "unpin": _rig.Unpin(A); break;
            default: _rig.Store.Move(Tier.Cold, ContentType.Memory, Tier.Hot, ContentType.Memory, A); break;
        }

        var (outcome, _, _, _) = await PullAndPush();

        Is<PushOutcome.Pushed>(outcome);
        var before = Flat(baseline);
        var after = Flat(RemoteText(A));
        var changed = before.Keys.Union(after.Keys).Where(k => !before.TryGetValue(k, out var b) || !after.TryGetValue(k, out var a) || a != b)
            .Order(StringComparer.Ordinal).ToArray();
        var tr = "/ext/total-recall";
        string[] expected = group switch
        {
            "text" => ["/content", "/updated_at", tr + "/content_stamp", tr + "/written_by"],
            "tags" => ["/tags"],
            "pin" => [tr + "/placement/sticky", tr + "/placement/sticky_stamp", tr + "/placement/tier", tr + "/placement/tier_stamp"],
            "unpin" => [tr + "/placement/sticky", tr + "/placement/sticky_stamp"],
            _ => [tr + "/placement/tier", tr + "/placement/tier_stamp"],
        };
        // `written_by` is never changed by a patch; it is listed above only to be removed again
        Assert.Equal(expected.Where(e => !e.EndsWith("written_by")).Order(StringComparer.Ordinal).ToArray(), changed.Where(c => !c.EndsWith("written_by")).ToArray());
        Assert.Equal("1", after["/zz_future/a"]);
        Assert.Equal(before["/created_at"], after["/created_at"]);
    }

    // ---- twice, and recovery ----

    [GitFact]
    public async Task ASecondPushOnTheSameSummary_IsNotReady_AndAFreshPullThenAPush_IsNothingToPush()
    {
        await OneNewOneChanged();
        var (outcome, pusher, apply, scan) = await PullAndPush();
        Is<PushOutcome.Pushed>(outcome);

        Is<PushOutcome.NotReady>(await pusher.PushAsync(apply, scan));
        var (again, _, _, _) = await PullAndPush();

        Is<PushOutcome.NothingToPush>(again);
    }

    [GitTheory]
    [InlineData("finalize", "Finalize")]
    [InlineData("meta", "Meta")]
    [InlineData("ref", "Ref")]
    public async Task AFailureAfterThePush_IsFailedYes_AndTheNextPullRecovers_WithNothingPushedTwice(string step, string kind)
    {
        await OneNewOneChanged();

        var (outcome, _, _, _) = await PullAndPush(p => p.BeforeStep = (_, s) =>
        {
            if (s == step)
                throw new InvalidOperationException("injected");
        });

        var failed = Is<PushOutcome.Failed>(outcome);
        Assert.Equal(kind, failed.Kind);
        Assert.Equal(SyncPush.Published.Yes, failed.Published);
        var head = _rig.Remote.Head();

        var (again, _, _, _) = await PullAndPush();

        Is<PushOutcome.NothingToPush>(again);
        Assert.Equal(head, _rig.Remote.Head());   // nothing was pushed twice
        Assert.Equal(head, _rig.Pull.Last);
        Assert.Equal(head, LastRef);
        foreach (var id in new[] { A, B })
        {
            Assert.Equal(RemoteText(id), Row(id).BaseJson);
            Assert.Null(Row(id).ContentStamp);
            Assert.Null(Row(id).PushingStamps);
        }
    }

    [GitFact]
    public async Task ALostResponse_IsFailedUnknown_AndTheNextPullAdoptsTheRecord_AndMergesWhatAnotherMachineChanged()
    {
        _rig.Seed(Rec(A, content: "old"));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "changed text" });
        var before = _rig.Remote.Head();
        _rig.Pull.After = (c, r) => c.IsPush ? FaultingGitProcess.Failure(SyncGit.FailureKind.Network, "connection reset") : r;

        var (outcome, _, _, _) = await PullAndPush();

        var failed = Is<PushOutcome.Failed>(outcome);
        Assert.Equal("Network", failed.Kind);
        Assert.Equal(SyncPush.Published.Unknown, failed.Published);
        Assert.NotEqual(before, _rig.Remote.Head());   // the push did happen
        _rig.Pull.After = (_, r) => r;
        OtherMachine(dir =>
        {
            var file = Path.Combine(dir, SyncValidation.recordPath(A).Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"tags\": []", "\"tags\": [\n    \"other\"\n  ]"));
        });

        var (again, _, _, _) = await PullAndPush();

        Is<PushOutcome.NothingToPush>(again);
        var entry = _rig.Store.Get(Tier.Warm, ContentType.Memory, A)!;
        Assert.Equal("changed text", entry.Content);
        Assert.Contains("other", entry.Tags);
        Assert.Null(Row(A).ContentStamp);
        Assert.Contains("other", Row(A).BaseJson!);
    }

    [GitFact]
    public async Task ANonFastForward_ThenAPullAndAPush_PublishesThePendingChangesOnTopOfTheOtherMachine()
    {
        await OneNewOneChanged();
        string? moved = null;

        var (outcome, _, _, _) = await PullAndPush(p => p.BeforePush = () => moved = _rig.MoveRemote());

        Is<PushOutcome.NonFastForward>(outcome);
        Assert.Equal(moved, _rig.Remote.Head());
        Assert.NotNull(Row(A).ContentStamp);
        Assert.NotNull(Row(A).PushingStamps);
        Assert.NotEqual(moved, _rig.Pull.Last);

        var (second, _, _, _) = await PullAndPush();

        var pushed = Is<PushOutcome.Pushed>(second);
        Assert.Equal(_rig.Remote.Head(), pushed.Summary.Commit);
        Assert.Equal(moved, _rig.Remote.Git("rev-parse", "main~1").Trim());
        Assert.Equal([A, B], pushed.Summary.Written);
        Assert.Equal(_rig.Remote.Head(), _rig.Pull.Last);
    }

    [GitFact]
    public async Task ACancelAfterThePushCommandSucceeded_StillFinalizes()
    {
        await OneNewOneChanged();
        using var cts = new CancellationTokenSource();
        _rig.Pull.After = (c, r) =>
        {
            if (c.IsPush)
                cts.Cancel();
            return r;
        };

        var (outcome, _, _, _) = await PullAndPush(ct: cts.Token);

        Is<PushOutcome.Pushed>(outcome);
        Assert.Equal(_rig.Remote.Head(), _rig.Pull.Last);
        Assert.Equal(_rig.Remote.Head(), LastRef);
        Assert.Null(Row(A).PushingStamps);
    }

    [GitFact]
    public async Task ABlockedEntry_GetsNoFinalize_AndKeepsItsMarks_WhileTheOthersGoOut()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, content: "fine");
        _rig.AddMemory(Tier.Warm, C, content: new string('x', 262_144 + 1));
        _rig.AddMemory(Tier.Warm, B, content: "also fine");

        var (outcome, _, _, _) = await PullAndPush();

        var pushed = Is<PushOutcome.Pushed>(outcome);
        Assert.Equal([A, B], pushed.Summary.Written);
        Assert.Equal(new PushItem(C, "too-large"), Assert.Single(pushed.Summary.Blocked));
        var blocked = Row(C);
        Assert.Null(blocked.BaseJson);
        Assert.Equal("too-large", blocked.BlockedReason);
        Assert.NotNull(blocked.ContentStamp);
    }

    [GitFact]
    public async Task AMixedPush_ReportsEveryListExactly()
    {
        _rig.Seed(Rec(A, content: "old"), Rec(B, content: "old"));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "edited" });
        _rig.Edit(Tier.Warm, B, new UpdateEntryOpts { Content = "edited too" });
        _rig.AddMemory(Tier.Warm, C, content: new string('x', 262_144 + 1));
        _rig.AddMemory(Tier.Warm, "d4e5f6a7");
        _rig.AddMemory(Tier.Warm, "e5f6a7b8");

        var (outcome, _, _, _) = await PullAndPush(p => p.BeforePlanTransaction = () =>
        {
            _rig.Store.Update(Tier.Warm, ContentType.Memory, B, new UpdateEntryOpts { Content = "a later edit" });
        });

        var summary = Is<PushOutcome.Pushed>(outcome).Summary;
        Assert.Equal(2, summary.New);
        Assert.Equal(1, summary.Changed);
        Assert.Equal(0, summary.Unchanged);
        Assert.Equal([A, "d4e5f6a7", "e5f6a7b8"], summary.Written);
        Assert.Equal([new PushItem(C, "too-large")], summary.Blocked);
        Assert.Equal([new PushItem(B, "changed-since-scan")], summary.Deferred);
        Assert.Empty(summary.Skipped);
        Assert.Equal("sync: testbox +2 ~1 -0", _rig.Remote.Git("log", "-1", "--format=%s", "main").Trim());
    }

    [GitFact]
    public async Task AFailedFinalize_OfAnUnchangedOnlyPush_IsFailedNo()
    {
        _rig.Seed(Rec(A, content: "old"));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "different" });
        _rig.Now += 1000;
        _rig.Scan();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { ContentStamp = St(5) });

        var (outcome, _, _, _) = await PullAndPush(p => p.BeforeStep = (_, s) =>
        {
            if (s == "finalize")
                throw new InvalidOperationException("injected");
        });

        var failed = Is<PushOutcome.Failed>(outcome);
        Assert.Equal("Finalize", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
    }

    // ---- removals (milestone 1b-ii, D14) ----

    private bool RemoteHas(string id) =>
        _rig.Remote.Git("ls-tree", "--name-only", "main", "--", SyncValidation.recordPath(id)).Trim() != "";

    /// <summary>A and B synced, then A deleted here: the next pull lists A for the push.</summary>
    private async Task SyncedThenDeleted()
    {
        _rig.Seed(Rec(A), Rec(B, content: "stays"));
        await _rig.PullAsync();
        _rig.Store.Delete(Tier.Warm, ContentType.Memory, A);
    }

    [GitFact]
    public async Task FinalizeSetsForgotten_KeepingTheBase_ClearingPendingPushingAndRepush()
    {
        await SyncedThenDeleted();
        GitSyncRow? before = null;

        // the apply clears the pending stamps of a pushed deletion; the columns are set by hand after the plan
        var (outcome, pusher, apply, _) = await PullAndPush(p => p.BeforePush = () =>
        {
            before = Row(A) with
            {
                ContentStamp = St(20),
                StickyStamp = St(21),
                TierStamp = St(22),
                PushingStamps = SyncApply.PushingStamps.encode(new SyncMerge.GroupStamps(St(20), null, null)).Value,
                RepushGroups = "tier",
            };
            GitSyncRows.Write(_rig.Pull.Connection, before);
        });

        Assert.Equal([A], apply.PushDeletions);
        var pushed = Is<PushOutcome.Pushed>(outcome);
        Assert.Equal([A], pushed.Summary.Removed);
        Assert.Equal(1, pusher.TransactionCount);   // only the finalize: a removal needs no plan transaction
        Assert.False(RemoteHas(A));
        Assert.Equal(
            before! with
            {
                State = SyncMerge.RowState.Forgotten,
                ContentStamp = null,
                StickyStamp = null,
                TierStamp = null,
                PushingStamps = null,
                RepushGroups = null,
            },
            Row(A));
        Assert.NotNull(Row(A).BaseJson);
        Assert.Equal(_rig.Remote.Head(), _rig.Pull.Last);
        Assert.Equal(_rig.Remote.Head(), LastRef);

        // a scan right after stamps nothing and writes nothing for the forgotten row
        var dump = _rig.RowDump();
        _rig.Now += 1000;
        Assert.Empty(_rig.Scan().LocalDeletions);
        Assert.Equal(dump, _rig.RowDump());
    }

    [GitFact]
    public async Task FinalizeForgetsTheRow_EvenWhenTheMemoryExistsAgain_AndTheNextCycleRepublishesIt()
    {
        await SyncedThenDeleted();
        var text = Rec(A).Content;

        // the memory is re-created with the same id and text after the apply decided the deletion (an import keeps ids)
        var (outcome, _, _, _) = await PullAndPush(p => p.BeforePush = () => _rig.AddMemory(Tier.Warm, A, content: text));

        Assert.Equal([A], Is<PushOutcome.Pushed>(outcome).Summary.Removed);
        Assert.False(RemoteHas(A));
        Assert.Equal(SyncMerge.RowState.Forgotten, Row(A).State);
        Assert.NotNull(Row(A).BaseJson);
        Assert.Equal(_rig.Remote.Head(), _rig.Pull.Last);

        // the next scan reactivates the row: active, base cleared, the entry stamped as new
        _rig.Now += 1000;
        _rig.Scan();
        Assert.Equal(SyncMerge.RowState.Active, Row(A).State);
        Assert.Null(Row(A).BaseJson);
        Assert.NotNull(Row(A).ContentStamp);

        // the next pull keeps the memory, and the next push writes its record back
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        Assert.Empty(apply.Deleted);
        Assert.Equal(text, _rig.Store.Get(Tier.Warm, ContentType.Memory, A)!.Content);

        var pushed = Is<PushOutcome.Pushed>(await _rig.Push(apply, scan));

        Assert.Equal([A], pushed.Summary.Written);
        Assert.Empty(pushed.Summary.Removed);
        Assert.True(RemoteHas(A));
        Assert.Equal(text, JsonNode.Parse(RemoteText(A))!.AsObject()["content"]!.GetValue<string>());
        Assert.Equal(RemoteText(A), Row(A).BaseJson);
        Assert.Equal(_rig.Remote.Head(), _rig.Pull.Last);
    }

    [GitTheory]
    [InlineData("finalize")]
    [InlineData("forget")]
    public async Task KillAfterPushBeforeFinalize_NextApplyMakesTheRowForgotten_AndNoSecondCommitIsMade(string step)
    {
        await SyncedThenDeleted();
        _rig.AddMemory(Tier.Warm, C, content: "new here");   // a written entry, so the per-entry finalize step runs

        var (outcome, _, apply, _) = await PullAndPush(p => p.BeforeStep = (_, s) =>
        {
            if (s == step)
                throw new InvalidOperationException("killed");
        });

        var failed = Is<PushOutcome.Failed>(outcome);
        Assert.Equal(SyncPush.Published.Yes, failed.Published);
        var published = _rig.Remote.Head();
        Assert.False(RemoteHas(A));
        Assert.Equal(apply.Head, _rig.Pull.Last);                      // the finalize rolled back whole
        Assert.Equal(SyncMerge.RowState.Active, Row(A).State);
        Assert.Null(Row(A).PushingStamps);                             // a removal writes no pushing_stamps
        var rowA = Row(A);                                             // the row the finalize would have written over

        var (again, _, apply2, scan2) = await PullAndPush();

        Assert.Contains(A, scan2.LocalDeletions.Select(d => d.Id));
        Assert.Empty(apply2.PushDeletions);                            // the record is gone from R: nothing to remove
        Is<PushOutcome.NothingToPush>(again);
        Assert.Equal(published, _rig.Remote.Head());                   // no second commit
        Assert.Equal(published, _rig.Pull.Last);
        // the repaired row is the row a finalize would have written
        Assert.Equal(
            rowA with
            {
                State = SyncMerge.RowState.Forgotten,
                ContentStamp = null,
                StickyStamp = null,
                TierStamp = null,
                PushingStamps = null,
                RepushGroups = null,
            },
            Row(A));
        Assert.Equal(RemoteText(C), Row(C).BaseJson);
    }

    [GitFact]
    public async Task AnIncompletePull_IsNotReady_AndNothingChanges()
    {
        await OneNewOneChanged();
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        var rows = _rig.RowDump();

        var outcome = await _rig.Pusher().PushAsync(apply with { Complete = false }, scan);

        Is<PushOutcome.NotReady>(outcome);
        Assert.Equal(rows, _rig.RowDump());
    }
}
