using TotalRecall.Core;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.TestSupport.Transcripts;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Git sync secret block (#161, spec D5 and D6): the push re-checks every entry it is about to write. The preparation runs
/// the detectors and keeps each candidate's key and hit; the plan transaction only compares keys and writes the block, so
/// no record with a token is produced whatever produced the text. Most tests make the scan miss the token
/// (<see cref="PushRig.ScanDetector"/>), so it reaches the push as text the scan never saw would. Every token comes from
/// <see cref="FakeSecrets"/>.
/// </summary>
public sealed class GitSyncPusherSecretTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";
    private const string C = "c3d4e5f6";
    private const int Limit = 262_144;

    private static readonly FakeSecrets Fakes = new(41);

    private readonly PushRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>The scan finds nothing: a token reaches the push unmarked.</summary>
    private void BlindScan() => _rig.ScanDetector = _ => [];

    private async Task<(PushOutcome Outcome, GitSyncPusher Pusher)> PullAndPush(Action<GitSyncPusher>? configure = null)
    {
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        var pusher = _rig.Pusher();
        configure?.Invoke(pusher);
        return (await pusher.PushAsync(apply, scan), pusher);
    }

    private static PushSummary SummaryOf(PushOutcome outcome) => outcome switch
    {
        PushOutcome.Pushed p => p.Summary,
        PushOutcome.NothingToPush n => n.Summary,
        _ => throw new Xunit.Sdk.XunitException("the push did not finish: " + outcome),
    };

    private GitSyncRow Row(string id) => GitSyncRows.ReadAll(_rig.Pull.Connection)[id];

    private void WriteRow(GitSyncRow row) => GitSyncRows.Write(_rig.Pull.Connection, row);

    /// <summary>The secret key of the entry as the store holds it now.</summary>
    private string KeyOf(string id)
    {
        var read = Assert.IsType<LocalRead.Present>(LocalEntryReader.Read(
            t => _rig.Store.Get(t, ContentType.Memory, id), () => _rig.Store.IsSticky(ContentType.Memory, id)));
        return SyncSecret.secretKey(read.View.Content, read.View.Tags);
    }

    private string[] RemotePaths() =>
        _rig.Remote.Git("ls-tree", "-r", "--name-only", "main").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private void Exec(string sql)
    {
        using var cmd = _rig.Pull.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- a hit is not written ----

    [GitFact]
    public async Task ANewMemoryWithAToken_IsNotWritten_AndIsListedBlocked()
    {
        _rig.Seed();
        BlindScan();
        _rig.AddMemory(Tier.Warm, A, content: "the token is " + Fakes.Get("github-token"));
        _rig.AddMemory(Tier.Warm, B, content: "Use bun, not npm.");
        _rig.AddMemory(Tier.Warm, C, content: "Prefer tabs.");

        var (outcome, _) = await PullAndPush();

        var summary = Assert.IsType<PushOutcome.Pushed>(outcome).Summary;
        Assert.Equal([B, C], summary.Written);
        Assert.Equal([new PushItem(A, "secret-detected")], summary.Blocked);
        Assert.Empty(summary.Deferred);
        Assert.Empty(summary.Skipped);
        Assert.DoesNotContain(SyncValidation.recordPath(A), RemotePaths());
        Assert.Contains(SyncValidation.recordPath(B), RemotePaths());
        var row = Row(A);
        Assert.Equal("secret-detected", row.BlockedReason);
        Assert.Equal(KeyOf(A), row.BlockedHash);
        Assert.Null(row.AllowedHash);
        Assert.Null(row.BaseJson);
        Assert.Null(row.PushingStamps);
        Assert.NotNull(row.ContentStamp);
    }

    [GitFact]
    public async Task AnAllowedMemory_IsWritten()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, content: "the key is " + Fakes.Get("openai-key"));
        _rig.Now += 1000;
        await _rig.PullAsync();
        Assert.Equal("secret-detected", Row(A).BlockedReason);
        // what a release leaves: the mark cleared, the fields allowed
        var key = KeyOf(A);
        WriteRow(Row(A) with { BlockedReason = null, BlockedHash = null, AllowedHash = key });

        var (outcome, _) = await PullAndPush();

        var summary = Assert.IsType<PushOutcome.Pushed>(outcome).Summary;
        Assert.Equal([A], summary.Written);
        Assert.Empty(summary.Blocked);
        Assert.Contains(SyncValidation.recordPath(A), RemotePaths());
        var row = Row(A);
        Assert.Null(row.BlockedReason);
        Assert.Equal(key, row.AllowedHash);
    }

    [GitFact]
    public async Task ATextTheApplyChangedToHoldAToken_IsBlocked_AtThePush()
    {
        var token = Fakes.Get("github-token");
        _rig.Seed(Rec(A, content: "Use bun, not npm.", tags: ["a"]));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Tags = ["a", "local"] });
        // another machine adds a token tag: the apply's union brings it into the local entry after the scan ran
        _rig.Pull.PutRecord(Rec(A, content: "Use bun, not npm.", tags: ["a", token]));
        _rig.Pull.Commit("another machine");
        _rig.Pull.Source.PushTo(_rig.Remote.Path);
        var remoteBefore = _rig.Remote.Git("show", "main:" + SyncValidation.recordPath(A));

        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();

        Assert.Empty(scan.Blocked);
        Assert.Null(Row(A).BlockedReason);
        Assert.Contains(token, _rig.Store.Get(Tier.Warm, ContentType.Memory, A)!.Tags);

        var outcome = await _rig.Pusher().PushAsync(apply, scan);

        var summary = SummaryOf(outcome);
        Assert.Empty(summary.Written);
        Assert.Equal([new PushItem(A, "secret-detected")], summary.Blocked);
        Assert.Equal(remoteBefore, _rig.Remote.Git("show", "main:" + SyncValidation.recordPath(A)));
        Assert.Equal("secret-detected", Row(A).BlockedReason);
        Assert.Equal(KeyOf(A), Row(A).BlockedHash);
    }

    // ---- naming ----

    [GitFact]
    public async Task ABlockedRow_IsSkippedByName()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, content: "the token is " + Fakes.Get("github-token"));
        _rig.AddMemory(Tier.Warm, B, content: "Use bun, not npm.");
        _rig.Now += 1000;
        await _rig.PullAsync();
        Assert.Equal("secret-detected", Row(A).BlockedReason);
        WriteRow(Row(B) with { BlockedReason = "secret", BlockedHash = "h" });

        var (outcome, _) = await PullAndPush();

        var summary = SummaryOf(outcome);
        Assert.Empty(summary.Written);
        Assert.Empty(summary.Blocked);
        Assert.Equal([new PushItem(A, "secret-detected"), new PushItem(B, "blocked-elsewhere")], summary.Skipped);
    }

    // ---- the key compared in the transaction ----

    [GitFact]
    public async Task AnEntryChangedAfterThePreparation_IsDeferredChangedSinceScan()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, content: "Use bun, not npm.");
        GitSyncRow? before = null;

        var (outcome, _) = await PullAndPush(p => p.BeforePlanTransaction = () =>
        {
            before = Row(A);
            _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "the token is " + Fakes.Get("github-token") });
        });

        var summary = SummaryOf(outcome);
        Assert.Empty(summary.Written);
        Assert.Equal([new PushItem(A, "changed-since-scan")], summary.Deferred);
        Assert.Empty(summary.Blocked);
        Assert.Equal(before, Row(A));
    }

    [GitFact]
    public async Task ACandidateWithNoPreparedKey_IsDeferred()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, content: "Use bun, not npm.");
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        // in two tiers while the preparation reads it: no key is prepared
        Exec($"INSERT INTO cold_memories SELECT * FROM warm_memories WHERE id = '{A}'");
        var pusher = _rig.Pusher();
        GitSyncRow? before = null;
        pusher.BeforePlanTransaction = () =>
        {
            Exec($"DELETE FROM cold_memories WHERE id = '{A}'"); // readable again, and it agrees with its row
            before = Row(A);
        };

        var outcome = await pusher.PushAsync(apply, scan);

        var summary = SummaryOf(outcome);
        Assert.Empty(summary.Written);
        Assert.Equal([new PushItem(A, "changed-since-scan")], summary.Deferred);
        Assert.DoesNotContain(SyncValidation.recordPath(A), RemotePaths());
        Assert.Equal(before, Row(A));
    }

    [GitFact]
    public async Task AKeyThatDiffersFromThePreparedKey_IsDeferred()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, content: "Use bun, not npm.");
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        // no scanned hashes: the row agrees with any text
        WriteRow(Row(A) with { ScannedContentHash = null, ScannedTagsHash = null });
        var pusher = _rig.Pusher();
        GitSyncRow? before = null;
        pusher.BeforePlanTransaction = () =>
        {
            before = Row(A);
            _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "the token is " + Fakes.Get("github-token") });
        };

        var outcome = await pusher.PushAsync(apply, scan);

        var summary = SummaryOf(outcome);
        Assert.Empty(summary.Written);
        Assert.Equal([new PushItem(A, "changed-since-scan")], summary.Deferred);
        Assert.DoesNotContain(SyncValidation.recordPath(A), RemotePaths());
        Assert.Equal(before, Row(A));
    }

    // ---- the mark ----

    [GitFact]
    public async Task AWriterReasonIsReplacedByTheSecretAtThePush()
    {
        _rig.Seed();
        var large = new string('x', Limit + 1);
        _rig.AddMemory(Tier.Warm, A, content: large);
        var (first, _) = await PullAndPush();
        Assert.Equal([new PushItem(A, "too-large")], SummaryOf(first).Blocked);
        Assert.Equal("too-large", Row(A).BlockedReason);
        BlindScan();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = large + " " + Fakes.Get("aws-key") });

        var (outcome, _) = await PullAndPush();

        Assert.Equal([new PushItem(A, "secret-detected")], SummaryOf(outcome).Blocked);
        var row = Row(A);
        Assert.Equal("secret-detected", row.BlockedReason);
        Assert.Equal(KeyOf(A), row.BlockedHash);
    }

    [GitFact]
    public async Task AFailedSecretWrite_RollsBackThePlan()
    {
        const string Clean = "a0000001"; // planned before the token entry
        const string Token = "b0000002";
        _rig.Seed();
        BlindScan();
        _rig.AddMemory(Tier.Warm, Clean, content: "Use bun, not npm.");
        _rig.AddMemory(Tier.Warm, Token, content: "the token is " + Fakes.Get("github-token"));
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        var before = _rig.RowDump();
        var pusher = _rig.Pusher();
        var steps = new List<string>();
        pusher.BeforeStep = (id, s) =>
        {
            if (id != Token)
                return;
            steps.Add(s);
            if (s == "block")
                throw new InvalidOperationException("injected fault at block");
        };

        var outcome = await pusher.PushAsync(apply, scan);

        // the fault is the secret write's: no record was planned for the token entry
        Assert.Equal(["reread", "block"], steps);
        var failed = Assert.IsType<PushOutcome.Failed>(outcome);
        Assert.Equal("Store", failed.Kind);
        Assert.Contains("injected fault", failed.Message);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Equal(before, _rig.RowDump());
        Assert.DoesNotContain(SyncValidation.recordPath(Clean), RemotePaths());
    }

    [GitFact]
    public async Task TheDetectorNeverRunsInsideThePlanTransaction()
    {
        _rig.Seed();
        BlindScan();
        _rig.AddMemory(Tier.Warm, A, content: "the token is " + Fakes.Get("github-token"));
        _rig.AddMemory(Tier.Warm, B, content: "Use bun, not npm.", tags: ["clean"]);
        var planning = false;
        var before = 0;
        var inside = 0;

        var (outcome, _) = await PullAndPush(p =>
        {
            p.SecretDetector = text =>
            {
                if (planning) inside++;
                else before++;
                return SensitiveContentScanner.ScanSecrets(text);
            };
            p.BeforePlanTransaction = () => planning = true;
        });

        var summary = SummaryOf(outcome);
        Assert.Equal([new PushItem(A, "secret-detected")], summary.Blocked);
        Assert.Equal([B], summary.Written);
        Assert.True(planning);
        Assert.True(before > 0);
        Assert.Equal(0, inside);
    }

    [GitFact]
    public async Task ACancellationDuringTheSecretCheck_StopsItBeforeTheNextCandidate()
    {
        _rig.Seed();
        BlindScan();
        _rig.AddMemory(Tier.Warm, A, content: "first memory");
        _rig.AddMemory(Tier.Warm, B, content: "second memory");
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        using var cts = new CancellationTokenSource();
        var seen = new List<string?>();
        var pusher = _rig.Pusher();
        pusher.SecretDetector = text =>
        {
            seen.Add(text);
            cts.Cancel();
            return [];
        };

        var result = await pusher.PrepareAsync(apply, scan, cts.Token);

        var stopped = Assert.IsType<PrepareResult.Stop>(result);
        Assert.Equal("Cancelled", Assert.IsType<PushOutcome.Failed>(stopped.Outcome).Kind);
        Assert.Contains("first memory", seen);
        Assert.DoesNotContain("second memory", seen);
    }

    /// <summary>
    /// A clean push of other entries leaves a secret mark alone: the blocked row is skipped (listed in <c>Skipped</c>), not
    /// finalized. Finalize's own-mark test is pinned through the F# <c>isWriterReason</c> assertion
    /// (<c>SyncPushTests</c>: <c>secret-detected</c> is not a writer reason), so this test shows the outcome of that rule.
    /// </summary>
    [GitFact]
    public async Task AFinalizeAndACleanPushOfOtherEntries_KeepTheMark()
    {
        _rig.Seed();
        BlindScan();
        _rig.AddMemory(Tier.Warm, A, content: "the token is " + Fakes.Get("github-token"));
        _rig.AddMemory(Tier.Warm, B, content: "Use bun, not npm.");
        var (first, _) = await PullAndPush();
        Assert.Equal([B], Assert.IsType<PushOutcome.Pushed>(first).Summary.Written);
        var key = KeyOf(A);
        Assert.Equal(("secret-detected", key), (Row(A).BlockedReason, Row(A).BlockedHash));

        _rig.ScanDetector = null; // the real scan from now on
        _rig.AddMemory(Tier.Warm, C, content: "Prefer tabs.");
        var (second, _) = await PullAndPush();

        var summary = Assert.IsType<PushOutcome.Pushed>(second).Summary;
        Assert.Equal([C], summary.Written);
        Assert.Equal([new PushItem(A, "secret-detected")], summary.Skipped);
        var row = Row(A);
        Assert.Equal(("secret-detected", key), (row.BlockedReason, row.BlockedHash));
        Assert.Null(row.BaseJson);
        Assert.DoesNotContain(SyncValidation.recordPath(A), RemotePaths());
    }

    [GitFact]
    public async Task TheWriterNeverClearsASecretMark()
    {
        _rig.Seed(Rec(A));
        _rig.Now += 1000;
        await _rig.PullAsync();
        // a synced entry (not a candidate) whose row carries a secret mark for its current fields
        WriteRow(Row(A) with { BlockedReason = "secret-detected", BlockedHash = KeyOf(A) });
        _rig.AddMemory(Tier.Warm, B, content: "Use bun, not npm."); // the plan transaction opens and runs the clear pass

        var (outcome, pusher) = await PullAndPush();

        Assert.Equal([B], Assert.IsType<PushOutcome.Pushed>(outcome).Summary.Written);
        Assert.Equal(2, pusher.TransactionCount);
        Assert.Equal(("secret-detected", KeyOf(A)), (Row(A).BlockedReason, Row(A).BlockedHash));
    }

    [GitFact]
    public async Task ALiftedSecretOnATooLargeMemory_IsMarkedTooLargeByTheNextPush()
    {
        _rig.Seed();
        var large = new string('x', Limit + 1);
        _rig.AddMemory(Tier.Warm, A, content: large + " " + Fakes.Get("aws-key"));
        var (first, _) = await PullAndPush();
        Assert.Equal([new PushItem(A, "secret-detected")], SummaryOf(first).Skipped);
        Assert.Equal("secret-detected", Row(A).BlockedReason);

        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = large });
        var (outcome, _) = await PullAndPush();

        Assert.Equal([new PushItem(A, "too-large")], SummaryOf(outcome).Blocked);
        Assert.Empty(SummaryOf(outcome).Skipped);
        var row = Row(A);
        Assert.Equal("too-large", row.BlockedReason);
        Assert.Null(row.AllowedHash);
    }

    // ---- store failures ----

    [GitFact]
    public async Task AnEntryThatCannotBeReadFromTheStore_InThePreparation_IsFailedStore()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        Exec("ALTER TABLE hot_memories RENAME TO hot_memories_gone");

        var result = await _rig.Pusher().PrepareAsync(apply, scan, CancellationToken.None);

        var failed = Assert.IsType<PushOutcome.Failed>(Assert.IsType<PrepareResult.Stop>(result).Outcome);
        Assert.Equal("Store", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
    }
}
