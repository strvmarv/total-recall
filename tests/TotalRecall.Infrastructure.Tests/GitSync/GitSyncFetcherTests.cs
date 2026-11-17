using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncFetcherTests : IDisposable
{
    private readonly PullRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>A joined machine, a manifest and one commit on the remote, cloned: the state right after <c>join</c>.</summary>
    private string Joined()
    {
        _rig.Join();
        _rig.PutManifest();
        var first = _rig.Commit("manifest");
        _rig.Clone();
        return first;
    }

    private static T Is<T>(FetchOutcome outcome) where T : FetchOutcome => Assert.IsType<T>(outcome);

    private Task<FetchOutcome> Fetch() => _rig.Fetcher().FetchAsync();

    // ---- decision 1, steps in order ----

    [GitFact]
    public async Task AnUnjoinedMachine_IsRefusedBeforeAnyNetworkCall()
    {
        _rig.PutManifest();
        _rig.Commit();
        _rig.Clone(); // never joined: no identity keys

        var refused = Is<FetchOutcome.Refused>(await Fetch());

        Assert.Contains("not joined", refused.Reason);
        Assert.Equal(0, _rig.Count(c => c.IsLsRemote || c.IsFetch));
    }

    [GitFact]
    public async Task AMovedDataDirectory_AndAChangedRemote_AreRefusedBeforeAnyNetworkCall()
    {
        Joined();
        _rig.Store.SetMeta(SyncIdentity.MetaKeys.Host, "0123456789abcdef0123456789abcdef");
        Assert.Contains("moved or was copied", Is<FetchOutcome.Refused>(await Fetch()).Reason);

        _rig.Join();
        _rig.NewGuard("git@github.com:other/other.git");
        Assert.Contains("differs", Is<FetchOutcome.Refused>(await Fetch()).Reason);
        Assert.Equal(0, _rig.Count(c => c.IsLsRemote || c.IsFetch));
    }

    [GitFact]
    public async Task AnUnreachableRemote_IsFailed_WithTheGitKind_AndNothingChanged()
    {
        Joined();
        _rig.Fault = c => c.IsLsRemote ? FaultingGitProcess.Failure(SyncGit.FailureKind.Network, "could not resolve host") : null;
        var headBefore = _rig.CloneRef("HEAD");

        var failed = Is<FetchOutcome.Failed>(await Fetch());

        Assert.Equal("Network", failed.Kind);
        Assert.Contains("could not resolve host", failed.Message);
        Assert.Equal(headBefore, _rig.CloneRef("HEAD"));
        Assert.Null(_rig.Last);
    }

    [GitFact]
    public async Task ARemoteWithNoSuchBranch_IsRefused()
    {
        Joined();

        var refused = Is<FetchOutcome.Refused>(await _rig.Fetcher("nope").FetchAsync());

        Assert.Contains("no branch 'nope'", refused.Reason);
    }

    [GitFact]
    public async Task AfterAPush_Fetched_HeadIsTheTrackingRef_ClonePointsAtIt_AndTheGuardRanBeforeTheReset()
    {
        var first = Joined();
        _rig.PutText("memories/3f/x.json", "{}");
        var second = _rig.Commit("second");

        var fetched = Is<FetchOutcome.Fetched>(await Fetch());

        Assert.Equal(second, fetched.Result.Head);
        Assert.Null(fetched.Result.Last);
        Assert.False(fetched.Result.Proven); // no L
        Assert.False(fetched.Result.NothingNew);
        Assert.Equal(second, _rig.CloneRef("HEAD"));
        Assert.Equal(second, _rig.CloneRef("refs/remotes/origin/main"));
        Assert.NotEqual(first, second);
        var commands = _rig.Recorder.Calls.Select(c => c.Command).ToList();
        Assert.True(commands.FindIndex(c => c.IsFetch) < commands.FindIndex(c => c.IsResetHard));
        Assert.True(commands.FindLastIndex(c => c.IsLsTreeRoot) < commands.FindIndex(c => c.IsResetHard), "the manifest was read before the reset");
    }

    [GitTheory]
    [InlineData("newer")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("other-repo")]
    public async Task AManifestTheGuardRefuses_LeavesTheCloneUnreset(string kind)
    {
        Joined();
        var before = _rig.CloneRef("HEAD");
        switch (kind)
        {
            case "newer": _rig.PutManifest(PullRig.ManifestBytes(format: "total-recall/v9")); break;
            case "missing": _rig.Remove("manifest.json"); _rig.PutText("memories/3f/x.json", "{}"); break;
            case "invalid": _rig.PutText("manifest.json", "not json"); break;
            default: _rig.PutManifest(PullRig.ManifestBytes("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")); break;
        }
        _rig.Commit("change");

        Is<FetchOutcome.Refused>(await Fetch());

        Assert.Equal(before, _rig.CloneRef("HEAD")); // not reset
        Assert.Equal(0, _rig.Count(c => c.IsResetHard));
    }

    // ---- NothingNew ----

    [GitFact]
    public async Task WhenTheHeadEqualsL_AndTheCloneHoldsIt_NothingIsFetchedOrReset_AndTheGuardRunsAtR()
    {
        var first = Joined();
        _rig.SetLast(first);
        _rig.CloneGit("update-ref", "refs/total-recall/last-synced", first);

        var outcome = Is<FetchOutcome.NothingNew>(await Fetch());

        Assert.Equal(first, outcome.Result.Head);
        Assert.Equal(first, outcome.Result.Last);
        Assert.True(outcome.Result.Proven);
        Assert.True(outcome.Result.NothingNew);
        Assert.Equal(1, _rig.Count(c => c.IsLsRemote));
        Assert.Equal(0, _rig.Count(c => c.IsFetch));
        Assert.Equal(0, _rig.Count(c => c.IsResetHard));
        Assert.True(_rig.Count(c => c.IsLsTreeRoot) >= 1, "the manifest was read from the clone at R");
    }

    [GitFact]
    public async Task WhenTheHeadEqualsL_ACopiedDataDirectory_IsRefusedToo()
    {
        var first = Joined();
        _rig.SetLast(first);
        _rig.Store.SetMeta(SyncIdentity.MetaKeys.Host, "0123456789abcdef0123456789abcdef");

        Is<FetchOutcome.Refused>(await Fetch());
    }

    [GitFact]
    public async Task WhenTheHeadEqualsL_ButTheCloneLacksIt_ItFallsThroughToTheFetch_AndIsUnproven()
    {
        Joined(); // the clone holds the first commit only
        _rig.PutText("memories/3f/x.json", "{}");
        var second = _rig.Commit("second"); // pushed, not fetched
        _rig.SetLast(second);                // a restored database: L is a commit the clone does not hold

        var fetched = Is<FetchOutcome.Fetched>(await Fetch());

        Assert.Equal(second, fetched.Result.Head);
        Assert.False(fetched.Result.NothingNew);
        Assert.False(fetched.Result.Proven); // no protective ref
        Assert.True(_rig.Count(c => c.IsFetch) >= 1);
    }

    // ---- decision 2, the proof state ----

    private async Task<FetchResult> ProvenAfterPush(Action<string>? prepare = null)
    {
        var first = Joined();
        _rig.SetLast(first);
        _rig.CloneGit("update-ref", "refs/total-recall/last-synced", first);
        prepare?.Invoke(first);
        _rig.PutText("memories/3f/x.json", "{}");
        _rig.Commit("second");
        return Is<FetchOutcome.Fetched>(await Fetch()).Result;
    }

    [GitFact]
    public async Task L_AnAncestorOfR_WithTheRefAtL_IsProven()
    {
        var result = await ProvenAfterPush();

        Assert.True(result.Proven);
        Assert.Equal(result.Last, _rig.CloneRef("refs/total-recall/last-synced"));
    }

    [GitFact]
    public async Task NoL_IsUnproven()
    {
        var first = Joined();
        _rig.CloneGit("update-ref", "refs/total-recall/last-synced", first);
        _rig.PutText("memories/3f/x.json", "{}");
        _rig.Commit("second");

        Assert.False(Is<FetchOutcome.Fetched>(await Fetch()).Result.Proven);
    }

    [GitFact]
    public async Task AnLThatIsNotAnAncestor_AfterRewrittenHistory_IsUnproven()
    {
        var first = Joined();
        _rig.SetLast(first);
        _rig.CloneGit("update-ref", "refs/total-recall/last-synced", first);
        _rig.PutText("memories/3f/y.json", "{}");
        var tree = _rig.Source.Git("write-tree").Trim();
        var root = _rig.Source.Git("commit-tree", tree, "-m", "rewritten").Trim(); // a new root: the first commit is not its ancestor
        _rig.Source.Git("update-ref", "refs/heads/main", root);

        Assert.False(Is<FetchOutcome.Fetched>(await Fetch()).Result.Proven);
    }

    [GitFact]
    public async Task AMissingRef_IsUnproven()
    {
        Assert.False((await ProvenAfterPushWithoutRef()).Proven);
    }

    [GitFact]
    public async Task ARefElsewhere_IsUnproven()
    {
        var first = Joined();
        _rig.PutText("memories/3f/x.json", "{}");
        var second = _rig.Commit("second");
        Is<FetchOutcome.Fetched>(await Fetch()); // the clone holds both commits
        _rig.SetLast(first);
        _rig.CloneGit("update-ref", "refs/total-recall/last-synced", second); // a descendant of L: not an ancestor, so not a crash leftover

        var result = Is<FetchOutcome.Fetched>(await Fetch()).Result;

        Assert.Equal(first, result.Last);
        Assert.False(result.Proven);
        Assert.Equal(second, _rig.CloneRef("refs/total-recall/last-synced")); // not touched
    }

    private async Task<FetchResult> ProvenAfterPushWithoutRef()
    {
        var first = Joined();
        _rig.SetLast(first);
        _rig.PutText("memories/3f/x.json", "{}");
        _rig.Commit("second");
        return Is<FetchOutcome.Fetched>(await Fetch()).Result;
    }

    [GitFact]
    public async Task ARefThatIsAStrictAncestorOfL_IsRepairedInPlace_AndProven_AlsoWhenNothingIsNew()
    {
        var first = Joined();
        _rig.PutText("memories/3f/x.json", "{}");
        var second = _rig.Commit("second");
        Is<FetchOutcome.Fetched>(await Fetch()); // the clone now holds both commits
        _rig.SetLast(second);
        _rig.CloneGit("update-ref", "refs/total-recall/last-synced", first); // a crash between the two writes of a pull

        var outcome = Is<FetchOutcome.NothingNew>(await Fetch());

        Assert.True(outcome.Result.Proven);
        Assert.Equal(second, _rig.CloneRef("refs/total-recall/last-synced"));
    }

    [GitFact]
    public async Task AnUnknownAncestryAnswer_IsUnproven_NotAFailure()
    {
        var first = Joined();
        _rig.SetLast(first);
        _rig.CloneGit("update-ref", "refs/total-recall/last-synced", first);
        _rig.PutText("memories/3f/x.json", "{}");
        _rig.Commit("second");
        _rig.Fault = c => c.IsIsAncestor ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "fatal: not a valid commit", exit: 128) : null;

        Assert.False(Is<FetchOutcome.Fetched>(await Fetch()).Result.Proven);
    }

    [GitFact]
    public async Task AGitCallThatGivesNoAnswer_IsFailed_NotUnproven()
    {
        var first = Joined();
        _rig.SetLast(first);
        _rig.PutText("memories/3f/x.json", "{}");
        _rig.Commit("second");
        _rig.Fault = c => c.IsIsAncestor ? new GitResult(null, [], "timed out", SyncGit.FailureKind.TimedOut, TimeSpan.Zero) : null;

        var failed = Is<FetchOutcome.Failed>(await Fetch());

        Assert.Equal("TimedOut", failed.Kind);
        Assert.Equal(0, _rig.Count(c => c.IsResetHard));
    }

    [GitFact]
    public async Task AFailedReset_IsFailed()
    {
        Joined();
        _rig.PutText("memories/3f/x.json", "{}");
        _rig.Commit("second");
        _rig.Fault = c => c.IsResetHard ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "reset failed") : null;

        Is<FetchOutcome.Failed>(await Fetch());
    }

    [GitFact]
    public async Task L_IsReadOnlyWhenItIsFullLowercaseHex()
    {
        Joined();
        foreach (var bad in new[] { "", "abc", new string('A', 40), new string('g', 40), new string('a', 41) })
        {
            _rig.Store.SetMeta(SyncIdentity.MetaKeys.LastSha, bad);
            Assert.Null(Is<FetchOutcome.Fetched>(await Fetch()).Result.Last);
        }
    }

    [GitFact]
    public async Task ACancelledCall_IsFailedCancelled_AndNothingChanged()
    {
        Joined();
        var before = _rig.CloneRef("HEAD");
        _rig.Fault = c => c.IsFetch ? FaultingGitProcess.Failure(SyncGit.FailureKind.Cancelled, "cancelled") : null;

        var failed = Is<FetchOutcome.Failed>(await Fetch());

        Assert.Equal("Cancelled", failed.Kind);
        Assert.Equal(before, _rig.CloneRef("HEAD"));
    }
}
