using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncApplierSafetyTests
{
    private const string Id = "3f9c2a7e-0000-4000-8000-000000000001";

    private static T Is<T>(EntryResult r) where T : EntryResult => Assert.IsType<T>(r);

    /// <summary>A scenario: a rig set up to the point just before the apply, and the apply's arguments.</summary>
    private sealed record Scenario(ApplyRig Rig, string Id, SyncRecordModule.SyncRecord? Record) : IDisposable
    {
        public void Dispose() => Rig.Dispose();
    }

    private static Scenario Insert()
    {
        var rig = new ApplyRig();
        return new Scenario(rig, Id, ApplyRig.Rec(Id, tags: ["a"], project: "o/r", source: SourceTool.Cursor));
    }

    private static Scenario TextAndTags()
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, tags: ["a"], minute: 10));
        return new Scenario(rig, Id, ApplyRig.Rec(Id, content: "Newer text.", tags: ["a", "b"], minute: 20));
    }

    private static Scenario PromotionWithText()
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, tier: Tier.Warm, minute: 10));
        return new Scenario(rig, Id, ApplyRig.Rec(Id, content: "Newer text.", tier: Tier.Hot, minute: 20, tierMinute: 20));
    }

    private static Scenario PromotionOnly()
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, tier: Tier.Warm, minute: 10));
        return new Scenario(rig, Id, ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10, tierMinute: 20));
    }

    private static Scenario Unpin()
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10));
        rig.Scan.Pin(Id);
        rig.Now = 1_780_000_000_000L;
        rig.RunScan();
        rig.Now = 1_800_000_000_000L;
        return new Scenario(rig, Id, ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10, stickyMinute: 59));
    }

    private static Scenario Drop()
    {
        var rig = new ApplyRig();
        rig.Scan.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "x", Id: Id));
        rig.RunScan();
        rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        return new Scenario(rig, Id, null);
    }

    public static IEnumerable<object[]> Scenarios() =>
        new[] { nameof(Insert), nameof(TextAndTags), nameof(PromotionWithText), nameof(PromotionOnly), nameof(Unpin), nameof(Drop) }
            .Select(n => new object[] { n });

    private static Scenario Build(string name) => name switch
    {
        nameof(Insert) => Insert(),
        nameof(TextAndTags) => TextAndTags(),
        nameof(PromotionWithText) => PromotionWithText(),
        nameof(PromotionOnly) => PromotionOnly(),
        nameof(Unpin) => Unpin(),
        _ => Drop(),
    };

    private static EntryResult Run(Scenario s) =>
        s.Rig.Applier.ApplyOne(s.Id, s.Record is null ? null : ApplyRig.Parsed(s.Record), proven: true, s.Rig.Held(s.Id), blobSha: "abc");

    // ---- the deletion decisions (milestone 1b-ii) ----

    /// <summary>A synced entry whose record is gone from the repository: a proven remote deletion.</summary>
    private static Scenario RemoteDelete()
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, tags: ["a"], minute: 10));
        return new Scenario(rig, Id, null);
    }

    /// <summary>A pinned entry whose record is gone: held with cause <c>sticky</c>.</summary>
    private static Scenario RemoteHold()
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10));
        rig.Scan.Pin(Id);
        rig.RunScan();
        return new Scenario(rig, Id, null);
    }

    /// <summary>An open remote item whose record reappears unchanged: the item is closed.</summary>
    private static Scenario CloseOnly()
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, minute: 10));
        rig.SeedHeld(Id);
        return new Scenario(rig, Id, ApplyRig.Rec(Id, minute: 10));
    }

    /// <summary>A pending text edit and an open remote item, the record gone: a fresh content stamp and a close.</summary>
    private static Scenario FreshStamp()
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, minute: 10));
        rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new UpdateEntryOpts { Content = "A local edit." });
        rig.RunScan();
        rig.SeedHeld(Id);
        return new Scenario(rig, Id, null);
    }

    public static IEnumerable<object[]> DeletionScenarios() =>
        new[] { nameof(RemoteDelete), nameof(RemoteHold), nameof(CloseOnly), nameof(FreshStamp) }.Select(n => new object[] { n });

    private static Scenario BuildDeletion(string name) => name switch
    {
        nameof(RemoteDelete) => RemoteDelete(),
        nameof(RemoteHold) => RemoteHold(),
        nameof(CloseOnly) => CloseOnly(),
        _ => FreshStamp(),
    };

    [Theory]
    [MemberData(nameof(DeletionScenarios))]
    public void HeldAndDeletionDecisions_FaultAtEveryStep_RollBackEverything(string scenario)
    {
        var steps = new List<string>();
        using (var probe = BuildDeletion(scenario))
        {
            probe.Rig.Applier.BeforeEntryStep = (_, step) => steps.Add(step);
            Is<EntryResult.Carried>(Run(probe));
        }
        Assert.Contains("commit", steps);
        var expected = scenario switch
        {
            nameof(RemoteDelete) => new[] { "delete-vec", "delete-row" },
            nameof(RemoteHold) => ["hold"],
            nameof(CloseOnly) => ["close"],
            _ => ["close", "row"],
        };
        Assert.All(expected, step => Assert.Contains(step, steps));

        foreach (var step in steps.Distinct())
        {
            using var s = BuildDeletion(scenario);
            var before = s.Rig.Full(issues: false); // content, vectors, import log, rows, held items, clock
            var beforeIssues = s.Rig.Scan.Issues();
            s.Rig.Applier.BeforeEntryStep = (_, current) =>
            {
                if (current == step)
                    throw new InvalidOperationException("fault at " + step);
            };

            var result = Run(s);

            Assert.Equal(before, s.Rig.Full(issues: false));
            if (step is "reread" or "issue")
            {
                Assert.True(result is EntryResult.Deferred, $"{scenario}/{step}: {result}");
                Assert.Equal(beforeIssues, s.Rig.Scan.Issues());
            }
            else
            {
                Assert.True(result is EntryResult.Failed { Attempts: 1 }, $"{scenario}/{step}: {result}");
                var issue = Assert.Single(s.Rig.Scan.Issues());
                Assert.Equal((Id, "apply-failed"), (issue.Id, issue.Kind));
                Assert.Contains("fault at " + step, issue.Detail);
            }
        }
    }

    [Fact]
    public void AHeldItemChangedAfterTheDecision_IsDeferred_AndNothingChanges()
    {
        using var s = CloseOnly();
        string? after = null;
        s.Rig.Applier.BeforeTransaction = _ =>
        {
            var (conn, _) = s.Rig.Scan.Other();
            using (conn)
                GitSyncReviews.UpsertHeld(conn, new HeldItem(Id, "remote", null, "guard", 5));
            after = s.Rig.Full();
        };

        Assert.IsType<EntryResult.Deferred>(Run(s));
        Assert.Equal(after, s.Rig.Full());
    }

    // ---- a fault at every step rolls everything back ----

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void AFaultAtEachStep_RollsBackEverything_AndANonDeferredFaultWritesTheFailure(string scenario)
    {
        var steps = new List<string>();
        using (var probe = Build(scenario))
        {
            probe.Rig.Applier.BeforeEntryStep = (_, step) => steps.Add(step);
            Is<EntryResult.Carried>(Run(probe));
        }
        Assert.Contains("commit", steps);

        foreach (var step in steps.Distinct())
        {
            using var s = Build(scenario);
            var before = s.Rig.Full(issues: false);
            var beforeIssues = s.Rig.Scan.Issues();
            s.Rig.Applier.BeforeEntryStep = (_, current) =>
            {
                if (current == step)
                    throw new InvalidOperationException("fault at " + step);
            };

            var result = Run(s);

            if (step is "reread" or "issue")
            {
                Assert.True(result is EntryResult.Deferred, $"{scenario}/{step}: {result}");
                Assert.Equal(before, s.Rig.Full(issues: false));
                Assert.Equal(beforeIssues, s.Rig.Scan.Issues());
            }
            else
            {
                var failed = Assert.IsType<EntryResult.Failed>(result);
                Assert.Equal(1, failed.Attempts);
                Assert.Equal(before, s.Rig.Full(issues: false)); // store, vectors, import log, rows, clock: exactly as before
                var issue = Assert.Single(s.Rig.Scan.Issues());
                Assert.Equal((Id, "apply-failed"), (issue.Id, issue.Kind));
                Assert.Contains("fault at " + step, issue.Detail);
            }
        }
    }

    [Fact]
    public void AnEarlierFailureRow_SurvivesAnyRollback_AndIsRemovedByACarriedOutEntry()
    {
        using var s = TextAndTags();
        s.Rig.Embedder.Fails = _ => true;
        Is<EntryResult.Failed>(Run(s)); // attempts 1, hash "abc"
        s.Rig.Embedder.Fails = _ => false;
        s.Rig.Applier.BeforeEntryStep = (_, step) => { if (step == "row") throw new InvalidOperationException("boom"); };

        var second = Is<EntryResult.Failed>(Run(s));

        Assert.Equal(2, second.Attempts);
        Assert.Contains("boom", Assert.Single(s.Rig.Scan.Issues()).Detail);

        s.Rig.Applier.BeforeEntryStep = null;
        Is<EntryResult.Carried>(Run(s));
        Assert.Empty(s.Rig.Scan.Issues());
    }

    [Fact]
    public void AFailureRow_HoldsTheBlobId_OneLineOfAtMost200Characters_AndCountsAttempts()
    {
        using var s = TextAndTags();
        s.Rig.Embedder.Fails = _ => throw new InvalidOperationException("line one\nline two\t" + new string('x', 500));

        Is<EntryResult.Failed>(Run(s));
        Is<EntryResult.Failed>(Run(s));

        using var cmd = s.Rig.Scan.Connection.CreateCommand();
        cmd.CommandText = "SELECT record_hash, attempts, detail FROM git_sync_issue WHERE kind = 'apply-failed'";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("abc", r.GetString(0));
        Assert.Equal(2, r.GetInt32(1));
        var detail = r.GetString(2);
        Assert.True(detail.Length <= 200);
        Assert.DoesNotContain('\n', detail);
        Assert.DoesNotContain('\t', detail);
    }

    // ---- an edit after the scan is never overwritten ----

    private static void EditElsewhere(ApplyRig rig, Action<SqliteStore> edit)
    {
        var (conn, store) = rig.Scan.Other();
        using (conn)
            edit(store);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("pin")]
    [InlineData("move")]
    public void AnEditAfterTheScan_IsDeferred_AndNeverOverwritten(string edit)
    {
        using var s = PromotionWithText();
        string? after = null;
        s.Rig.Applier.BeforeTransaction = _ =>
        {
            EditElsewhere(s.Rig, store =>
            {
                switch (edit)
                {
                    case "text": store.Update(Tier.Warm, ContentType.Memory, Id, new UpdateEntryOpts { Content = "A local edit." }); break;
                    case "pin": store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, Id); store.SetSticky(ContentType.Memory, Id, true); break;
                    default: store.Move(Tier.Warm, ContentType.Memory, Tier.Cold, ContentType.Memory, Id); break;
                }
            });
            after = s.Rig.Full();
        };

        var result = Run(s);

        Assert.IsType<EntryResult.Deferred>(result);
        Assert.Equal(after, s.Rig.Full());
        Assert.Empty(s.Rig.Scan.Issues());
        Assert.NotEqual("Newer text.", s.Rig.Entry(Id)!.Row.Content);
    }

    [Fact]
    public void ARowChangedAfterTheDecision_IsDeferred()
    {
        using var s = TextAndTags();
        s.Rig.Applier.BeforeTransaction = _ =>
        {
            var (conn, _) = s.Rig.Scan.Other();
            using (conn)
                ScanRig.Exec(conn, $"UPDATE git_sync_entry SET content_stamp = '2026-11-12T09:30:00.000Z.000000.7b3d1e55' WHERE entry_id = '{Id}'");
        };

        Assert.IsType<EntryResult.Deferred>(Run(s));
        Assert.Equal("Use bun, not npm.", s.Rig.Entry(Id)!.Row.Content);
    }

    [Fact]
    public void ADropRow_IsDeferred_WhenTheEntryReappears()
    {
        using var s = Drop();
        s.Rig.Applier.BeforeTransaction = _ =>
            EditElsewhere(s.Rig, store => store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "back", Id: Id)));

        Assert.IsType<EntryResult.Deferred>(Run(s));
        Assert.Contains(Id, s.Rig.Scan.Rows().Keys);
    }

    // ---- a busy database ----

    private static void HoldTheLock(ApplyRig rig, Action<ApplyRig> whileHeld)
    {
        var (conn, _) = rig.Scan.Other();
        using (conn)
        {
            ScanRig.Exec(conn, "BEGIN IMMEDIATE");
            try { whileHeld(rig); }
            finally { ScanRig.Exec(conn, "ROLLBACK"); }
        }
    }

    [Fact]
    public void ABusyDatabase_IsADeferralThatStopsTheApply_WithNoIssueAndNoAttempt()
    {
        using var s = Insert();
        s.Rig.Scan.Connection.DefaultTimeout = 1;
        ScanRig.Exec(s.Rig.Scan.Connection, "PRAGMA busy_timeout = 0");
        var before = s.Rig.Full();

        HoldTheLock(s.Rig, _ =>
        {
            var deferred = Is<EntryResult.Deferred>(Run(s));
            Assert.True(deferred.Busy);
        });

        Assert.Equal(before, s.Rig.Full());
        Assert.Empty(s.Rig.Scan.Issues());
    }

    [Fact]
    public void ABusyLock_InTheCleanUpOfANoOp_IsADeferralThatStops()
    {
        using var s = Insert();
        Is<EntryResult.Carried>(Run(s));
        ScanRig.Exec(s.Rig.Scan.Connection, "INSERT INTO git_sync_issue (entry_id, kind, record_hash, attempts, detail) " +
                                            $"VALUES ('{Id}', 'apply-failed', 'x', 1, 'old')");
        s.Rig.Scan.Connection.DefaultTimeout = 1;
        ScanRig.Exec(s.Rig.Scan.Connection, "PRAGMA busy_timeout = 0");

        HoldTheLock(s.Rig, _ =>
        {
            var deferred = Is<EntryResult.Deferred>(Run(s)); // equal to the stored row: a no-op, whose clean-up is busy
            Assert.True(deferred.Busy);
        });

        Assert.Single(s.Rig.Scan.Issues());
    }

    // ---- the issue rows ----

    private static void SeedFailure(ApplyRig rig, string detail, int attempts = 3) =>
        ScanRig.Exec(rig.Scan.Connection,
            $"INSERT INTO git_sync_issue (entry_id, kind, record_hash, attempts, detail) VALUES ('{Id}', 'apply-failed', 'old', {attempts}, '{detail}')");

    [Fact]
    public void ANoOp_RemovesTheFailureRow_InOneSmallTransaction_AndAPostponedOrSkippedEntryDoesNot()
    {
        using var s = Insert();
        Is<EntryResult.Carried>(Run(s));
        SeedFailure(s.Rig, "old");
        var opened = s.Rig.Applier.TransactionCount;

        Is<EntryResult.NoOp>(Run(s));

        Assert.Empty(s.Rig.Scan.Issues());
        Assert.Equal(opened + 1, s.Rig.Applier.TransactionCount);

        // postponed: a pinned record for an id with no entry
        const string other = "3f9c2a7e-0000-4000-8000-000000000009";
        ScanRig.Exec(s.Rig.Scan.Connection, $"INSERT INTO git_sync_issue (entry_id, kind, record_hash, attempts, detail) VALUES ('{other}', 'apply-failed', 'old', 3, 'old')");
        Is<EntryResult.Postponed>(s.Rig.Apply(ApplyRig.Rec(other, sticky: true)));
        Assert.Single(s.Rig.Scan.Issues());
    }

    [Fact]
    public void ASkip_WritesAnUnreadableRow_OnlyWhenThereIsNone_NeverOverwritesARealFailure_AndARealFailureReplacesIt()
    {
        using var rig = new ApplyRig();
        rig.Scan.AddMemory(Tier.Warm, Id);
        rig.Scan.AddMemory(Tier.Cold, Id); // an id in two tiers

        Is<EntryResult.Skipped>(rig.Apply(ApplyRig.Rec(Id)));
        var skip = Assert.Single(rig.Scan.Issues());
        Assert.StartsWith("unreadable:", skip.Detail);
        using (var cmd = rig.Scan.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT attempts FROM git_sync_issue";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }

        // a real failure replaces the skip row with attempts = 1
        rig.Embedder.Fails = _ => true;
        rig.Scan.Store.Delete(Tier.Cold, ContentType.Memory, Id);
        rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        var failed = Is<EntryResult.Failed>(rig.Apply(ApplyRig.Rec(Id))); // an insert whose embed fails
        Assert.Equal(1, failed.Attempts);
        var real = Assert.Single(rig.Scan.Issues());
        Assert.DoesNotContain("unreadable", real.Detail);

        // and a skip never overwrites it
        rig.Scan.AddMemory(Tier.Warm, Id);
        rig.Scan.AddMemory(Tier.Cold, Id);
        Is<EntryResult.Skipped>(rig.Apply(ApplyRig.Rec(Id)));
        Assert.Equal(real, Assert.Single(rig.Scan.Issues()));
    }

    [Fact]
    public void AnIdenticalSkipRow_IsNotRewritten()
    {
        using var rig = new ApplyRig();
        ScanRig.Exec(rig.Scan.Connection, "CREATE TABLE issue_writes (n INTEGER)");
        ScanRig.Exec(rig.Scan.Connection, "CREATE TRIGGER issue_ins AFTER INSERT ON git_sync_issue BEGIN INSERT INTO issue_writes VALUES (1); END");
        ScanRig.Exec(rig.Scan.Connection, "CREATE TRIGGER issue_upd AFTER UPDATE ON git_sync_issue BEGIN INSERT INTO issue_writes VALUES (1); END");

        Assert.Null(rig.Applier.WriteSkipIssue(Id, GitSyncApplier.ScanSkippedPrefix, "content"));
        Assert.Null(rig.Applier.WriteSkipIssue(Id, GitSyncApplier.ScanSkippedPrefix, "content"));

        using var cmd = rig.Scan.Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM issue_writes";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void AFailureToWriteTheIssue_IsADeferral_NotAFailure()
    {
        using var s = TextAndTags();
        s.Rig.Embedder.Fails = _ => true;
        s.Rig.Applier.BeforeEntryStep = (_, step) => { if (step == "write-issue") throw new InvalidOperationException("no issue"); };

        var result = Is<EntryResult.Deferred>(Run(s));

        Assert.Contains("could not be recorded", result.Reason);
        Assert.Empty(s.Rig.Scan.Issues());
    }

    [Fact]
    public void AnEmbedderThatThrowsAnUnexpectedOutOfMemory_IsNotSwallowed()
    {
        using var s = TextAndTags();
        s.Rig.Embedder.Fails = _ => throw new OutOfMemoryException();

        Assert.Throws<OutOfMemoryException>(() => Run(s));
    }
}
