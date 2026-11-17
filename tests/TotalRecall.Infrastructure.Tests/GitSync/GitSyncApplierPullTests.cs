using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncApplierPullTests : IDisposable
{
    private const string A = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string B = "3f9c2a7e-0000-4000-8000-0000000000b2";
    private const string C = "3f9c2a7e-0000-4000-8000-0000000000c3";
    private const string Linked = "3f9d2a7e-0000-4000-8000-000000000000";

    private readonly PullRig _rig = new();
    private readonly CountingEmbedder _embedder = new();
    private readonly GitSyncApplier _applier;
    private long _now = 1_780_000_000_000L;

    public GitSyncApplierPullTests()
    {
        _applier = new GitSyncApplier(_rig.Client, _rig.Store, _embedder, new ApplyOptions(0), () => _now);
        _rig.Join();
        _rig.PutManifest();
        _rig.Commit("manifest");
        _rig.Clone();
    }

    public void Dispose() => _rig.Dispose();

    private static T Is<T>(object o) => Assert.IsType<T>(o);

    private ScanSummary Scan() => Is<ScanOutcome.Done>(new GitSyncScanner(_rig.Connection, () => _now).Scan()).Summary;

    private async Task<(FetchResult Fetch, ApplyOutcome Outcome)> PullWith(ScanSummary? scan = null, CancellationToken ct = default)
    {
        var fetch = await _rig.Fetcher().FetchAsync(ct);
        var result = fetch switch
        {
            FetchOutcome.Fetched f => f.Result,
            FetchOutcome.NothingNew n => n.Result,
            _ => throw new Xunit.Sdk.XunitException("the fetch did not succeed: " + fetch),
        };
        return (result, await _applier.ApplyAsync(result, scan ?? Scan(), ct));
    }

    private async Task<ApplySummary> Pull(ScanSummary? scan = null) => Is<ApplyOutcome.Done>((await PullWith(scan)).Outcome).Summary;

    private static SyncRecordModule.SyncRecord Rec(string id, string content = "Use bun, not npm.", int minute = 10, Tier? tier = null, bool sticky = false) =>
        ApplyRig.Rec(id, content, tier, sticky, minute: minute);

    /// <summary>Reads of record blobs so far (the manifest reads of the guard are not counted).</summary>
    private int RecordReads()
    {
        var manifest = _rig.Source.Git("rev-parse", "HEAD:manifest.json").Trim();
        return _rig.Recorder.Calls.Count(c => c.Command is SyncGit.GitCommand.CatBlob cb && cb.sha != manifest);
    }

    private string BlobOf(string path) => _rig.Source.Git("rev-parse", "HEAD:" + path).Trim();

    private string? Meta(string key) => _rig.Store.GetMeta(key);

    private int Rows() => GitSyncRows.ReadAll(_rig.Connection).Count;

    private List<IssueRow> Issues(string kind) => GitSyncIssues.ReadOpen(_rig.Connection, kind).Values.OrderBy(r => r.EntryId, StringComparer.Ordinal).ToList();

    private string Content(string id)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (_rig.Store.Get(tier, ContentType.Memory, id) is { } e)
                return e.Content;
        throw new Xunit.Sdk.XunitException("no such entry " + id);
    }

    private bool Has(string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(t => _rig.Store.Get(t, ContentType.Memory, id) is not null);

    private string PushRecords(params SyncRecordModule.SyncRecord[] records)
    {
        foreach (var r in records)
            _rig.PutRecord(r);
        return _rig.Commit("records");
    }

    // ---- the happy path ----

    [GitFact]
    public async Task ANewRecord_IsInserted_AndLAdvances_MetaFirst_ThenTheRef()
    {
        var head = PushRecords(Rec(A), Rec(B, "Other."));

        var summary = await Pull();

        Assert.Equal(2, summary.Inserted);
        Assert.True(summary.Complete);
        Assert.Equal(head, summary.Head);
        Assert.Equal(head, Meta(SyncIdentity.MetaKeys.LastSha));
        Assert.Equal(head, _rig.CloneRef("refs/total-recall/last-synced"));
        Assert.Equal("Use bun, not npm.", Content(A));
        Assert.Equal("Other.", Content(B));
    }

    [GitFact]
    public async Task AnEditedRecord_IsUpdated_AndASecondPullWithNothingNew_ReadsNothing_AndChangesNothing()
    {
        PushRecords(Rec(A));
        await Pull();
        PushRecords(Rec(A, "Edited text.", minute: 20));

        var summary = await Pull();

        Assert.Equal(1, summary.Updated);
        Assert.Equal("Edited text.", Content(A));

        var reads = RecordReads();
        var again = await Pull();
        Assert.Equal(0, again.Inserted + again.Updated);
        Assert.True(again.Complete);
        Assert.Equal(reads, RecordReads()); // a proven NothingNew reads no unchanged record
    }

    [GitFact]
    public async Task AnUnprovenNothingNew_ReadsEveryRecord_AndWritesTheRefAgain()
    {
        var head = PushRecords(Rec(A), Rec(B, "Other."));
        await Pull();
        _rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var reads = RecordReads();

        var summary = await Pull();

        Assert.False(summary.Proven);
        Assert.Equal(reads + 2, RecordReads());
        Assert.Equal(head, _rig.CloneRef("refs/total-recall/last-synced"));
    }

    [GitFact]
    public async Task AnEmptyVisitSet_IsComplete_AndLAdvances()
    {
        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Equal(0, summary.Inserted);
        Assert.Equal(_rig.Head, Meta(SyncIdentity.MetaKeys.LastSha));
    }

    [GitFact]
    public async Task ManyRecords_AreAppliedInOrdinalIdOrder()
    {
        var ids = Enumerable.Range(0, 12).Select(i => $"3f9c2a7e-0000-4000-8000-0000000{i:x5}").Reverse().ToArray();
        PushRecords(ids.Select(id => Rec(id, "text " + id)).ToArray());
        var seen = new List<string>();
        _applier.BeforeEntry = seen.Add;

        await Pull();

        Assert.Equal(ids.Order(StringComparer.Ordinal), seen);
    }

    // ---- postponed ----

    [GitFact]
    public async Task ARemoteDeletion_IsPostponed_LStays_AndTheMemorySurvives()
    {
        var first = PushRecords(Rec(A), Rec(B, "Other."));
        await Pull();
        _rig.Remove($"memories/{A[..2]}/{A}.json");
        _rig.Commit("remove a");

        var summary = await Pull();

        Assert.False(summary.Complete);
        Assert.Equal("deletion", Assert.Single(summary.Postponed).Reason);
        Assert.Equal(A, summary.Postponed[0].Id);
        Assert.Equal(first, Meta(SyncIdentity.MetaKeys.LastSha));
        Assert.True(Has(A));
    }

    [GitFact]
    public async Task ALocalDeletion_WithTheRecordUnchanged_IsPostponed_AndLStays()
    {
        var first = PushRecords(Rec(A));
        await Pull();
        _rig.Store.Delete(Tier.Warm, ContentType.Memory, A);
        _rig.PutText("README", "x");
        _rig.Commit("another commit");

        var summary = await Pull();

        Assert.False(summary.Complete);
        Assert.Equal("deletion", Assert.Single(summary.Postponed).Reason);
        Assert.Equal(first, Meta(SyncIdentity.MetaKeys.LastSha));
    }

    [GitFact]
    public async Task ANewPinAndANoBaseTextDifference_AreReviewsThatHoldL()
    {
        _rig.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "Local text.", Id: B));
        var head = PushRecords(Rec(A, tier: Tier.Hot, sticky: true), Rec(B, "Their text."));

        var summary = await Pull();

        Assert.False(summary.Complete);
        Assert.Equal(2, summary.Postponed.Count);
        Assert.All(summary.Postponed, p => Assert.Equal("review", p.Reason));
        Assert.NotEqual(head, Meta(SyncIdentity.MetaKeys.LastSha));
        Assert.False(Has(A));
        Assert.Equal("Local text.", Content(B));
    }

    // ---- failures and rejections ----

    [GitFact]
    public async Task AFailure_NextToARejectedRecordAndValidOnes_AppliesTheValidOnes_HoldsNothing_AndIsRetriedByTheNextPull()
    {
        _embedder.Fails = t => t == "Bad one.";
        _rig.PutRaw($"memories/{Linked[..2]}/{Linked}.json", Encoding.UTF8.GetBytes("../x"), "120000");
        var head = PushRecords(Rec(A), Rec(B, "Bad one."), Rec(C, "Third."));

        var summary = await Pull();

        Assert.Equal(2, summary.Inserted);
        var failure = Assert.Single(summary.Failed);
        Assert.Equal((B, 1), (failure.Id, failure.Attempts));
        Assert.Equal("symlink", Assert.Single(summary.Rejected).Reason);
        Assert.True(summary.Complete);                        // neither holds L
        Assert.Equal(head, Meta(SyncIdentity.MetaKeys.LastSha));
        Assert.False(Has(B));
        Assert.Single(Issues("apply-failed"));
        Assert.Single(Issues("rejected-record"));

        _embedder.Fails = _ => false;
        var again = await Pull(); // the head did not move: a NothingNew that still visits the failed id

        Assert.Equal(1, again.Inserted);
        Assert.True(Has(B));
        Assert.Empty(Issues("apply-failed"));
    }

    [GitFact]
    public async Task ARejectedPathOfAKnownId_IsNeverDecidedAsDeleted_AndTheRejectionIsClearedWhenTheRecordIsValidAgain()
    {
        PushRecords(Rec(A));
        await Pull();
        _rig.Remove($"memories/{A[..2]}/{A}.json");
        _rig.PutRaw($"memories/{A[..2]}/{A}.json", Encoding.UTF8.GetBytes("../x"), "120000");
        var head = _rig.Commit("symlink in place of the record");

        var summary = await Pull();

        Assert.True(Has(A));
        Assert.Empty(summary.Postponed);
        Assert.Equal("symlink", Assert.Single(summary.Rejected).Reason);
        Assert.True(summary.Complete);
        Assert.Equal(head, Meta(SyncIdentity.MetaKeys.LastSha));
        Assert.Single(Issues("rejected-record"));

        _rig.Remove($"memories/{A[..2]}/{A}.json");
        _rig.PutRecord(Rec(A, "Valid again.", minute: 30));
        _rig.Commit("valid again");
        var fixedUp = await Pull();

        Assert.Empty(fixedUp.Rejected);
        Assert.Empty(Issues("rejected-record"));
        Assert.Equal("Valid again.", Content(A));
    }

    [GitFact]
    public async Task AParseRejectedRecordAtAKnownId_IsNeverDecidedAsDeleted_ListedByItsKey_AndSweptWhenFixed()
    {
        PushRecords(Rec(A));
        await Pull();
        var path = $"memories/{A[..2]}/{A}.json";
        _rig.PutText(path, "{\"format\":\"total-recall/memory/v9\"}");
        var head = _rig.Commit("an unknown format");

        var summary = await Pull();

        Assert.True(Has(A));
        Assert.Empty(summary.Postponed);
        Assert.True(summary.Complete);
        Assert.Equal(head, Meta(SyncIdentity.MetaKeys.LastSha));
        Assert.Equal(SyncApply.rejectionKey(path), Assert.Single(summary.Rejected).Key);
        Assert.Contains("unknown format", summary.Rejected[0].Reason);
        Assert.Single(Issues("rejected-record"));

        _rig.PutRecord(Rec(A, "Fixed.", minute: 40));
        _rig.Commit("fixed");
        await Pull();
        Assert.Empty(Issues("rejected-record"));
        Assert.Equal("Fixed.", Content(A));
    }

    [GitFact]
    public async Task ARejectedIdThatWasDeletedHere_IsPostponedAsADeletion()
    {
        PushRecords(Rec(A));
        await Pull();
        _rig.Store.Delete(Tier.Warm, ContentType.Memory, A);
        _rig.Remove($"memories/{A[..2]}/{A}.json");
        _rig.PutRaw($"memories/{A[..2]}/{A}.json", Encoding.UTF8.GetBytes("../x"), "120000");
        _rig.Commit("symlink");

        var summary = await Pull();

        Assert.Equal("deletion", Assert.Single(summary.Postponed).Reason);
        Assert.False(summary.Complete);
    }

    [GitFact]
    public async Task AnIdenticalRejectionRow_IsNotRewritten()
    {
        _rig.PutRaw($"memories/{Linked[..2]}/{Linked}.json", Encoding.UTF8.GetBytes("../x"), "120000");
        _rig.Commit("symlink");
        await Pull();
        ScanRig.Exec(_rig.Connection, "CREATE TABLE issue_writes (n INTEGER)");
        ScanRig.Exec(_rig.Connection, "CREATE TRIGGER i1 AFTER INSERT ON git_sync_issue BEGIN INSERT INTO issue_writes VALUES (1); END");
        ScanRig.Exec(_rig.Connection, "CREATE TRIGGER i2 AFTER UPDATE ON git_sync_issue BEGIN INSERT INTO issue_writes VALUES (1); END");
        _rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced"); // an unproven pull judges the path again

        await Pull();

        using var cmd = _rig.Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM issue_writes";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [GitFact]
    public async Task EveryRejectionShape_NeverThrows_AndIsListed()
    {
        _rig.PutRaw($"memories/{Linked[..2]}/{Linked}.json", Encoding.UTF8.GetBytes("../x"), "120000");
        _rig.PutText("memories/4f/" + A + ".json", "{}");
        _rig.PutText("memories/zz/zz.json", "{}");
        _rig.PutText("memories/3f/sub/" + A + ".json", "{}");
        _rig.PutRaw($"memories/3f/{C}.json", new byte[1_048_577]);
        _rig.PutText($"memories/3f/{B}.json", "not json");
        _rig.Commit("shapes");

        var summary = await Pull();

        Assert.Equal(6, summary.Rejected.Count);
        Assert.True(summary.Complete);
        Assert.Empty(_rig.Store.List(Tier.Warm, ContentType.Memory));
    }

    // ---- scan-skipped ----

    [GitFact]
    public async Task AScanSkippedId_IsNotApplied_DoesNotHoldL_AndItsRowSurvivesTheCleanUp()
    {
        var head = PushRecords(Rec(A), Rec(B, "Other."));
        var scan = new ScanSummary(0, 0, 0, 0, 0, 0, 0, 0, [], [new SkippedItem(A, "content", "unreadable entry")]);

        var summary = await Pull(scan);

        Assert.False(Has(A));
        Assert.True(Has(B));
        Assert.True(summary.Complete);
        Assert.Equal(head, Meta(SyncIdentity.MetaKeys.LastSha));
        var issue = Assert.Single(Issues("apply-failed"));
        Assert.StartsWith("scan-skipped:", issue.Detail);
        Assert.Equal(0, issue.Attempts);
        Assert.Contains(summary.Skipped, s => s.Id == A);

        // the next pull (nothing new) visits it again because of the row, and applies it once the scan no longer skips it
        var later = await Pull();
        Assert.Equal(1, later.Inserted);
        Assert.Empty(Issues("apply-failed"));
    }

    // ---- busy, cancelled, failed L ----

    [GitFact]
    public async Task ABusyDatabase_StopsTheApply_CountsNotAttempted_HoldsL_AndSkipsTheSweep()
    {
        PushRecords(Rec(A), Rec(B, "Other."), Rec(C, "Third."));
        _rig.Connection.DefaultTimeout = 1;
        ScanRig.Exec(_rig.Connection, "PRAGMA busy_timeout = 0");
        using var holder = SyncDb.OpenMigrated(_rig.DbPath);
        _applier.BeforeTransaction = _ => ScanRig.Exec(holder, "BEGIN IMMEDIATE");
        try
        {
            var summary = await Pull();

            Assert.False(summary.Complete);
            Assert.Equal(2, summary.NotAttempted);
            Assert.Equal(0, summary.Inserted);
            Assert.Null(Meta(SyncIdentity.MetaKeys.LastSha));
            Assert.NotEqual(_rig.Head, Meta(SyncIdentity.MetaKeys.LastSha));
            Assert.Empty(Issues("apply-failed"));
        }
        finally
        {
            ScanRig.Exec(holder, "ROLLBACK");
        }
    }

    [GitFact]
    public async Task ACancelledToken_StopsBetweenEntries_WritesNoIssue_AndHoldsL()
    {
        PushRecords(Rec(A), Rec(B, "Other."), Rec(C, "Third."));
        using var cts = new CancellationTokenSource();
        _applier.BeforeEntry = _ => cts.Cancel(); // cancelled while the first entry is being applied: it finishes, nothing starts after it

        var fetched = Is<FetchOutcome.Fetched>(await _rig.Fetcher().FetchAsync());
        var done = Is<ApplyOutcome.Done>(await _applier.ApplyAsync(fetched.Result, Scan(), cts.Token));

        Assert.False(done.Summary.Complete);
        Assert.Equal(1, done.Summary.Inserted);
        Assert.Empty(Issues("apply-failed"));
        Assert.Null(Meta(SyncIdentity.MetaKeys.LastSha));
    }

    [GitFact]
    public async Task ACancelledTokenBeforeTheStart_IsAFailedListing_AndChangesNothing()
    {
        PushRecords(Rec(A));
        var fetched = Is<FetchOutcome.Fetched>(await _rig.Fetcher().FetchAsync());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Is<ApplyOutcome.Failed>(await _applier.ApplyAsync(fetched.Result, Scan(), cts.Token));

        Assert.False(Has(A));
        Assert.Null(Meta(SyncIdentity.MetaKeys.LastSha));
    }

    [GitFact]
    public async Task AGitCancelledOnABlobRead_IsNotAFailure_AndHoldsL()
    {
        PushRecords(Rec(A));
        var blob = BlobOf($"memories/{A[..2]}/{A}.json");
        _rig.Fault = c => c is { } && c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == blob
            ? FaultingGitProcess.Failure(SyncGit.FailureKind.Cancelled, "cancelled")
            : null;

        var summary = await Pull();

        Assert.False(summary.Complete);
        Assert.Empty(summary.Failed);
        Assert.Empty(Issues("apply-failed"));
        Assert.NotEqual(_rig.Head, Meta(SyncIdentity.MetaKeys.LastSha));
    }

    [GitFact]
    public async Task AFailedBlobRead_IsThatEntrysFailure_AndLStillAdvances_ButTheSweepIsSkipped()
    {
        PushRecords(Rec(A), Rec(B, "Other."));
        var blob = BlobOf($"memories/{A[..2]}/{A}.json");
        _rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == blob
            ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "bad object")
            : null;

        var summary = await Pull();

        var failure = Assert.Single(summary.Failed);
        Assert.Equal(A, failure.Id);
        Assert.Contains("could not be read", failure.Detail);
        Assert.Equal(1, summary.Inserted);
        Assert.True(summary.Complete);
        Assert.Equal(blob, Assert.Single(Issues("apply-failed")).RecordHash);
    }

    private void SeedRejectionRow(string key) =>
        ScanRig.Exec(_rig.Connection,
            $"INSERT INTO git_sync_issue (entry_id, kind, record_hash, attempts, detail) VALUES ('{key}', 'rejected-record', 'old', 0, 'symlink')");

    [GitFact]
    public async Task AnEarlyStop_KeepsEveryRejectionRow_ButACompletePullSweepsThem()
    {
        const string stale = "memories/zz/gone.json";
        SeedRejectionRow(stale);
        PushRecords(Rec(A), Rec(B, "Other."));

        // a blob read that fails: the pull is complete but unread records may hide a rejection, so no sweep
        var blob = BlobOf($"memories/{A[..2]}/{A}.json");
        _rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == blob
            ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "bad object")
            : null;
        await Pull();
        Assert.Contains(Issues("rejected-record"), r => r.EntryId == stale);

        // a busy stop
        _rig.Fault = _ => null;
        _rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        _rig.Connection.DefaultTimeout = 1;
        ScanRig.Exec(_rig.Connection, "PRAGMA busy_timeout = 0");
        using (var holder = SyncDb.OpenMigrated(_rig.DbPath))
        {
            _applier.BeforeTransaction = _ => ScanRig.Exec(holder, "BEGIN IMMEDIATE");
            try { await Pull(); }
            finally { ScanRig.Exec(holder, "ROLLBACK"); _applier.BeforeTransaction = null; }
        }
        Assert.Contains(Issues("rejected-record"), r => r.EntryId == stale);

        // a cancellation
        using var cts = new CancellationTokenSource();
        _applier.BeforeEntry = _ => cts.Cancel();
        var fetched = Is<FetchOutcome.NothingNew>(await _rig.Fetcher().FetchAsync());
        await _applier.ApplyAsync(fetched.Result, Scan(), cts.Token);
        _applier.BeforeEntry = null;
        Assert.Contains(Issues("rejected-record"), r => r.EntryId == stale);

        // and a complete one sweeps it
        _rig.Connection.DefaultTimeout = 30;
        var done = await Pull();
        Assert.True(done.Complete);
        Assert.DoesNotContain(Issues("rejected-record"), r => r.EntryId == stale);
    }

    [GitFact]
    public async Task ACancellationDuringTheLastEntry_MovesNeitherMetaNorTheRef()
    {
        PushRecords(Rec(A));
        using var cts = new CancellationTokenSource();
        _applier.BeforeEntry = _ => cts.Cancel();
        var fetched = Is<FetchOutcome.Fetched>(await _rig.Fetcher().FetchAsync());

        var done = Is<ApplyOutcome.Done>(await _applier.ApplyAsync(fetched.Result, Scan(), cts.Token));

        Assert.Equal(1, done.Summary.Inserted);
        Assert.False(done.Summary.Complete);
        Assert.Null(Meta(SyncIdentity.MetaKeys.LastSha));
        Assert.Null(_rig.CloneRef("refs/total-recall/last-synced"));
    }

    [GitFact]
    public async Task AFailureToWriteTheRef_IsFailed_WithMetaAlreadyMoved_AndTheNextPullRepairsIt()
    {
        var head = PushRecords(Rec(A));
        _rig.Fault = c => c.IsUpdateLastSynced ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "ref locked") : null;

        var (_, outcome) = await PullWith();

        Is<ApplyOutcome.Failed>(outcome);
        Assert.Equal(head, Meta(SyncIdentity.MetaKeys.LastSha));
        Assert.Null(_rig.CloneRef("refs/total-recall/last-synced"));

        _rig.Fault = _ => null;
        var summary = await Pull();
        Assert.False(summary.Proven);
        Assert.Equal(head, _rig.CloneRef("refs/total-recall/last-synced"));
    }

    [GitFact]
    public async Task AFailedMetaWrite_IsFailed_WithLUnchanged()
    {
        PushRecords(Rec(A));
        ScanRig.Exec(_rig.Connection, "CREATE TRIGGER no_l BEFORE INSERT ON _meta WHEN NEW.key = 'git_sync_last_sha' BEGIN SELECT RAISE(ABORT, 'no'); END");

        var (_, outcome) = await PullWith();

        Is<ApplyOutcome.Failed>(outcome);
        Assert.Null(Meta(SyncIdentity.MetaKeys.LastSha));
        Assert.Null(_rig.CloneRef("refs/total-recall/last-synced"));
    }

    [GitFact]
    public async Task AFailedListing_IsFailed_AndNothingChanges()
    {
        PushRecords(Rec(A));
        var fetch = Is<FetchOutcome.Fetched>(await _rig.Fetcher().FetchAsync());
        _rig.Fault = c => c.IsLsTree ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "corrupt object") : null;

        var outcome = await _applier.ApplyAsync(fetch.Result, Scan());

        Is<ApplyOutcome.Failed>(outcome);
        Assert.False(Has(A));
        Assert.Null(Meta(SyncIdentity.MetaKeys.LastSha));
    }

    [GitFact]
    public async Task AnUnknownFieldInARecord_SurvivesInTheRowsBase()
    {
        _rig.PutText($"memories/{A[..2]}/{A}.json", RecordFixtures.Unknowns.Replace("3f9c2a7e-1111", A));
        _rig.Commit("unknowns");

        var summary = await Pull();

        Assert.Equal(1, summary.Inserted);
        Assert.Contains("\"future\": true", GitSyncRows.Read(_rig.Connection, A)!.BaseJson);
    }

    [GitFact]
    public async Task AScanRightAfterAPull_StampsNothing()
    {
        PushRecords(Rec(A), Rec(B, "Other.", tier: Tier.Hot));
        await Pull();
        PushRecords(Rec(A, "Edited.", minute: 20));
        await Pull();

        var scan = Scan();

        Assert.Equal(0, scan.New + scan.Content + scan.Tags + scan.Sticky + scan.Tier + scan.Left + scan.Reactivated);
    }
}
