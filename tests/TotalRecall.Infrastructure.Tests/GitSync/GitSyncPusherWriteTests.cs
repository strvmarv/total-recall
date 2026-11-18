using System.Security.Cryptography;
using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncPusherWriteTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";

    private readonly PushRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private string Clone => _rig.Paths.CloneDirectory;

    private static string BlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes])).ToLowerInvariant();
    }

    /// <summary>Pull, edit nothing more, prepare and plan, ready for <c>WriteAsync</c>.</summary>
    private async Task<(PlanResult.Planned Plan, GitSyncPusher Pusher, string Head)> ReadyAsync(
        Action<GitSyncPusher>? configure = null, CancellationToken ct = default)
    {
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        var pusher = _rig.Pusher();
        var prepared = Assert.IsType<PrepareResult.Ready>(await pusher.PrepareAsync(apply, scan, ct)).Value;
        configure?.Invoke(pusher);
        var plan = Assert.IsType<PlanResult.Planned>(pusher.Plan(prepared, ct));
        return (plan, pusher, apply.Head);
    }

    private static SyncGit.FailureKind KindOf(string name) => name switch
    {
        "Network" => SyncGit.FailureKind.Network,
        "TimedOut" => SyncGit.FailureKind.TimedOut,
        "OutputTooLarge" => SyncGit.FailureKind.OutputTooLarge,
        "Auth" => SyncGit.FailureKind.Auth,
        "RemoteNotFound" => SyncGit.FailureKind.RemoteNotFound,
        "RemoteBranchMissing" => SyncGit.FailureKind.RemoteBranchMissing,
        _ => SyncGit.FailureKind.Other,
    };

    private static PushOutcome.Failed Failed(WriteOutcome outcome) =>
        Assert.IsType<PushOutcome.Failed>(Assert.IsType<WriteOutcome.Stop>(outcome).Outcome);

    private void AssertCloneIsAt(string head)
    {
        Assert.Equal(head, _rig.CloneHead());
        Assert.Equal("", _rig.Status().Trim());
        Assert.Empty(Directory.GetFiles(Path.Combine(Clone, ".git"), "tr-push-*"));
    }

    private string RecordFile(string id) => Path.Combine(Clone, SyncValidation.recordPath(id).Replace('/', Path.DirectorySeparatorChar));

    private async Task OneNewOneChanged()
    {
        _rig.Seed(Rec(A, content: "old"));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "changed text" });
        _rig.AddMemory(Tier.Warm, B, content: "brand new");
    }

    // ---- the commit ----

    [GitFact]
    public async Task ANewAndAChangedEntry_AreOneCommit_OnTopOfL_WithTheRightFilesAndMessage()
    {
        await OneNewOneChanged();
        var (plan, pusher, head) = await ReadyAsync();

        var outcome = await pusher.WriteAsync(head, plan, default);

        var committed = Assert.IsType<WriteOutcome.Committed>(outcome);
        Assert.Equal(_rig.Remote.Head(), committed.Commit);
        var log = _rig.Remote.Git("log", "-1", "--format=%P|%an|%ae|%s", "main").Trim();
        Assert.Equal($"{head}|total-recall|noreply@total-recall.local|sync: testbox +1 ~1 -0", log);
        var changed = _rig.Remote.Git("diff-tree", "--no-commit-id", "--name-status", "-r", "main").Trim().Split('\n');
        Assert.Equal([$"M\t{SyncValidation.recordPath(A)}", $"A\t{SyncValidation.recordPath(B)}"], changed.Order(StringComparer.Ordinal).Reverse().ToArray());
        foreach (var entry in plan.Entries)
            Assert.Equal(BlobSha(entry.Bytes), _rig.Remote.Git("rev-parse", $"main:{SyncValidation.recordPath(entry.Id)}").Trim());
        Assert.Empty(Directory.GetFiles(Path.Combine(Clone, ".git"), "tr-push-*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(Clone, "memories"), "*.tmp", SearchOption.AllDirectories));
    }

    [GitFact]
    public async Task ATrackedIdentAttribute_DoesNotChangeTheCommittedBytes()
    {
        _rig.SeedWith(pull => pull.PutText("memories/.gitattributes", "* ident\n* text eol=crlf\n"));
        _rig.AddMemory(Tier.Warm, A, content: "keywords $Id: x$ and crlf\r\nline\r\nend");
        var (plan, pusher, head) = await ReadyAsync();

        Assert.IsType<WriteOutcome.Committed>(await pusher.WriteAsync(head, plan, default));

        var bytes = plan.Entries.Single().Bytes;
        Assert.Equal(BlobSha(bytes), _rig.Remote.Git("rev-parse", $"main:{SyncValidation.recordPath(A)}").Trim());
    }

    [GitFact]
    public async Task CrlfAndNulText_IsCommittedByteForByte()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, content: "a\r\nb\u0000c\rd");
        var (plan, pusher, head) = await ReadyAsync();

        Assert.IsType<WriteOutcome.Committed>(await pusher.WriteAsync(head, plan, default));

        Assert.Equal(BlobSha(plan.Entries.Single().Bytes), _rig.Remote.Git("rev-parse", $"main:{SyncValidation.recordPath(A)}").Trim());
    }

    [GitFact]
    public async Task AGitignoreCoveringMemories_DoesNotStopANewRecord()
    {
        _rig.SeedWith(pull => pull.PutText(".gitignore", "memories/\n*.json\n"));
        _rig.AddMemory(Tier.Warm, A);
        var (plan, pusher, head) = await ReadyAsync();

        Assert.IsType<WriteOutcome.Committed>(await pusher.WriteAsync(head, plan, default));

        Assert.Equal(SyncValidation.recordPath(A), _rig.Remote.Git("diff-tree", "--no-commit-id", "--name-only", "-r", "main").Trim());
    }

    [GitFact]
    public async Task TwoHundredFiftyNewEntries_AreStagedInThreeBatches()
    {
        _rig.Seed();
        for (var i = 0; i < 250; i++)
            _rig.AddMemory(Tier.Warm, $"{i + 0x10000000:x8}", content: "entry " + i);
        var (plan, pusher, head) = await ReadyAsync();
        var before = _rig.Pull.Recorder.Calls.Count;

        Assert.IsType<WriteOutcome.Committed>(await pusher.WriteAsync(head, plan, default));

        var batches = _rig.Pull.Recorder.Calls.Skip(before)
            .Where(c => c.Command.IsStagePaths)
            .Select(c => c.Argv.SkipWhile(a => a != "--").Skip(1).Count())
            .ToArray();
        Assert.Equal([100, 100, 50], batches);
        Assert.Equal(250, _rig.Remote.Git("diff-tree", "--no-commit-id", "--name-only", "-r", "main").Trim().Split('\n').Length);
    }

    [ShFact]
    public async Task ARecordWithTheExecutableMode_KeepsItAfterAPatch()
    {
        var bytes = RecordWriter.Write(Rec(A, content: "old")).Bytes!;
        _rig.SeedWith(pull => pull.PutRaw(SyncValidation.recordPath(A), bytes, "100755"));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "patched" });
        var (plan, pusher, head) = await ReadyAsync();

        Assert.IsType<WriteOutcome.Committed>(await pusher.WriteAsync(head, plan, default));

        Assert.StartsWith("100755", _rig.Remote.Git("ls-tree", "main", "--", SyncValidation.recordPath(A)));
    }

    // ---- nothing to write ----

    [GitFact]
    public async Task AnEmptyWriteSet_IsNothingToPush_WithNoStageCommitOrPush()
    {
        _rig.Seed(Rec(A));
        var (plan, pusher, head) = await ReadyAsync();
        var before = _rig.Pull.Recorder.Calls.Count;

        var outcome = await pusher.WriteAsync(head, plan, default);

        var nothing = Assert.IsType<PushOutcome.NothingToPush>(Assert.IsType<WriteOutcome.Stop>(outcome).Outcome);
        Assert.Null(nothing.Summary.Commit);
        Assert.Equal(before, _rig.Pull.Recorder.Calls.Count);
    }

    // ---- the staged set ----

    [GitFact]
    public async Task AWrittenFileRemovedBeforeTheStage_IsFailed_AndTheCloneIsReset()
    {
        await OneNewOneChanged();
        foreach (var removed in new[] { A, B })
        {
            var (plan, pusher, head) = await ReadyAsync(p => p.BeforeStep = (_, s) =>
            {
                if (s == "stage")
                    File.Delete(RecordFile(removed));
            });

            var failed = Failed(await pusher.WriteAsync(head, plan, default));

            // a tracked file named in the list is staged as a deletion and the set check refuses it; a new one cannot be added
            Assert.Equal(removed == A ? "StagedSet" : "Stage", failed.Kind);
            Assert.Equal(SyncPush.Published.No, failed.Published);
            AssertCloneIsAt(head);
            Assert.False(File.Exists(RecordFile(B)));
            Assert.Equal(head, _rig.Remote.Head());
        }
    }

    [GitTheory]
    [InlineData("write", "Write")]
    [InlineData("stage", "Stage")]
    [InlineData("commit", "Commit")]
    public async Task AFaultAtAStep_IsFailed_TheCloneIsReset_AndNothingIsPublished(string step, string kind)
    {
        await OneNewOneChanged();
        var (plan, pusher, head) = await ReadyAsync(p => p.BeforeStep = (_, s) =>
        {
            if (s == step)
                throw new InvalidOperationException("injected");
        });
        var rows = _rig.RowDump();

        var failed = Failed(await pusher.WriteAsync(head, plan, default));

        Assert.Equal(kind, failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        AssertCloneIsAt(head);
        Assert.False(File.Exists(RecordFile(B)));
        Assert.Equal(head, _rig.Remote.Head());
        Assert.Equal(rows, _rig.RowDump());   // only the plan's notes are there
    }

    [GitFact]
    public async Task AFailingCommit_IsFailedCommit_AndTheCloneIsReset()
    {
        await OneNewOneChanged();
        var (plan, pusher, head) = await ReadyAsync();
        _rig.Pull.Fault = c => c.IsCommit ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "canned commit failure") : null;

        var failed = Failed(await pusher.WriteAsync(head, plan, default));

        Assert.Equal("Commit", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        _rig.Pull.Fault = _ => null;
        AssertCloneIsAt(head);
    }

    // ---- the push ----

    [GitFact]
    public async Task ARemoteThatMovedOn_IsNonFastForward_NothingIsOverwritten_AndTheNextPrepareResetsTheClone()
    {
        await OneNewOneChanged();
        var (plan, pusher, head) = await ReadyAsync();
        string? moved = null;
        pusher.BeforePush = () => moved = _rig.MoveRemote();

        var outcome = await pusher.WriteAsync(head, plan, default);

        var stop = Assert.IsType<PushOutcome.NonFastForward>(Assert.IsType<WriteOutcome.Stop>(outcome).Outcome);
        Assert.Equal(moved, _rig.Remote.Head());
        Assert.NotNull(stop.Summary.Commit);
        Assert.Equal(2, stop.Summary.Written.Count);
        Assert.NotEqual(head, _rig.CloneHead());   // this call does not reset the clone
        Assert.NotNull(GitSyncRows.ReadAll(_rig.Pull.Connection)[A].PushingStamps);
        Assert.NotNull(GitSyncRows.ReadAll(_rig.Pull.Connection)[A].ContentStamp);

        var again = await _rig.Pusher().PrepareAsync(PushRig.Summary(head), ScanSummary.Empty, default);
        Assert.IsType<PrepareResult.Ready>(again);
        Assert.Equal(head, _rig.CloneHead());
    }

    [GitTheory]
    [InlineData("Network", "could not resolve host", "Unknown")]
    [InlineData("TimedOut", "", "Unknown")]
    [InlineData("OutputTooLarge", "", "Unknown")]
    [InlineData("Auth", "authentication failed", "No")]
    [InlineData("RemoteNotFound", "repository not found", "No")]
    [InlineData("RemoteBranchMissing", "branch gone", "No")]
    [InlineData("Other", "something odd", "Unknown")]
    [InlineData("Other", " ! [remote rejected] main -> main (pre-receive hook declined)", "No")]
    public async Task APushFailure_SaysWhetherTheCommitMayHaveReachedTheRemote(string kindName, string stderr, string expectedName)
    {
        var kind = KindOf(kindName);
        var expected = expectedName switch { "Unknown" => SyncPush.Published.Unknown, "No" => SyncPush.Published.No, _ => SyncPush.Published.Yes };
        await OneNewOneChanged();
        var (plan, pusher, head) = await ReadyAsync();
        _rig.Pull.Fault = c => c.IsPush ? FaultingGitProcess.Failure(kind, stderr) : null;

        var failed = Failed(await pusher.WriteAsync(head, plan, default));

        Assert.Equal(kind.ToString(), failed.Kind);
        Assert.Equal(expected, failed.Published);
        Assert.Equal(head, _rig.Remote.Head());
    }

    // ---- cancellation ----

    [GitFact]
    public async Task ACancelThatArrivedInsideThePlan_IsCancelled_BeforeAnyFileIsWritten()
    {
        await OneNewOneChanged();
        var (plan, pusher, head) = await ReadyAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var failed = Failed(await pusher.WriteAsync(head, plan, cts.Token));

        Assert.Equal("Cancelled", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        AssertCloneIsAt(head);
        Assert.Empty(GitSyncIssues.ReadOpen(_rig.Pull.Connection, GitSyncIssues.ApplyFailed));
    }

    [GitTheory]
    [InlineData("write")]
    [InlineData("stage")]
    [InlineData("commit")]
    [InlineData("push")]
    public async Task ACancelBetweenSteps_IsCancelled_TheCloneIsReset_AndTheRemoteIsUnmoved(string step)
    {
        await OneNewOneChanged();
        using var cts = new CancellationTokenSource();
        var (plan, pusher, head) = await ReadyAsync(p =>
        {
            p.BeforeStep = (_, s) =>
            {
                if (s == step)
                    cts.Cancel();
            };
            if (step == "push")
                p.BeforePush = cts.Cancel;
        });

        var failed = Failed(await pusher.WriteAsync(head, plan, cts.Token));

        Assert.Equal("Cancelled", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        AssertCloneIsAt(head);
        Assert.Equal(head, _rig.Remote.Head());
    }

    [GitFact]
    public async Task ACancelWhileThePushCommandRuns_IsCancelled_AndMayHavePublished()
    {
        await OneNewOneChanged();
        var (plan, pusher, head) = await ReadyAsync();
        using var cts = new CancellationTokenSource();
        _rig.Pull.Fault = c =>
        {
            if (!c.IsPush)
                return null;
            cts.Cancel();
            return FaultingGitProcess.Failure(SyncGit.FailureKind.Cancelled, "");
        };

        var failed = Failed(await pusher.WriteAsync(head, plan, cts.Token));

        Assert.Equal("Cancelled", failed.Kind);
        Assert.Equal(SyncPush.Published.Unknown, failed.Published);
    }
}
