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
/// closed: a blob or knowledge read it cannot make keeps every local id that could have converged. Until convergence itself
/// is carried out (a later task), a converging id is routed through the ignored path, so these tests assert only what holds
/// either way: never deleted, never held, never counted, and <c>Has(x) || Has(canonical)</c> where a canonical is present.
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
        Assert.Empty(summary.Deleted);
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
    public async Task ACorruptKnowledgeRowUnderTheArrivingCandidate_KeepsTheOther_NotDeletedNotHeld(bool proven)
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
        AssertKept(rig, summary, X, rowBefore);
        Assert.Empty(summary.Held);
        Assert.Empty(Aliases(rig));
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
}
