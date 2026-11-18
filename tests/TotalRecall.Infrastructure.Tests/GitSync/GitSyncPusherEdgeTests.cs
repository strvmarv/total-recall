using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>The spec's remaining tests: shapes a real pull never produces (deletions), bulk finalization, and a left-over commit after a failed push.</summary>
public sealed class GitSyncPusherEdgeTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";
    private const string C = "c3d4e5f6";

    private readonly PushRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [GitFact]
    public async Task ALocalDeletion_AMissingRecord_AndANotReconciledRow_WriteAndRemoveNothing_WhileTheOthersGoOut()
    {
        // a real pull that holds a deletion is incomplete (1b-ii), so the completed summary is built by hand
        var head = _rig.Seed(Rec(A, content: "kept in the repository"), Rec(B, content: "old"));
        await _rig.PullAsync();
        _rig.Store.Delete(Tier.Warm, ContentType.Memory, A);               // a local deletion
        _rig.Edit(Tier.Warm, B, new UpdateEntryOpts { Content = "edited" });
        _rig.AddMemory(Tier.Warm, C, content: "new here");
        _rig.Now += 1000;
        var scan = _rig.Scan();
        Assert.Contains(scan.LocalDeletions, d => d.Id == A);
        // a row with a base whose record is gone from the tree, and a new row whose record is accepted
        _rig.Pull.CloneGit("rm", "-q", SyncValidation.recordPath(B));
        _rig.Pull.CloneGit("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "remote deletion");
        var removedHead = _rig.CloneHead()!;
        _rig.Pull.SetLast(removedHead);
        var pusher = _rig.Pusher();

        var outcome = await pusher.PushAsync(PushRig.Summary(removedHead), scan);

        var pushed = Assert.IsType<PushOutcome.Pushed>(outcome);
        Assert.Equal([C], pushed.Summary.Written);
        Assert.Equal(new PushItem(B, "record-missing"), Assert.Single(pushed.Summary.Skipped));
        Assert.DoesNotContain(A, pushed.Summary.Written);
        var changed = _rig.Remote.Git("diff-tree", "--no-commit-id", "--name-status", "-r", removedHead, "main").Trim();
        Assert.Equal($"A\t{SyncValidation.recordPath(C)}", changed);   // nothing of A's is touched or removed by the push
        Assert.Equal("sync: testbox +1 ~0 -0", _rig.Remote.Git("log", "-1", "--format=%s", "main").Trim());
        Assert.Equal(head, _rig.Remote.Git("rev-parse", "main~2").Trim());   // only the remote deletion and this push are above the seed
    }

    [GitFact]
    public async Task ANotReconciledRow_IsListed_AndNotPushed()
    {
        var head = _rig.Seed(Rec(A, content: "in the repository"));
        _rig.Pull.SetLast(head);
        _rig.AddMemory(Tier.Warm, A, content: "a local twin that was never reconciled");
        _rig.AddMemory(Tier.Warm, B);
        _rig.Now += 1000;
        var scan = _rig.Scan();

        var outcome = await _rig.Pusher().PushAsync(PushRig.Summary(head), scan);

        var pushed = Assert.IsType<PushOutcome.Pushed>(outcome);
        Assert.Equal([B], pushed.Summary.Written);
        Assert.Equal(new PushItem(A, "not-reconciled"), Assert.Single(pushed.Summary.Skipped));
    }

    [GitFact]
    public async Task TwoHundredUnchangedEntries_AreFinalizedInOneTransaction()
    {
        var ids = Enumerable.Range(0, 200).Select(i => $"{i + 0x20000000:x8}").ToArray();
        _rig.Seed(ids.Select(id => Rec(id, content: "old")).ToArray());
        await _rig.PullAsync();
        foreach (var id in ids)
            _rig.Edit(Tier.Warm, id, new UpdateEntryOpts { Content = "edited " + id });
        _rig.Now += 1000;
        _rig.Scan();
        foreach (var (id, row) in GitSyncRows.ReadAll(_rig.Pull.Connection))
            GitSyncRows.Write(_rig.Pull.Connection, row with { ContentStamp = St(5) });   // stale: nothing to write
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        var pusher = _rig.Pusher();

        var outcome = await pusher.PushAsync(apply, scan);

        var nothing = Assert.IsType<PushOutcome.NothingToPush>(outcome);
        Assert.Equal(200, nothing.Summary.Unchanged);
        Assert.Equal(2, pusher.TransactionCount);   // the plan transaction and one finalize transaction
    }

    [GitFact]
    public async Task ALeftOverCommitAfterAFailedPush_IsNeverPublished_AndThePendingChangesAreSentOnTheNextTry()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        _rig.Pull.Fault = c => c.IsPush ? FaultingGitProcess.Failure(SyncGit.FailureKind.Auth, "authentication failed") : null;
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        var head = apply.Head;
        var first = await _rig.Pusher().PushAsync(apply, scan);
        Assert.IsType<PushOutcome.Failed>(first);
        var leftOver = _rig.CloneHead();
        Assert.NotEqual(head, leftOver);   // the unpublished commit is in the clone
        Assert.Equal(head, _rig.Remote.Head());
        _rig.Pull.Fault = _ => null;

        _rig.Now += 1000;
        var (apply2, scan2) = await _rig.PullAsync();
        var second = await _rig.Pusher().PushAsync(apply2, scan2);

        var pushed = Assert.IsType<PushOutcome.Pushed>(second);
        Assert.Equal(1, pushed.Summary.New);
        Assert.Equal(head, _rig.Remote.Git("rev-parse", "main~1").Trim());
        Assert.Equal("1", _rig.Remote.Git("rev-list", "--count", $"{head}..main").Trim());
        Assert.Equal(SyncValidation.recordPath(A), _rig.Remote.Git("diff-tree", "--no-commit-id", "--name-only", "-r", "main").Trim());
    }
}
