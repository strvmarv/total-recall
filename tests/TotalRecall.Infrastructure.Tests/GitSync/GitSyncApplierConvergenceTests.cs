using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Alias D2, D3 (conditions) and D6 (git sync part 5, #165): before the deletion preview a pre-pass computes the converging
/// and ignored ids; neither is counted by the delete guard, deleted, or held by the unproven fallback. The pre-pass fails
/// closed: in a proven pull a blob or knowledge read it cannot make keeps every local id that could have converged; in an
/// unproven pull an id with a known candidate (an open row or a readable detection) is ignored, never held, and only an id
/// with no known candidate is left to the unproven hold. The pre-pass tests assert
/// what holds whether an id converges or is ignored: never deleted, never held, never counted, and
/// <c>Has(x) || Has(canonical)</c> where a canonical is present. The convergence tests (alias D3 to D6) assert the re-key
/// onto the canonical id and the first-sync merge that follows it, one test per row of D4's table.
/// </summary>
public sealed class GitSyncApplierConvergenceTests
{
    // Ordinal: A < C < B < X (A and C share "a"; the canonical candidates are lower than the local id X).
    private const string A = "3f9c2a7e-0000-4000-8000-0000000000a0";
    private const string C = "3f9c2a7e-0000-4000-8000-0000000000a3";
    private const string B = "3f9c2a7e-0000-4000-8000-0000000000a5";
    private const string X = "3f9c2a7e-0000-4000-8000-0000000000b1";

    private const long Now = 1_800_000_000_000L;

    private static readonly string H = AliasTestSupport.Hash("host file");

    // ---- helpers ----

    private static (PullRig Rig, GitSyncApplier Applier) NewPull(ApplyOptions? options = null)
    {
        var rig = new PullRig();
        rig.Join();
        rig.PutManifest();
        rig.Commit("manifest");
        rig.Clone();
        return (rig, new GitSyncApplier(rig.Client, rig.Store, new CountingEmbedder(), options ?? new ApplyOptions(0), () => Now));
    }

    /// <summary>A pull; asserts that it deleted nothing and tripped no guard.</summary>
    private static async Task<ApplySummary> Pull(PullRig rig, GitSyncApplier applier, Func<ScanSummary, ScanSummary>? adjust = null)
    {
        var summary = await PullAllowingDeletions(rig, applier, adjust);
        Assert.Empty(summary.Deleted);
        return summary;
    }

    /// <summary>A pull; asserts that it tripped no guard.</summary>
    private static async Task<ApplySummary> PullAllowingDeletions(PullRig rig, GitSyncApplier applier, Func<ScanSummary, ScanSummary>? adjust = null)
    {
        var fetch = await rig.Fetcher().FetchAsync();
        var result = fetch switch
        {
            FetchOutcome.Fetched f => f.Result,
            FetchOutcome.NothingNew n => n.Result,
            _ => throw new Xunit.Sdk.XunitException("the fetch did not succeed: " + fetch),
        };
        var scan = Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(rig.Connection, () => Now).Scan()).Summary;
        if (adjust is not null)
            scan = adjust(scan);
        var summary = Assert.IsType<ApplyOutcome.Done>(await applier.ApplyAsync(result, scan)).Summary;
        Assert.Empty(summary.GuardTripped);
        return summary;
    }

    private static SyncRecordModule.SyncRecord Rec(string id, string text = "Use bun.", string? hash = null) =>
        ApplyRig.Rec(id, text, originHash: hash ?? H, writtenBy: ApplyRig.OtherMachine);

    private static bool Has(PullRig rig, string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(t => rig.Store.Get(t, ContentType.Memory, id) is not null);

    private static string BlobOf(PullRig rig, string id) => rig.Source.Git("rev-parse", "HEAD:" + SyncValidation.recordPath(id)).Trim();

    private static GitSyncRow? Row(PullRig rig, string id) => GitSyncRows.ReadAll(rig.Connection).GetValueOrDefault(id);

    private static IReadOnlyList<string> Aliases(PullRig rig) => AliasTestSupport.AliasRows(rig.Connection);

    private static bool HasHeldRow(PullRig rig, string id) =>
        GitSyncReviews.ReadOpenHeld(rig.Connection).ContainsKey(id) || GitSyncReviews.ReadApprovedHeld(rig.Connection).ContainsKey(id);

    private static string RecordJson(SyncRecordModule.SyncRecord record) => Encoding.UTF8.GetString(RecordWriter.Write(record).Bytes!);

    /// <summary>
    /// Commits <paramref name="x"/>'s record with origin <paramref name="hash"/> and pulls (the entry, its row active with a
    /// base and its <c>git-sync</c> log row exist), then commits <paramref name="c"/>'s record with the same origin and pulls
    /// (skipped: the open row <c>c|x|hash</c>). <paramref name="c"/> is lower than <paramref name="x"/>.
    /// </summary>
    private static async Task SyncedPair(
        PullRig rig, GitSyncApplier applier, string x, string c, string localText = "Use bun.", string canonicalText = "Use bun.", string? hash = null)
    {
        hash ??= H;
        rig.PutRecord(Rec(x, localText, hash));
        rig.Commit("x");
        await Pull(rig, applier);
        Assert.True(Has(rig, x));
        Assert.True(Row(rig, x) is { BaseJson: not null } r && r.State.IsActive);

        rig.PutRecord(Rec(c, canonicalText, hash));
        rig.Commit("c");
        await Pull(rig, applier);
        Assert.False(Has(rig, c));
        Assert.Contains($"{c}|{x}|{hash}|open", Aliases(rig));
    }

    /// <summary>Syncs <paramref name="x"/> alone (origin <see cref="H"/>) and seeds its host twin's <c>import_log</c> row of <see cref="H"/>: no open row yet.</summary>
    private static async Task SyncedAlone(PullRig rig, GitSyncApplier applier, string x)
    {
        rig.PutRecord(Rec(x));
        rig.Commit("x");
        await Pull(rig, applier);
        Assert.True(Has(rig, x));
        new ImportLog(rig.Connection).LogImport("claude-code", "/home/u/.claude/memory/" + x + ".md", H, x, Tier.Warm, ContentType.Memory);
        Assert.Empty(Aliases(rig));
    }

    /// <summary>
    /// The six content tables, the vector counts, <c>import_log</c>, <c>git_sync_entry</c>, <c>git_sync_review</c>,
    /// <c>git_sync_alias</c> and <c>git_sync_issue</c> as text. Without <paramref name="clock"/> the wall-clock columns
    /// (<c>timestamp</c>, <c>updated_at</c>, <c>last_accessed_at</c>) are left out, to compare two rigs.
    /// </summary>
    private static string Dump(PullRig rig, bool clock = true)
    {
        var sb = new StringBuilder();
        foreach (var table in new[]
                 {
                     "hot_memories", "warm_memories", "cold_memories", "hot_knowledge", "warm_knowledge", "cold_knowledge",
                     "import_log", "git_sync_entry", "git_sync_review", "git_sync_alias", "git_sync_issue",
                 })
        {
            sb.Append("== ").Append(table).Append('\n');
            using var cmd = rig.Connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} ORDER BY 1, 2";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                for (var i = 0; i < r.FieldCount; i++)
                {
                    if (!clock && r.GetName(i) is "timestamp" or "updated_at" or "last_accessed_at")
                        continue;
                    sb.Append(r.IsDBNull(i) ? "NULL" : r.GetValue(i)).Append('|');
                }
                sb.Append('\n');
            }
        }
        foreach (var table in new[] { "hot_memories_vec", "warm_memories_vec", "cold_memories_vec" })
        {
            using var cmd = rig.Connection.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM {table}";
            sb.Append(table).Append('=').Append(cmd.ExecuteScalar()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary><see cref="Dump"/> without the wall-clock columns, for comparing two rigs.</summary>
    private static string Snapshot(PullRig rig) => Dump(rig, clock: false);

    private static void AssertKept(PullRig rig, ApplySummary summary, string x, GitSyncRow? rowBefore)
    {
        Assert.True(Has(rig, x));
        Assert.Equal(rowBefore, Row(rig, x));
        Assert.DoesNotContain(x, summary.Deleted);
        Assert.DoesNotContain(summary.Held, h => h.Id == x);
        Assert.False(HasHeldRow(rig, x));
    }

    // ---- the guard ----

    [GitFact]
    public async Task ConvergingAndIgnoredRemovals_AreNotCountedByTheGuard()
    {
        var (rig, applier) = NewPull(new ApplyOptions(0, DeleteGuardPercent: 0));
        using var pullRig = rig;
        string Ci(int i) => $"3f9c2a7e-0000-4000-8000-0000000001{i:x2}";
        string Xi(int i) => $"3f9c2a7e-0000-4000-8000-0000000002{i:x2}";
        string Hi(int i) => AliasTestSupport.Hash("host file " + i);
        const int n = 11;
        for (var i = 0; i < n; i++)
            rig.PutRecord(Rec(Xi(i), "Text " + i, Hi(i)));
        rig.Commit("x records");
        await Pull(rig, applier);
        for (var i = 0; i < n; i++)
            rig.PutRecord(Rec(Ci(i), "Text " + i, Hi(i)));
        rig.Commit("c records");
        await Pull(rig, applier);
        Assert.Equal(n, Aliases(rig).Count(a => a.EndsWith("|open", StringComparison.Ordinal)));

        for (var i = 0; i < n; i++)
            rig.Remove(SyncValidation.recordPath(Xi(i)));
        rig.Commit("remove the x records");
        var summary = await Pull(rig, applier);

        Assert.Empty(summary.GuardTripped);
        Assert.Empty(summary.Held);
        Assert.Empty(summary.Deleted);
        for (var i = 0; i < n; i++)
        {
            Assert.True(Has(rig, Xi(i)) || Has(rig, Ci(i)));
            Assert.False(HasHeldRow(rig, Xi(i)));
        }
    }

    // ---- ignored: the canonical cannot be the target ----

    [GitFact]
    public async Task ACanonicalRecordAlsoAbsent_IsIgnored_TheEntryKept_NoHold_AliasOpen()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        var rowBefore = Row(rig, X);
        var before = Dump(rig);

        rig.Remove(SyncValidation.recordPath(X));
        rig.Remove(SyncValidation.recordPath(C));
        rig.Commit("remove both");
        var summary = await Pull(rig, applier);

        AssertKept(rig, summary, X, rowBefore);
        Assert.Empty(summary.Held);
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
        Assert.Equal(before, Dump(rig));
    }

    [GitFact]
    public async Task ACanonicalRecordThatDoesNotParse_IsIgnored()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        var rowBefore = Row(rig, X);

        rig.PutText(SyncValidation.recordPath(C), "{not json");
        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("c broken, x removed");
        var summary = await Pull(rig, applier);

        AssertKept(rig, summary, X, rowBefore);
        Assert.Empty(summary.Held);
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
        Assert.Contains(summary.Rejected, r => r.Key == SyncApply.rejectionKey(SyncValidation.recordPath(C)));
        Assert.Contains(GitSyncIssues.ReadOpen(rig.Connection, GitSyncIssues.RejectedRecord).Keys, k => k == SyncApply.rejectionKey(SyncValidation.recordPath(C)));
    }

    [GitTheory]
    [InlineData("memory")]
    [InlineData("knowledge")]
    public async Task AnEntryUnderTheCanonicalId_IgnoresTheOther_AndTheGuardCountsNeither(string kind)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        var rowBefore = Row(rig, X);
        rig.Store.Insert(Tier.Warm, kind == "memory" ? ContentType.Memory : ContentType.Knowledge, new InsertEntryOpts(Content: "Taken.", Id: C));

        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        var summary = await Pull(rig, applier);

        AssertKept(rig, summary, X, rowBefore);
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
    }

    [GitTheory]
    [InlineData("forgotten")]
    [InlineData("pending-deletion")]
    [InlineData("held-delete")]
    public async Task ARowOrAReviewUnderTheCanonicalId_IgnoresTheOther(string what)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        var rowBefore = Row(rig, X);
        var baseJson = RecordJson(Rec(C));
        switch (what)
        {
            case "forgotten":
                GitSyncRows.Write(rig.Connection, new GitSyncRow(C, SyncMerge.RowState.Forgotten, baseJson, null, null, null, null, null, null, null, null, null, null, null, null));
                break;
            case "pending-deletion":
                GitSyncRows.Write(rig.Connection, new GitSyncRow(C, SyncMerge.RowState.Active, baseJson, null, null, null, null, null, null, null, null, null, null, null, null));
                break;
            default:
                GitSyncReviews.UpsertHeld(rig.Connection, new HeldItem(C, "local", null, "unproven", 1_700_000_000_000L));
                break;
        }

        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        var summary = await Pull(rig, applier);

        AssertKept(rig, summary, X, rowBefore);
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
    }

    // ---- the pre-pass fails closed ----

    [GitTheory]
    [InlineData("other")]
    [InlineData("cancelled")]
    public async Task AFailedCandidateBlobRead_KeepsTheOther_NotDeletedNotHeld_AlsoOnTheNextPull(string kindName)
    {
        var kind = kindName == "other" ? SyncGit.FailureKind.Other : SyncGit.FailureKind.Cancelled;
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedAlone(rig, applier, X);
        var rowBefore = Row(rig, X);

        rig.PutRecord(Rec(C));
        rig.Remove(SyncValidation.recordPath(X));
        var head = rig.Commit("c added, x removed");
        var cBlob = BlobOf(rig, C);
        rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == cBlob ? FaultingGitProcess.Failure(kind, "bad object") : null;
        var summary = await Pull(rig, applier);

        AssertKept(rig, summary, X, rowBefore);
        Assert.Empty(summary.Held);
        Assert.Empty(Aliases(rig));
        if (kindName == "other")
        {
            Assert.Equal(C, Assert.Single(summary.Failed).Id);
            Assert.True(summary.Complete);
        }
        else
        {
            Assert.Empty(summary.Failed);
            Assert.False(summary.Complete);
            Assert.NotEqual(head, rig.Last);
        }

        rig.Fault = _ => null;
        var next = await Pull(rig, applier);

        Assert.Empty(next.Held);
        Assert.False(HasHeldRow(rig, X));
        Assert.True(Has(rig, X) || Has(rig, C));
    }

    [GitTheory]
    [InlineData("other")]
    [InlineData("cancelled")]
    public async Task AFailedBlobReadOfTheLowestCandidate_KeepsX_NotConvergedOntoTheHigher_AlsoOnTheNextPull(string kindName)
    {
        var kind = kindName == "other" ? SyncGit.FailureKind.Other : SyncGit.FailureKind.Cancelled;
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(Rec(X));
        rig.Commit("x");
        await Pull(rig, applier);
        rig.PutRecord(Rec(B));
        rig.Commit("b");
        await Pull(rig, applier);
        rig.PutRecord(Rec(A));
        rig.Commit("a");
        await Pull(rig, applier);
        Assert.Equal([$"{A}|{X}|{H}|open", $"{B}|{X}|{H}|open"], Aliases(rig));
        var rowBefore = Row(rig, X);

        rig.Remove(SyncValidation.recordPath(X));
        var head = rig.Commit("remove x");
        var aBlob = BlobOf(rig, A);
        rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == aBlob ? FaultingGitProcess.Failure(kind, "bad object") : null;
        var summary = await Pull(rig, applier);

        AssertKept(rig, summary, X, rowBefore);
        Assert.Empty(summary.Held);
        Assert.False(Has(rig, A));
        Assert.False(Has(rig, B));
        Assert.Equal([$"{A}|{X}|{H}|open", $"{B}|{X}|{H}|open"], Aliases(rig));
        Assert.Equal([X], LogTargets(rig, H));
        Assert.Empty(summary.Failed);
        Assert.True(summary.Complete);
        Assert.Equal(head, rig.Last);

        rig.Fault = _ => null;
        var next = await Pull(rig, applier);

        Assert.Empty(next.Held);
        Assert.False(HasHeldRow(rig, X));
        Assert.True(Has(rig, X) || Has(rig, A));
    }

    private static IReadOnlyList<string> LogTargets(PullRig rig, string hash)
    {
        var ids = new List<string>();
        using var cmd = rig.Connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT target_entry_id FROM import_log WHERE content_hash = $h ORDER BY 1";
        cmd.Parameters.AddWithValue("$h", hash);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            ids.Add(r.GetString(0));
        return ids;
    }

    private static void InsertCorruptKnowledge(PullRig rig, string id)
    {
        rig.Store.Insert(Tier.Warm, ContentType.Knowledge, new InsertEntryOpts(Content: "A knowledge chunk.", Id: id));
        ScanRig.Exec(rig.Connection, $"UPDATE warm_knowledge SET source_tool = 'bogus' WHERE id = '{id}'");
        Assert.ThrowsAny<ArgumentException>(() => rig.Store.Get(Tier.Warm, ContentType.Knowledge, id));
    }

    [GitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACorruptKnowledgeRowUnderTheArrivingCandidate_NeverDeletesTheOther_IgnoredWhenProven_HeldWhenUnproven(bool proven)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedAlone(rig, applier, X);
        var rowBefore = Row(rig, X);
        InsertCorruptKnowledge(rig, C);

        rig.PutRecord(Rec(C));
        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("c added, x removed");
        if (!proven)
            rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(rig, applier);

        Assert.Equal(proven, summary.Proven);
        Assert.Contains(summary.Skipped, s => s.Id == C);
        Assert.Empty(Aliases(rig));
        if (proven)
        {
            AssertKept(rig, summary, X, rowBefore);
            Assert.Empty(summary.Held);
        }
        else
        {
            // The check failed, so x is not cleared: no ignore, the unproven hold of W6.5 applies (a person's question).
            Assert.True(Has(rig, X));
            Assert.Contains(summary.Held, h => h.Id == X);
            Assert.True(GitSyncReviews.ReadOpenHeld(rig.Connection).ContainsKey(X));
        }
    }

    [GitFact]
    public async Task AnUnreadableKnowledgeCheckOnTheTarget_IgnoresTheOther()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        var rowBefore = Row(rig, X);
        InsertCorruptKnowledge(rig, C); // after the open row exists, so the knowledge check of step 5 is what reads it

        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        var summary = await Pull(rig, applier);

        AssertKept(rig, summary, X, rowBefore);
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
    }

    // ---- a failed check in an unproven pull: an id with alias evidence is ignored; one with none is held ----

    private static readonly string HOther = AliasTestSupport.Hash("another host file");

    /// <summary>
    /// Syncs <see cref="X"/> alone with no alias (no twin <c>import_log</c> row, no alias row), then commits the removal of
    /// its record and an unrelated record <see cref="Y"/> (another origin) whose pre-pass check fails: its blob read
    /// (<paramref name="fault"/> <c>blob</c>) or its knowledge check (<c>knowledge</c>, a corrupt knowledge row under Y).
    /// Returns the head.
    /// </summary>
    private static async Task<string> XAloneThenAFailingUnrelatedArrival(PullRig rig, GitSyncApplier applier, string fault, bool proven)
    {
        rig.PutRecord(Rec(X));
        rig.Commit("x");
        await Pull(rig, applier);
        Assert.True(Has(rig, X));
        Assert.Empty(Aliases(rig));
        if (fault == "knowledge")
            InsertCorruptKnowledge(rig, Y);
        rig.PutRecord(Rec(Y, "Use deno.", HOther));
        rig.Remove(SyncValidation.recordPath(X));
        var head = rig.Commit("y added, x removed");
        var yBlob = BlobOf(rig, Y);
        if (fault == "blob")
            rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == yBlob ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "bad object") : null;
        if (!proven)
            rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        return head;
    }

    [GitTheory]
    [InlineData("blob")]
    [InlineData("knowledge")]
    public async Task InAnUnprovenPull_AFailedCheckOfAnUnrelatedArrival_StillHoldsAMemoryWithNoAlias_AndTheNextPullDeletesNothing(string fault)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var head = await XAloneThenAFailingUnrelatedArrival(rig, applier, fault, proven: false);

        var summary = await Pull(rig, applier);

        Assert.False(summary.Proven);
        Assert.True(summary.Complete);
        Assert.Equal(head, rig.Last);
        Assert.True(Has(rig, X));
        Assert.Contains(summary.Held, h => h.Id == X);
        Assert.True(GitSyncReviews.ReadOpenHeld(rig.Connection).ContainsKey(X));
        if (fault == "blob")
            Assert.Equal(Y, Assert.Single(summary.Failed).Id);
        else
            Assert.Contains(summary.Skipped, s => s.Id == Y);

        rig.Fault = _ => null;
        var next = await Pull(rig, applier); // asserts that nothing is deleted

        Assert.True(next.Proven);
        Assert.True(Has(rig, X));
        Assert.True(GitSyncReviews.ReadOpenHeld(rig.Connection).ContainsKey(X));
    }

    [GitFact]
    public async Task InAProvenPull_AFailedBlobReadOfAnUnrelatedArrival_DelaysTheOrdinaryDeletionByOnePull()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await XAloneThenAFailingUnrelatedArrival(rig, applier, "blob", proven: true);
        var rowBefore = Row(rig, X);

        var summary = await Pull(rig, applier);

        Assert.True(summary.Proven);
        AssertKept(rig, summary, X, rowBefore);
        Assert.Empty(summary.Held);
        Assert.Equal(Y, Assert.Single(summary.Failed).Id);

        rig.Fault = _ => null;
        var next = await PullAllowingDeletions(rig, applier);

        Assert.Contains(X, next.Deleted);
        Assert.False(Has(rig, X));
        Assert.True(Has(rig, Y));
    }

    [GitFact]
    public async Task ACorruptKnowledgeRowUnderAnUnrelatedArrival_DoesNotStopTheProvenDeletionOfAnotherMemory()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await XAloneThenAFailingUnrelatedArrival(rig, applier, "knowledge", proven: true);

        var summary = await PullAllowingDeletions(rig, applier);

        Assert.True(summary.Proven);
        Assert.Contains(X, summary.Deleted);
        Assert.False(Has(rig, X));
        Assert.Contains(summary.Skipped, s => s.Id == Y);
    }

    [GitTheory]
    [InlineData("other")]
    [InlineData("cancelled")]
    public async Task InAnUnprovenPull_AFailedBlobReadOfTheLowestCandidate_IgnoresX_NotHeldNotConvergedNotDeleted(string kindName)
    {
        var kind = kindName == "other" ? SyncGit.FailureKind.Other : SyncGit.FailureKind.Cancelled;
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(Rec(X));
        rig.Commit("x");
        await Pull(rig, applier);
        rig.PutRecord(Rec(B));
        rig.Commit("b");
        await Pull(rig, applier);
        rig.PutRecord(Rec(A));
        rig.Commit("a");
        await Pull(rig, applier);
        Assert.Equal([$"{A}|{X}|{H}|open", $"{B}|{X}|{H}|open"], Aliases(rig));

        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        var aBlob = BlobOf(rig, A);
        rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == aBlob ? FaultingGitProcess.Failure(kind, "bad object") : null;
        rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(rig, applier);

        Assert.False(summary.Proven);
        Assert.True(Has(rig, X));
        Assert.False(Has(rig, A));
        Assert.False(Has(rig, B));
        // An unproven pull visits b's unchanged record too, whose skip also opens a|b (b is not a memory here).
        Assert.Contains($"{A}|{X}|{H}|open", Aliases(rig));
        Assert.Contains($"{B}|{X}|{H}|open", Aliases(rig));
        Assert.DoesNotContain(Aliases(rig), a => a.EndsWith("|resolved", StringComparison.Ordinal));
        if (kindName == "other")
        {
            // The target is unknown (a, the lowest candidate, cannot be read), but x has open rows: ignored, not held.
            Assert.True(summary.Complete);
            Assert.DoesNotContain(summary.Held, h => h.Id == X);
            Assert.False(HasHeldRow(rig, X));
        }
        else
        {
            Assert.False(summary.Complete); // a cancelled read stops the pull before x is visited
        }

        rig.Fault = _ => null;
        await Pull(rig, applier); // asserts that nothing is deleted

        Assert.True(Has(rig, X) || Has(rig, A));
        Assert.False(Has(rig, B));
    }

    /// <summary>
    /// <paramref name="withRow"/>: c also has a <c>forgotten</c> row, so c is no W1 candidate and the throwing knowledge
    /// check is step 5's (is the target free?) rather than step 3's. Either way x has an open row: ignored, never held.
    /// </summary>
    [GitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InAnUnprovenPull_AnUnreadableKnowledgeCheckOnTheTarget_IgnoresTheOther_NotHeld(bool withRow)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        InsertCorruptKnowledge(rig, C);
        if (withRow)
            GitSyncRows.Write(rig.Connection, new GitSyncRow(C, SyncMerge.RowState.Forgotten, RecordJson(Rec(C)), null, null, null, null, null, null, null, null, null, null, null, null));
        var rowBefore = Row(rig, X);

        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(rig, applier);

        Assert.False(summary.Proven);
        AssertKept(rig, summary, X, rowBefore);
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));

        var next = await Pull(rig, applier); // asserts that nothing is deleted

        Assert.True(next.Proven);
        AssertKept(rig, next, X, rowBefore);
    }

    /// <summary>
    /// Step 4: the lowest candidate a has a <c>forgotten</c> row (so it is no W1 candidate and is read only as a canonical
    /// record) and its blob read fails; b parses and is free. Both kinds of pull ignore x (no convergence onto b): a proven
    /// pull fails closed, an unproven one leaves x's target unknown but x has open rows, so it is not held. Never deleted.
    /// </summary>
    [GitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailedReadOfTheLowestCanonicalRecord_NeverConvergesOntoTheHigher_IgnoredInBothKindsOfPull(bool proven)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(Rec(X));
        rig.Commit("x");
        await Pull(rig, applier);
        rig.PutRecord(Rec(B));
        rig.Commit("b");
        await Pull(rig, applier);
        rig.PutRecord(Rec(A));
        rig.Commit("a");
        await Pull(rig, applier);
        Assert.Equal([$"{A}|{X}|{H}|open", $"{B}|{X}|{H}|open"], Aliases(rig));
        GitSyncRows.Write(rig.Connection, new GitSyncRow(A, SyncMerge.RowState.Forgotten, RecordJson(Rec(A)), null, null, null, null, null, null, null, null, null, null, null, null));
        var rowBefore = Row(rig, X);

        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        var aBlob = BlobOf(rig, A);
        rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == aBlob ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "bad object") : null;
        if (!proven)
            rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(rig, applier);

        Assert.Equal(proven, summary.Proven);
        Assert.True(Has(rig, X));
        Assert.False(Has(rig, B));
        Assert.DoesNotContain(Aliases(rig), a => a.EndsWith("|resolved", StringComparison.Ordinal));
        AssertKept(rig, summary, X, rowBefore);
    }

    /// <summary>
    /// A failed check that concerns only one local id leaves the others' alias evidence standing in an unproven pull: an id
    /// whose canonical record is gone stays ignored, and the id whose target is unknown is ignored too (it has an open row);
    /// neither is held.
    /// </summary>
    [GitFact]
    public async Task InAnUnprovenPull_AFailedCheckOfOneTarget_IgnoresBothIdsWithOpenRows_HoldsNeither()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        var h2 = AliasTestSupport.Hash("second host file");
        await SyncedPair(rig, applier, X2, A, hash: h2);
        InsertCorruptKnowledge(rig, C);

        rig.Remove(SyncValidation.recordPath(X));
        rig.Remove(SyncValidation.recordPath(X2));
        rig.Remove(SyncValidation.recordPath(A));
        rig.Commit("remove x, x2 and a");
        rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(rig, applier);

        Assert.False(summary.Proven);
        Assert.True(Has(rig, X));
        Assert.True(Has(rig, X2));
        Assert.Empty(summary.Held);
        Assert.False(HasHeldRow(rig, X));
        Assert.False(HasHeldRow(rig, X2));
    }

    [GitFact]
    public async Task InAnUnprovenPull_BothRecordsGoneAndAnUnrelatedFailedBlobRead_IgnoresTheIdWithAnOpenRow_NoHold()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        var rowBefore = Row(rig, X);

        rig.PutRecord(Rec(Y, "Use deno.", HOther));
        rig.Remove(SyncValidation.recordPath(X));
        rig.Remove(SyncValidation.recordPath(C));
        rig.Commit("y added, x and c removed");
        var yBlob = BlobOf(rig, Y);
        rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == yBlob ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "bad object") : null;
        rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(rig, applier);

        Assert.False(summary.Proven);
        Assert.Equal(Y, Assert.Single(summary.Failed).Id);
        AssertKept(rig, summary, X, rowBefore);
        Assert.Empty(summary.Held);
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
    }

    [GitFact]
    public async Task InAProvenPull_AFailedBlobReadOfARecordWithALocalMemory_DoesNotDelayTheDeletionOfAnotherMemory()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(Rec(X));
        rig.Commit("x");
        await Pull(rig, applier);
        Assert.True(Has(rig, X));

        rig.PutRecord(Rec(Y, "Use deno.", HOther));
        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("y added, x removed");
        var yBlob = BlobOf(rig, Y);
        rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == yBlob ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "bad object") : null;
        // A local memory under y with no row (written after the scan): not a W1 candidate, so the pre-pass does not read y.
        var summary = await PullAllowingDeletions(rig, applier, scan =>
        {
            Assert.Null(Row(rig, Y));
            rig.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "Use deno.", Id: Y));
            return scan;
        });

        Assert.True(summary.Proven);
        Assert.Contains(X, summary.Deleted);
        Assert.False(Has(rig, X));
    }

    [GitFact]
    public async Task ACanonicalThatIsAlsoALocalDeletion_IsReadOnce()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        GitSyncRows.Write(rig.Connection, new GitSyncRow(C, SyncMerge.RowState.Active, RecordJson(Rec(C)), null, null, null, null, null, null, null, null, null, null, null, null));

        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        var cBlob = BlobOf(rig, C);
        var reads = 0;
        rig.Fault = c =>
        {
            if (c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == cBlob)
                reads++;
            return null;
        };
        var summary = await Pull(rig, applier, scan =>
        {
            Assert.Contains(scan.LocalDeletions, d => d.Id == C);
            return scan;
        });

        Assert.Equal(1, reads);
        Assert.True(Has(rig, X));
        Assert.DoesNotContain(summary.Held, h => h.Id == X);
    }

    // ---- the ordinary rules where an alias does not apply ----

    [GitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AHeldDeleteItemOnTheOther_LeavesTheOrdinaryRules(bool proven)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        var item = new HeldItem(X, "remote", null, "unproven", 1_700_000_000_000L);
        GitSyncReviews.UpsertHeld(rig.Connection, item);

        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        if (!proven)
            rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(rig, applier);

        Assert.Equal(proven, summary.Proven);
        Assert.True(Has(rig, X));
        Assert.Equal(item, GitSyncReviews.ReadOpenHeld(rig.Connection).GetValueOrDefault(X));
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
    }

    [GitFact]
    public async Task AnIgnoredIdsNewPinItem_IsRemovedByTheUnchangedReconcile()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        GitSyncReviews.UpsertPinItem(rig.Connection, new PinItem(X, "new-pin", "open", "shown", ApplyRig.OtherMachine, 1_700_000_000_000L));
        Assert.Single(GitSyncReviews.ReadPinItems(rig.Connection), i => i.EntryId == X);

        rig.Remove(SyncValidation.recordPath(X));
        rig.Remove(SyncValidation.recordPath(C));
        rig.Commit("remove both");
        var summary = await Pull(rig, applier);

        Assert.True(Has(rig, X));
        Assert.DoesNotContain(GitSyncReviews.ReadPinItems(rig.Connection), i => i.EntryId == X);
        Assert.Empty(summary.Held);
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
    }

    [GitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InAnUnprovenPull_IgnoredAndScanSkippedAliasIds_OpenNoFallbackHold(bool bothRemoved)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);

        rig.Remove(SyncValidation.recordPath(X));
        if (bothRemoved)
            rig.Remove(SyncValidation.recordPath(C));
        rig.Commit("remove");
        rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(
            rig, applier,
            bothRemoved ? null : scan => scan with { Skipped = [new SkippedItem(X, null, "test")] });

        Assert.False(summary.Proven);
        Assert.Empty(summary.Held);
        Assert.False(HasHeldRow(rig, X));
        Assert.Empty(summary.Deleted);
        if (bothRemoved)
            Assert.True(Has(rig, X));
        else
            Assert.True(Has(rig, X) || Has(rig, C));
    }

    [GitFact]
    public async Task InAnUnprovenPull_AnIgnoredIdWhoseApplyFails_OpensNoFallbackHold()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        ScanRig.Exec(rig.Connection, $"UPDATE git_sync_entry SET base_json = '{{' WHERE entry_id = '{X}'");
        var rowBefore = Row(rig, X);

        rig.Remove(SyncValidation.recordPath(X));
        rig.Remove(SyncValidation.recordPath(C));
        rig.Commit("remove both");
        rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(rig, applier);

        Assert.False(summary.Proven);
        var failed = Assert.Single(summary.Failed);
        Assert.Equal(X, failed.Id);
        Assert.StartsWith("the row's base cannot be read", failed.Detail, StringComparison.Ordinal);
        AssertKept(rig, summary, X, rowBefore);
        Assert.Empty(summary.Held);
        Assert.Equal(0, Scalar(rig, "SELECT count(*) FROM git_sync_review WHERE kind = 'held-delete'"));
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
    }

    // =============================================================================================
    // Convergence (alias D3, D4, D5, D6): the other id is re-keyed onto the canonical id and the canonical record is applied
    // as a first sync, in one transaction.

    private const string X2 = "3f9c2a7e-0000-4000-8000-0000000000b7";
    private const string Y = "3f9c2a7e-0000-4000-8000-0000000000c7";
    private const string K = "3f9c2a7e-0000-4000-8000-0000000000d1";

    private static SyncRecordModule.SyncRecord R(
        string id,
        string text = "Use bun.",
        Tier? tier = null,
        bool sticky = false,
        IEnumerable<string>? tags = null,
        string? project = null,
        long createdAt = 1_700_000_000_000L,
        bool origin = true) =>
        ApplyRig.Rec(
            id, text, tier: tier, sticky: sticky, tags: tags, project: project, createdAt: createdAt,
            originHash: origin ? H : null, writtenBy: ApplyRig.OtherMachine);

    /// <summary>Syncs <paramref name="xRec"/>, then pulls <paramref name="cRec"/> (skipped: the open row <c>c|x|H</c>).</summary>
    private static async Task SyncedRecords(PullRig rig, GitSyncApplier applier, SyncRecordModule.SyncRecord xRec, SyncRecordModule.SyncRecord cRec)
    {
        rig.PutRecord(xRec);
        rig.Commit("x");
        await Pull(rig, applier);
        Assert.True(Has(rig, xRec.Id));
        rig.PutRecord(cRec);
        rig.Commit("c");
        await Pull(rig, applier);
        Assert.False(Has(rig, cRec.Id));
        Assert.Contains($"{cRec.Id}|{xRec.Id}|{H}|open", Aliases(rig));
    }

    private static async Task<ApplySummary> RemoveAndPull(PullRig rig, GitSyncApplier applier, string x, bool proven = true)
    {
        rig.Remove(SyncValidation.recordPath(x));
        rig.Commit("remove x");
        if (!proven)
            rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var summary = await Pull(rig, applier);
        Assert.Equal(proven, summary.Proven);
        return summary;
    }

    private static long Scalar(PullRig rig, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = rig.Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long Vectors(PullRig rig) =>
        Scalar(rig, "SELECT (SELECT count(*) FROM hot_memories_vec) + (SELECT count(*) FROM warm_memories_vec) + (SELECT count(*) FROM cold_memories_vec)");

    private static LocalRead.Present View(PullRig rig, string id) =>
        Assert.IsType<LocalRead.Present>(LocalEntryReader.Read(t => rig.Store.Get(t, ContentType.Memory, id), () => rig.Store.IsSticky(ContentType.Memory, id)));

    /// <summary>The id's conflict item as <c>state|hash|machine</c> and its losers as <c>side|text|stamp|machine</c>; null when none.</summary>
    private static (string Head, IReadOnlyList<string> Losers)? Conflict(PullRig rig, string id) =>
        GitSyncReviews.ReadConflictItem(rig.Connection, id) is { } item
            ? ($"{item.State}|{item.RecordHash}|{item.Machine}",
                GitSyncReviews.ReadConflictLosers(item.PayloadJson).Select(l => $"{SyncConflict.sideName(l.Side)}|{l.Text}|{l.Stamp}|{l.Machine}").ToList())
            : null;

    private static IReadOnlyList<string> LoserTexts(PullRig rig, string id) =>
        GitSyncReviews.ReadConflictItem(rig.Connection, id) is { } item
            ? GitSyncReviews.ReadConflictLosers(item.PayloadJson).Select(l => l.Text).ToList()
            : [];

    private static string ShownHash(string applied, params string[] losers) =>
        SyncConflict.shownHash(Microsoft.FSharp.Core.FSharpOption<string>.Some(applied), Microsoft.FSharp.Collections.ListModule.OfSeq(losers));

    private static IReadOnlyList<PinItem> PinsOf(PullRig rig, string id) =>
        GitSyncReviews.ReadPinItems(rig.Connection).Where(i => i.EntryId == id).ToList();

    private static long ReviewRows(PullRig rig, string id) =>
        Scalar(rig, "SELECT count(*) FROM git_sync_review WHERE entry_id = $id", ("$id", id));

    private static void SeedConflict(PullRig rig, string id, params string[] losers) =>
        GitSyncReviews.UpsertConflictItem(
            rig.Connection,
            new ConflictItem(
                id, "open", ShownHash("seeded", losers), ApplyRig.OtherMachine, 1_700_000_000_000L,
                GitSyncReviews.ConflictPayload(losers.Select(t => new SyncConflict.Loser(SyncMerge.Side.Remote, t, "", ApplyRig.OtherMachine)))),
            keepArrivedAt: false);

    /// <summary>
    /// <paramref name="x"/> converged onto <paramref name="cRec"/>'s id: <paramref name="x"/> is gone from the store, the rows
    /// and <c>import_log</c>; the vectors are as many as before; <c>c</c>'s row is active with the canonical record as its base
    /// and the stored entry's comparison columns; a <c>git-sync</c> log row for <c>c</c>; the alias row <c>c|x|H|resolved</c>;
    /// the pull counted it as updated, deleted and held nothing and (with <paramref name="complete"/>) is complete.
    /// </summary>
    private static void AssertConverged(
        PullRig rig, string x, SyncRecordModule.SyncRecord cRec, ApplySummary s, long vectorsBefore, bool complete = true)
    {
        var c = cRec.Id;
        Assert.False(Has(rig, x));
        Assert.True(Has(rig, c));
        Assert.Equal(vectorsBefore, Vectors(rig));
        Assert.Null(Row(rig, x));
        var row = Assert.IsType<GitSyncRow>(Row(rig, c));
        Assert.True(row.State.IsActive);
        var parsed = ApplyPlanning.ParseBase(c, row.BaseJson!).Parsed;
        Assert.NotNull(parsed);
        Assert.Equal(ApplyRig.Parsed(cRec).Record, parsed!.Record);
        var view = View(rig, c).View;
        Assert.Equal(SyncScan.contentHash(view.Content), row.ScannedContentHash);
        Assert.Equal(SyncScan.tagsHash(view.Tags), row.ScannedTagsHash);
        Assert.Equal(view.Sticky, row.LocalSticky);
        Assert.Equal(view.Tier, row.LocalTier);
        Assert.Equal(0, Scalar(rig, "SELECT count(*) FROM import_log WHERE target_entry_id = $x", ("$x", x)));
        Assert.Equal(1, Scalar(
            rig,
            "SELECT count(*) FROM import_log WHERE source_tool = 'git-sync' AND source_path = $p AND content_hash = $h AND target_entry_id = $c",
            ("$p", "sync:" + c), ("$h", H), ("$c", c)));
        Assert.Contains($"{c}|{x}|{H}|resolved", Aliases(rig));
        Assert.DoesNotContain(Aliases(rig), a => a.StartsWith($"{c}|{x}|", StringComparison.Ordinal) && !a.EndsWith("|resolved", StringComparison.Ordinal));
        Assert.True(s.Updated >= 1);
        Assert.Empty(s.Deleted);
        Assert.Empty(s.Held);
        Assert.False(HasHeldRow(rig, x));
        Assert.False(HasHeldRow(rig, c));
        if (complete)
        {
            Assert.True(s.Complete);
            Assert.Empty(s.Deferred);
        }
    }

    // ---- D4: one test per row ----

    [GitFact]
    public async Task SameContent_AdoptsCreatedAt_AndFilesNothing()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C, createdAt: 1_600_000_000_000L);
        await SyncedRecords(rig, applier, R(X), cRec);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        var view = View(rig, C).View;
        Assert.Equal(1_600_000_000_000L, view.Content.CreatedAtMs);
        Assert.Equal("Use bun.", view.Content.Text);
        Assert.Equal(Tier.Warm, view.Tier);
        Assert.False(view.Sticky);
        Assert.Equal(0, ReviewRows(rig, C));
        Assert.Equal(0, ReviewRows(rig, X));
    }

    [GitTheory]
    [InlineData(true)]
    [InlineData(false)] // criterion 14: an unproven convergence carries every local text, tag, tier and pin too
    public async Task ADifferentText_TakesTheCanonicalText_AndFilesTheLocalTextAsAConflict(bool proven)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C, "Use pnpm.");
        await SyncedRecords(rig, applier, R(X, "Use bun."), cRec);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X, proven);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Equal("Use pnpm.", View(rig, C).View.Content.Text);
        var item = Conflict(rig, C);
        Assert.NotNull(item);
        Assert.Equal($"open|{ShownHash("Use pnpm.", "Use bun.")}|{PullRig.MachineId}", item!.Value.Head);
        Assert.Equal([$"local|Use bun.||{PullRig.MachineId}"], item.Value.Losers);
        Assert.Empty(PinsOf(rig, C));
    }

    [GitTheory]
    [InlineData(true)]
    [InlineData(false)] // criterion 14: an unproven convergence carries every local text, tag, tier and pin too
    public async Task APinnedEntryWithADifferentText_KeepsThePinText_AndHoldsAPinEdit(bool proven)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C, "Use pnpm.");
        await SyncedRecords(rig, applier, R(X, "Use bun."), cRec);
        rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, X);
        rig.Store.SetSticky(ContentType.Memory, X, true);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X, proven);

        AssertConverged(rig, X, cRec, s, vectors);
        var view = View(rig, C).View;
        Assert.Equal("Use bun.", view.Content.Text);
        Assert.True(view.Sticky);
        Assert.Equal(Tier.Hot, view.Tier);
        var pin = Assert.Single(PinsOf(rig, C));
        Assert.Equal("pin-edit", pin.Kind);
        Assert.Equal("open", pin.State);
        Assert.Equal(ApplyPlanning.BaseShownHash(cRec), pin.RecordHash);
        Assert.Null(Conflict(rig, C));
    }

    [GitFact]
    public async Task OnlyProjectOrScopeDiffers_TakesTheRecords_WithNoItem()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C, project: "p-remote");
        await SyncedRecords(rig, applier, R(X, project: "p-local"), cRec);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        var view = View(rig, C).View;
        Assert.Equal("p-remote", view.Content.Project?.Value);
        Assert.Equal("Use bun.", view.Content.Text);
        Assert.Equal(0, ReviewRows(rig, C));
    }

    [GitFact]
    public async Task APinnedEntryAndANonStickyRecord_StaysPinned_AndTheStickyStampIsRestamped()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, X);
        rig.Store.SetSticky(ContentType.Memory, X, true);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.True(View(rig, C).View.Sticky);
        Assert.NotNull(Row(rig, C)!.StickyStamp);
        Assert.Equal(0, ReviewRows(rig, C));
    }

    [GitFact]
    public async Task AStickyRecordAndAnUnpinnedEntry_StaysUnpinned_WithANewPinItem()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C, sticky: true);
        await SyncedRecords(rig, applier, R(X), cRec);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.False(View(rig, C).View.Sticky);
        var pin = Assert.Single(PinsOf(rig, C));
        Assert.Equal("new-pin", pin.Kind);
        Assert.Equal("open", pin.State);
    }

    [GitTheory]
    [InlineData(true)]
    [InlineData(false)] // criterion 14: an unproven convergence carries every local text, tag, tier and pin too
    public async Task TheTags_AreTheUnion(bool proven)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C, tags: ["t-remote"]);
        await SyncedRecords(rig, applier, R(X, tags: ["t-local"]), cRec);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X, proven);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Equal(["t-local", "t-remote"], View(rig, C).View.Tags.ToList());
    }

    [GitTheory]
    [InlineData("hot", "hot", true)]
    [InlineData("cold", "warm", false)]
    public async Task TheWarmerTierWins_AndALocalWinnerIsRestamped(string local, string expected, bool restamped)
    {
        var localTier = local == "hot" ? Tier.Hot : Tier.Cold;
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C, tier: Tier.Warm);
        await SyncedRecords(rig, applier, R(X, tier: localTier), cRec);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Equal(expected == "hot" ? Tier.Hot : Tier.Warm, View(rig, C).View.Tier);
        Assert.Equal(restamped, Row(rig, C)!.TierStamp is not null);
    }

    [GitFact]
    public async Task APendingLocalTextEdit_IsFiledAsAConflict_NotWrittenBack()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        rig.Store.Update(Tier.Warm, ContentType.Memory, X, new UpdateEntryOpts { Content = "Use yarn." });
        Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(rig.Connection, () => Now).Scan());
        Assert.NotNull(Row(rig, X)!.ContentStamp);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Equal("Use bun.", View(rig, C).View.Content.Text);
        Assert.Equal(["Use yarn."], LoserTexts(rig, C));
        Assert.Empty(s.Notes);
        Assert.Null(Row(rig, C)!.ContentStamp);
    }

    // ---- order and pulls ----

    [GitFact]
    public async Task TheCanonicalAddAndTheRemoval_InOnePull_Converge()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(R(X));
        rig.Commit("x");
        await Pull(rig, applier);
        var vectors = Vectors(rig);

        var cRec = R(C);
        rig.PutRecord(cRec);
        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("c added, x removed");
        var s = await Pull(rig, applier);

        AssertConverged(rig, X, cRec, s, vectors);
    }

    [GitFact]
    public async Task TheAddThenTheRemoval_InTwoPulls_EndTheSame()
    {
        var (one, applierOne) = NewPull();
        using var rigOne = one;
        one.PutRecord(R(X, "Use bun."));
        one.Commit("x");
        await Pull(one, applierOne);
        one.PutRecord(R(C, "Use pnpm."));
        one.Remove(SyncValidation.recordPath(X));
        one.Commit("c added, x removed");
        await Pull(one, applierOne);

        var (two, applierTwo) = NewPull();
        using var rigTwo = two;
        await SyncedRecords(two, applierTwo, R(X, "Use bun."), R(C, "Use pnpm."));
        await RemoveAndPull(two, applierTwo, X);

        Assert.False(Has(one, X));
        Assert.True(Has(one, C));
        Assert.Equal(Snapshot(one), Snapshot(two));
    }

    [GitFact]
    public async Task TheCanonicalPresent_ConvergesInALaterPull()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        await Pull(rig, applier); // nothing new: x stays
        Assert.True(Has(rig, X));
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
    }

    [GitFact]
    public async Task ACanonicalVisitDeferredByARacingImporter_StillConvergesTheOther()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(R(X));
        rig.Commit("x");
        await Pull(rig, applier);
        var vectors = Vectors(rig);

        var cRec = R(C);
        rig.PutRecord(cRec);
        rig.Remove(SyncValidation.recordPath(X));
        var head = rig.Commit("c added, x removed");
        var seeded = false;
        applier.BeforeTransaction = id =>
        {
            if (id == C && !seeded)
            {
                seeded = true;
                AliasTestSupport.SeedLegacy(rig.Store, rig.Connection, Y, "Use bun, again.", H);
            }
        };
        var s = await Pull(rig, applier);

        Assert.True(seeded);
        Assert.Contains(s.Deferred, d => d.Id == C);
        Assert.False(s.Complete);
        AssertConverged(rig, X, cRec, s, vectors, complete: false); // Y is seeded with no vector
        Assert.NotEqual(head, rig.Last);

        applier.BeforeTransaction = null;
        var next = await Pull(rig, applier);
        Assert.True(next.Complete);
        Assert.Equal(head, rig.Last);
        Assert.True(Has(rig, C));
        Assert.False(Has(rig, X));
    }

    [GitFact]
    public async Task TheTwoLocalMembers_TheLowestConverges_TheOtherIsIgnored()
    {
        var (rig, applier) = await TwoLocalMembers();
        using var pullRig = rig;
        var rowBefore = Row(rig, X2);
        var vectors = Vectors(rig);

        rig.Remove(SyncValidation.recordPath(X));
        rig.Remove(SyncValidation.recordPath(X2));
        rig.Commit("remove both");
        var s = await Pull(rig, applier);

        AssertConverged(rig, X, R(C), s, vectors);
        AssertKept(rig, s, X2, rowBefore);
        Assert.Contains($"{C}|{X2}|{H}|open", Aliases(rig));
    }

    [GitFact]
    public async Task TheTwoLocalMembers_TheLowestConverges_TheOtherIsIgnored_AlsoWhenTheLowestDefers()
    {
        var (rig, applier) = await TwoLocalMembers();
        using var pullRig = rig;
        var rowBefore1 = Row(rig, X);
        var rowBefore2 = Row(rig, X2);
        applier.BeforeTransaction = id =>
        {
            if (id == X)
                rig.Store.Update(Tier.Warm, ContentType.Memory, X, new UpdateEntryOpts { Content = "Changed meanwhile." });
        };

        rig.Remove(SyncValidation.recordPath(X));
        rig.Remove(SyncValidation.recordPath(X2));
        rig.Commit("remove both");
        var s = await Pull(rig, applier);

        Assert.Contains(s.Deferred, d => d.Id == X);
        AssertKept(rig, s, X, rowBefore1);
        AssertKept(rig, s, X2, rowBefore2);
        Assert.False(Has(rig, C));
        Assert.Empty(s.Held);
        Assert.Equal([$"{C}|{X}|{H}|open", $"{C}|{X2}|{H}|open"], Aliases(rig));
    }

    /// <summary>
    /// Two local members <c>X &lt; X2</c> of one group with the canonical <c>C</c>: their records are synced without an origin,
    /// then host <c>import_log</c> rows of <see cref="H"/> are added for both, then <c>C</c>'s record is pulled (skipped).
    /// </summary>
    private static async Task<(PullRig Rig, GitSyncApplier Applier)> TwoLocalMembers()
    {
        var (rig, applier) = NewPull();
        rig.PutRecord(R(X, origin: false));
        rig.PutRecord(R(X2, origin: false));
        rig.Commit("x1, x2");
        await Pull(rig, applier);
        foreach (var id in new[] { X, X2 })
            new ImportLog(rig.Connection).LogImport("claude-code", "/home/u/.claude/memory/" + id + ".md", H, id, Tier.Warm, ContentType.Memory);
        rig.PutRecord(R(C));
        rig.Commit("c");
        await Pull(rig, applier);
        Assert.False(Has(rig, C));
        Assert.Equal([$"{C}|{X}|{H}|open", $"{C}|{X2}|{H}|open"], Aliases(rig));
        return (rig, applier);
    }

    [GitFact]
    public async Task ACanonicalVisitDeferredInThePullWhereXConverges_StillOpensTheOtherMembersRow()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(R(X, origin: false));
        rig.PutRecord(R(X2, origin: false));
        rig.Commit("x1, x2");
        await Pull(rig, applier);
        foreach (var id in new[] { X, X2 })
            new ImportLog(rig.Connection).LogImport("claude-code", "/home/u/.claude/memory/" + id + ".md", H, id, Tier.Warm, ContentType.Memory);
        var rowBefore = Row(rig, X2);

        var cRec = R(C);
        rig.PutRecord(cRec);
        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("c added, x removed");
        var seeded = false;
        applier.BeforeTransaction = id =>
        {
            if (id == C && !seeded)
            {
                seeded = true;
                AliasTestSupport.SeedLegacy(rig.Store, rig.Connection, Y, "Use bun, again.", H);
            }
        };
        var s = await Pull(rig, applier);
        applier.BeforeTransaction = null;

        Assert.True(seeded);
        Assert.Contains(s.Deferred, d => d.Id == C);
        Assert.False(Has(rig, X));
        Assert.True(Has(rig, C));
        Assert.Contains($"{C}|{X}|{H}|resolved", Aliases(rig));
        Assert.Contains($"{C}|{X2}|{H}|open", Aliases(rig));
        await Pull(rig, applier);

        // X2's record disappears later: the open row keeps it (C is taken here, so X2 is ignored), never deleted.
        var later = await RemoveAndPull(rig, applier, X2);
        AssertKept(rig, later, X2, rowBefore);
        Assert.True(Has(rig, C));
        Assert.Contains($"{C}|{X2}|{H}|open", Aliases(rig));
    }

    [GitFact]
    public async Task TheLowestCandidateNotParsing_ConvergesOntoAHigherOne()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(R(X));
        rig.Commit("x");
        await Pull(rig, applier);
        var bRec = R(B);
        rig.PutRecord(bRec);
        rig.Commit("b");
        await Pull(rig, applier);
        rig.PutRecord(R(A));
        rig.Commit("a");
        await Pull(rig, applier);
        Assert.Equal([$"{A}|{X}|{H}|open", $"{B}|{X}|{H}|open"], Aliases(rig));
        var vectors = Vectors(rig);

        rig.PutText(SyncValidation.recordPath(A), "{not json");
        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("a broken, x removed");
        var s = await Pull(rig, applier);

        AssertConverged(rig, X, bRec, s, vectors);
        Assert.Contains($"{A}|{X}|{H}|open", Aliases(rig));
        Assert.False(Has(rig, A));
    }

    [GitFact]
    public async Task ThreeMembersOneAtATime_ConvergeOntoTheLowestParsingCandidate()
    {
        // B is the local member; C then the lower A arrive: rows C|B then A|B.
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(R(B));
        rig.Commit("b");
        await Pull(rig, applier);
        rig.PutRecord(R(C));
        rig.Commit("c");
        await Pull(rig, applier);
        var aRec = R(A);
        rig.PutRecord(aRec);
        rig.Commit("a");
        await Pull(rig, applier);
        Assert.Equal([$"{A}|{B}|{H}|open", $"{C}|{B}|{H}|open"], Aliases(rig));
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, B);

        AssertConverged(rig, B, aRec, s, vectors);
        Assert.Contains($"{C}|{B}|{H}|open", Aliases(rig));
        Assert.False(Has(rig, C));
    }

    // ---- what the other id leaves ----

    [GitFact]
    public async Task AnEarlierApplyFailedIssueOfTheOther_IsRemoved()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        GitSyncIssues.Upsert(rig.Connection, X, GitSyncIssues.ApplyFailed, null, 1, "an earlier failure");
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Equal(0, Scalar(rig, "SELECT count(*) FROM git_sync_issue WHERE entry_id IN ($x, $c)", ("$x", X), ("$c", C)));
    }

    [GitFact]
    public async Task TheAllowedHash_IsCarried()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        ScanRig.Exec(rig.Connection, $"UPDATE git_sync_entry SET allowed_hash = 'abc' WHERE entry_id = '{X}'");
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        var row = Row(rig, C)!;
        Assert.Equal("abc", row.AllowedHash);
        Assert.Null(row.BlockedHash);
        Assert.Null(row.BlockedReason);
    }

    [GitFact]
    public async Task TheOthersConflictItem_IsReKeyed_AndALoserEqualToTheAppliedTextIsDropped()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        SeedConflict(rig, X, "Use bun.", "Old loser.");
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        var item = Conflict(rig, C);
        Assert.NotNull(item);
        Assert.Equal($"unseen|{ShownHash("Use bun.", "Old loser.")}|{ApplyRig.OtherMachine}", item!.Value.Head);
        Assert.Equal(["Old loser."], LoserTexts(rig, C));
        Assert.Null(Conflict(rig, X));
    }

    [GitFact]
    public async Task TheOthersConflictItem_LeftWithNoLoser_IsRemoved()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        SeedConflict(rig, X, "Use bun.");
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Null(Conflict(rig, C));
        Assert.Null(Conflict(rig, X));
        Assert.Equal(0, ReviewRows(rig, C) + ReviewRows(rig, X));
    }

    [GitFact]
    public async Task EightOldLosersAndADifferingLocalText_KeepTheNewestLocalText_AndDropTheOldest()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C, "Use pnpm.");
        await SyncedRecords(rig, applier, R(X, "Use bun."), cRec);
        var old = Enumerable.Range(1, 8).Select(i => $"l{i}").ToArray();
        SeedConflict(rig, X, old);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Equal([.. old.Skip(1), "Use bun."], LoserTexts(rig, C));
        Assert.Equal(ShownHash("Use pnpm.", [.. old.Skip(1), "Use bun."]), GitSyncReviews.ReadConflictItem(rig.Connection, C)!.RecordHash);
    }

    [GitFact]
    public async Task D4sArrival_IsDedupedAgainstTheOthersReKeyedLosers()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C, "Use pnpm.");
        await SyncedRecords(rig, applier, R(X, "Use bun."), cRec);
        SeedConflict(rig, X, "Old loser.", "Use bun.");
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Equal(["Old loser.", "Use bun."], LoserTexts(rig, C));
        Assert.Equal(ShownHash("Use pnpm.", "Old loser.", "Use bun."), GitSyncReviews.ReadConflictItem(rig.Connection, C)!.RecordHash);
    }

    [GitFact]
    public async Task TheOthersPinItems_AreRemoved()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        GitSyncReviews.UpsertPinItem(rig.Connection, new PinItem(X, "new-pin", "approved", "shown", ApplyRig.OtherMachine, 1_700_000_000_000L));
        GitSyncReviews.UpsertPinItem(rig.Connection, new PinItem(X, "pin-edit", "open", "shown", ApplyRig.OtherMachine, 1_700_000_000_000L));
        Assert.Equal(2, PinsOf(rig, X).Count);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Empty(PinsOf(rig, X));
        Assert.Empty(PinsOf(rig, C));
        Assert.Equal(0, ReviewRows(rig, X));
    }

    [GitFact]
    public async Task ParentIdAccessCountAndVector_FollowTheReKey()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        rig.Store.Insert(Tier.Cold, ContentType.Memory, new InsertEntryOpts(Content: "A child.", Id: K, ParentId: X));
        ScanRig.Exec(rig.Connection, $"UPDATE warm_memories SET access_count = 5 WHERE id = '{X}'");
        var vectorBefore = VectorBytes(rig, X);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X);

        AssertConverged(rig, X, cRec, s, vectors);
        Assert.Equal(C, rig.Store.Get(Tier.Cold, ContentType.Memory, K)!.ParentId?.Value);
        Assert.Equal(5, Scalar(rig, "SELECT access_count FROM warm_memories WHERE id = $c", ("$c", C)));
        Assert.Equal(vectorBefore, VectorBytes(rig, C));
    }

    private static byte[] VectorBytes(PullRig rig, string id)
    {
        var rowid = Scalar(rig, "SELECT rowid FROM warm_memories WHERE id = $id", ("$id", id));
        using var cmd = rig.Connection.CreateCommand();
        cmd.CommandText = "SELECT embedding FROM warm_memories_vec WHERE rowid = $r";
        cmd.Parameters.AddWithValue("$r", rowid);
        return (byte[])cmd.ExecuteScalar()!;
    }

    // ---- unproven pulls, faults and deferrals ----

    [GitFact]
    public async Task AnUnprovenPull_Converges()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        var vectors = Vectors(rig);

        var s = await RemoveAndPull(rig, applier, X, proven: false);

        AssertConverged(rig, X, cRec, s, vectors);
    }

    [GitFact]
    public async Task InAnUnprovenPull_AFailedConvergence_OpensNoFallbackHold_AndTheNextPullConverges()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        var cRec = R(C);
        await SyncedRecords(rig, applier, R(X), cRec);
        var rowBefore = Row(rig, X);
        var vectors = Vectors(rig);
        var fault = true;
        applier.BeforeEntryStep = (id, step) =>
        {
            if (fault && id == X && step == "converge")
                throw new InvalidOperationException("synthetic fault");
        };

        var s = await RemoveAndPull(rig, applier, X, proven: false);

        Assert.Equal(X, Assert.Single(s.Failed).Id);
        Assert.Empty(s.Held);
        AssertKept(rig, s, X, rowBefore);
        Assert.False(Has(rig, C));
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));

        fault = false;
        var next = await Pull(rig, applier);

        AssertConverged(rig, X, cRec, next, vectors);
    }

    [GitFact]
    public async Task AFaultAfterTheReKey_RollsEverythingBack()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedRecords(rig, applier, R(X, "Use bun."), R(C, "Use pnpm."));
        SeedConflict(rig, X, "Old loser.");
        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        var before = Dump(rig);
        var vectorBefore = VectorBytes(rig, X);
        applier.BeforeEntryStep = (id, step) =>
        {
            if (id == X && step == "converge")
                throw new InvalidOperationException("synthetic fault");
        };

        var s = await Pull(rig, applier);

        Assert.Equal(X, Assert.Single(s.Failed).Id);
        Assert.False(Has(rig, C));
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
        Assert.Equal(vectorBefore, VectorBytes(rig, X));
        Assert.Equal(X, FtsId(rig, "bun"));
        Assert.Single(GitSyncIssues.ReadOpen(rig.Connection, GitSyncIssues.ApplyFailed).Keys, k => k == X);
        GitSyncIssues.Delete(rig.Connection, X, GitSyncIssues.ApplyFailed);
        Assert.Equal(before, Dump(rig));
    }

    private static string? FtsId(PullRig rig, string word)
    {
        using var cmd = rig.Connection.CreateCommand();
        cmd.CommandText = "SELECT t.id FROM warm_memories_fts f JOIN warm_memories t ON t.rowid = f.rowid WHERE warm_memories_fts MATCH $w";
        cmd.Parameters.AddWithValue("$w", word);
        return cmd.ExecuteScalar() as string;
    }

    [GitFact]
    public async Task AChangedEntryBeforeTheTransaction_DefersTheConvergence()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedRecords(rig, applier, R(X), R(C));
        var rowBefore = Row(rig, X);
        applier.BeforeTransaction = id =>
        {
            if (id == X)
                rig.Store.Update(Tier.Warm, ContentType.Memory, X, new UpdateEntryOpts { Content = "Changed meanwhile." });
        };

        var s = await RemoveAndPull(rig, applier, X);

        Assert.Contains(s.Deferred, d => d.Id == X);
        Assert.False(s.Complete);
        AssertKept(rig, s, X, rowBefore);
        Assert.Equal("Changed meanwhile.", View(rig, X).View.Content.Text);
        Assert.False(Has(rig, C));
        Assert.Null(Row(rig, C));
        Assert.Equal(0, ReviewRows(rig, C));
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
        Assert.Equal(0, Scalar(rig, "SELECT count(*) FROM import_log WHERE target_entry_id = $c", ("$c", C)));
        Assert.Equal(0, s.Updated);
    }

    [GitFact]
    public async Task TheReappearanceOfTheOthersRecord_IsSkippedAsAnAlias()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedRecords(rig, applier, R(X), R(C));
        await RemoveAndPull(rig, applier, X);
        Assert.False(Has(rig, X));

        rig.PutRecord(R(X));
        rig.Commit("x again");
        var s = await Pull(rig, applier);

        Assert.False(Has(rig, X));
        Assert.Null(Row(rig, X));
        Assert.True(Has(rig, C));
        Assert.Equal(0, s.Inserted);
        Assert.True(s.Unchanged >= 1);
        Assert.True(s.Complete);
        Assert.Equal([$"{C}|{X}|{H}|resolved"], Aliases(rig));
    }

    [GitFact]
    public async Task AfterConvergence_ARepeatedPullIsANoOp()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedRecords(rig, applier, R(X), R(C));
        var converged = await RemoveAndPull(rig, applier, X);
        Assert.True(converged.Complete);
        var before = Dump(rig);
        var transactions = applier.TransactionCount;

        var s = await Pull(rig, applier);

        Assert.True(s.Complete);
        Assert.Empty(s.Deferred);
        Assert.Equal(transactions, applier.TransactionCount);
        Assert.Equal(before, Dump(rig));
    }

    /// <summary>
    /// An id whose canonical id is taken or cannot be read is ignored in one go: not routed to a convergence that then
    /// defers or fails on every pull (the pull is complete, nothing deferred or failed), and the convergence changes nothing
    /// under the canonical id.
    /// </summary>
    [GitTheory]
    [InlineData("memory")]
    [InlineData("knowledge")]
    [InlineData("forgotten")]
    [InlineData("pending-deletion")]
    [InlineData("held-delete")]
    [InlineData("unreadable-knowledge")]
    public async Task ATakenCanonicalId_IgnoresTheOther_WithNoDeferralAndNoFailure(string what)
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedPair(rig, applier, X, C);
        var rowBefore = Row(rig, X);
        var baseJson = RecordJson(Rec(C));
        switch (what)
        {
            case "memory":
                rig.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "Taken.", Id: C));
                break;
            case "knowledge":
                rig.Store.Insert(Tier.Warm, ContentType.Knowledge, new InsertEntryOpts(Content: "Taken.", Id: C));
                break;
            case "forgotten":
                GitSyncRows.Write(rig.Connection, new GitSyncRow(C, SyncMerge.RowState.Forgotten, baseJson, null, null, null, null, null, null, null, null, null, null, null, null));
                break;
            case "pending-deletion":
                GitSyncRows.Write(rig.Connection, new GitSyncRow(C, SyncMerge.RowState.Active, baseJson, null, null, null, null, null, null, null, null, null, null, null, null));
                break;
            case "held-delete":
                GitSyncReviews.UpsertHeld(rig.Connection, new HeldItem(C, "local", null, "unproven", 1_700_000_000_000L));
                break;
            default:
                InsertCorruptKnowledge(rig, C);
                break;
        }
        var cRow = Row(rig, C);

        var s = await RemoveAndPull(rig, applier, X);

        AssertKept(rig, s, X, rowBefore);
        Assert.Empty(s.Deferred);
        Assert.Empty(s.Failed);
        Assert.True(s.Complete);
        if (what != "memory") // a local memory under C meets its own record by the ordinary first-sync merge
            Assert.Equal(cRow, Row(rig, C));
        Assert.Equal([$"{C}|{X}|{H}|open"], Aliases(rig));
    }

    // ---- the follow-up pulls of the pre-pass faults ----

    [GitFact]
    public async Task AFailedCandidateBlobRead_TheOtherConvergesOnTheNextPull()
    {
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        await SyncedAlone(rig, applier, X);
        var vectors = Vectors(rig);

        var cRec = Rec(C);
        rig.PutRecord(cRec);
        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("c added, x removed");
        var cBlob = BlobOf(rig, C);
        rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == cBlob ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "bad object") : null;
        var first = await Pull(rig, applier);
        Assert.True(Has(rig, X));
        Assert.Equal(C, Assert.Single(first.Failed).Id);

        rig.Fault = _ => null;
        var next = await Pull(rig, applier);

        AssertConverged(rig, X, cRec, next, vectors);
    }

    [GitTheory]
    [InlineData("other")]
    [InlineData("cancelled")]
    public async Task AFailedBlobReadOfTheLowestCandidate_XConvergesOntoTheLowestOnTheNextPull(string kindName)
    {
        var kind = kindName == "other" ? SyncGit.FailureKind.Other : SyncGit.FailureKind.Cancelled;
        var (rig, applier) = NewPull();
        using var pullRig = rig;
        rig.PutRecord(Rec(X));
        rig.Commit("x");
        await Pull(rig, applier);
        rig.PutRecord(Rec(B));
        rig.Commit("b");
        await Pull(rig, applier);
        var aRec = Rec(A);
        rig.PutRecord(aRec);
        rig.Commit("a");
        await Pull(rig, applier);
        var vectors = Vectors(rig);

        rig.Remove(SyncValidation.recordPath(X));
        rig.Commit("remove x");
        var aBlob = BlobOf(rig, A);
        rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == aBlob ? FaultingGitProcess.Failure(kind, "bad object") : null;
        await Pull(rig, applier);
        Assert.True(Has(rig, X));
        Assert.False(Has(rig, B));

        rig.Fault = _ => null;
        var next = await Pull(rig, applier);

        AssertConverged(rig, X, aRec, next, vectors);
        Assert.Contains($"{A}|{X}|{H}|resolved", Aliases(rig));
        Assert.Contains($"{B}|{X}|{H}|open", Aliases(rig));
        Assert.False(Has(rig, B));
    }
}
