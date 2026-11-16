using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;
using SyncRecordModule = TotalRecall.Core.SyncRecord;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncScannerEdgeTests
{
    private const string A = "3f9c2a7e";
    private const string B = "aaaa1111";
    private const string C = "bbbb2222";
    private const long T1 = 1_800_000_000_000L;
    private const long T2 = T1 + 60_000;
    private const long T3 = T2 + 60_000;
    private const long Day = 86_400_000L;

    private static readonly SyncStamp.MachineId Other = SyncStamp.MachineIdModule.tryCreate("0000000b").Value;

    private static ScanSummary Scan(ScanRig rig, long now) =>
        Assert.IsType<ScanOutcome.Done>(rig.Scanner(now).Scan()).Summary;

    private static int Cmp(SyncStamp.Stamp? a, SyncStamp.Stamp? b)
    {
        Assert.NotNull(a);
        Assert.NotNull(b);
        return SyncStamp.StampModule.compare(a, b);
    }

    private static SyncStamp.Stamp Stamp(long timeMs, int counter = 0) => new(timeMs, counter, Other);

    private static string BaseJson(string id, Tier tier, SyncStamp.Stamp content, SyncStamp.Stamp sticky, SyncStamp.Stamp tierStamp)
    {
        var record = new SyncRecord(
            id, "base", EntryType.Decision, ListModule.Empty<string>(), null, 1_700_000_000_000L, null, null,
            new SyncRecordModule.Placement(tier, tierStamp, false, sticky), content, null, null);
        return Encoding.UTF8.GetString(RecordWriter.Write(record).Bytes!);
    }

    private static string ClockText(long ms) => SyncStamp.ClockModule.serialize(new SyncStamp.Clock(ms, 0));

    /// <summary>An active row that matches the entry as it is now, with the given base and recorded flag and tier.</summary>
    private static void SeedRow(ScanRig rig, string id, string? baseJson, Tier localTier, bool localSticky = false)
    {
        var view = rig.Scanner(T1).ReadSnapshot().Entries[id].View;
        GitSyncRows.Write(rig.Connection, new GitSyncRow(
            id, SyncMerge.RowState.Active, baseJson, SyncScan.contentHash(view.Content), SyncScan.tagsHash(view.Tags),
            localSticky, localTier, null, null, null, null, null, null, null, null));
    }

    /// <summary>The three tables the scan writes, as text.</summary>
    private static string DumpSync(ScanRig rig)
    {
        var sb = new StringBuilder();
        foreach (var sql in new[]
                 {
                     "SELECT * FROM git_sync_entry ORDER BY 1", "SELECT * FROM git_sync_issue ORDER BY 1, 2",
                     "SELECT * FROM _meta ORDER BY 1",
                 })
        {
            using var cmd = rig.Connection.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                for (var i = 0; i < r.FieldCount; i++)
                    sb.Append(r.IsDBNull(i) ? "NULL" : r.GetValue(i)).Append('|');
                sb.Append('\n');
            }
            sb.Append("==\n");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------
    // the clock (decision 11)

    [Fact]
    public void AClockMoreThan24HoursAhead_IsResetToTheWallClock_WithAWarning()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        rig.SetMeta("git_sync_hlc", ClockText(T1 + 3 * Day));

        Scan(rig, T1);

        Assert.Equal(T1, rig.Rows()[A].ContentStamp!.TimeMs);
        Assert.Equal(T1, SyncStamp.ClockModule.tryDeserialize(rig.Meta("git_sync_hlc")!)!.Value.ClockMs);
        Assert.Equal("clock: clock-reset", rig.Meta("git_sync_clock_warning"));
    }

    [Fact]
    public void AClockResetWithNothingToStamp_IsStillWritten()
    {
        using var rig = new ScanRig();
        rig.SetMeta("git_sync_hlc", ClockText(T1 + 3 * Day));

        Scan(rig, T1);

        Assert.Equal(ClockText(T1), rig.Meta("git_sync_hlc"));
        Assert.Equal("clock: clock-reset", rig.Meta("git_sync_clock_warning"));
    }

    [Fact]
    public void AnUnreadableClockValue_IsReplaced_WithAWarning()
    {
        using var rig = new ScanRig();
        rig.SetMeta("git_sync_hlc", "garbage");

        Scan(rig, T1);

        Assert.Equal(ClockText(T1), rig.Meta("git_sync_hlc"));
        Assert.Equal("clock: clock-unreadable", rig.Meta("git_sync_clock_warning"));
    }

    [Fact]
    public void AnAbsentClock_StartsFromTheWallClock_WithNoWarning()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");

        Scan(rig, T1);

        Assert.NotNull(rig.Meta("git_sync_hlc"));
        Assert.Null(rig.Meta("git_sync_clock_warning"));
    }

    [Fact]
    public void ACleanScan_LeavesTheWarningKeyAbsent()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "two" });

        Scan(rig, T2);

        Assert.Null(rig.Meta("git_sync_clock_warning"));
    }

    [Fact]
    public void ABaseStampAheadOfTheWallClock_GetsAStampAboveIt_AndTheFarFutureKind()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        var far = Stamp(T1 + 5 * Day);
        SeedRow(rig, A, BaseJson(A, Tier.Warm, far, Stamp(1), Stamp(2)), Tier.Warm);
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "two" });

        Scan(rig, T1);

        Assert.True(Cmp(rig.Rows()[A].ContentStamp, far) > 0);
        Assert.Equal("clock: far-future", rig.Meta("git_sync_clock_warning"));
        Assert.Null(rig.Meta("git_sync_hlc"));                                   // the clock did not move for a far-future stamp
    }

    [Fact]
    public void ASecondEditWithAFarFutureBase_IsStampedAboveTheFirst_EvenThen()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        SeedRow(rig, A, BaseJson(A, Tier.Warm, Stamp(T1 + 5 * Day), Stamp(1), Stamp(2)), Tier.Warm);
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "two" });
        Scan(rig, T1);
        var first = rig.Rows()[A].ContentStamp;
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "three" });

        Scan(rig, T2);

        Assert.True(Cmp(rig.Rows()[A].ContentStamp, first) > 0);
    }

    private static (ScanRig Rig, long Edited) ExhaustedSetup()
    {
        var rig = new ScanRig();
        rig.AddMemory(Tier.Hot, A, "one", tags: ["x"]);          // base: content stamp at the end of the range
        rig.AddMemory(Tier.Warm, B, "other");
        Scan(rig, T1);
        var end = new SyncStamp.Stamp(SyncStamp.maxTimeMs, 999_999, Other);
        // A's row: recorded warm and not sticky, the base says warm; the memory is hot, pinned, retagged and edited
        SeedRow(rig, A, BaseJson(A, Tier.Warm, end, Stamp(1), Stamp(2)), Tier.Warm);
        rig.Pin(A);
        rig.Store.Update(Tier.Hot, ContentType.Memory, A, new UpdateEntryOpts { Content = "one, edited", Tags = ["x", "y"] });
        rig.Store.Update(Tier.Warm, ContentType.Memory, B, new UpdateEntryOpts { Content = "other, edited" });
        return (rig, T2);
    }

    [Fact]
    public void ABaseStampAtTheEndOfTheClockRange_SkipsThatGroup_StampsEverythingElse()
    {
        var (rig, now) = ExhaustedSetup();
        using var _ = rig;
        var before = rig.Rows();

        var summary = Scan(rig, now);

        var skipped = Assert.Single(summary.Skipped);
        Assert.Equal((A, "content"), (skipped.Id, skipped.Group));
        Assert.False(string.IsNullOrEmpty(skipped.Reason));
        var row = rig.Rows()[A];
        Assert.Equal(before[A].ScannedContentHash, row.ScannedContentHash);
        Assert.Null(before[A].StickyStamp);                                       // the seeded row had no pending stamps
        Assert.Null(row.ContentStamp);
        Assert.NotEqual(before[A].ScannedTagsHash, row.ScannedTagsHash);
        Assert.NotNull(row.StickyStamp);
        Assert.True(row.LocalSticky);
        Assert.NotNull(row.TierStamp);
        Assert.True(row.LocalTier!.IsHot);
        Assert.True(Cmp(rig.Rows()[B].ContentStamp, before[B].ContentStamp) > 0);
        Assert.Equal("clock: exhausted", rig.Meta("git_sync_clock_warning"));
    }

    [Fact]
    public void SecondScan_WithRecurringConditions_WritesNothing()
    {
        var (rig, now) = ExhaustedSetup();
        using var _ = rig;
        rig.AddMemory(Tier.Warm, "not-an-id");                                  // an ineligible id
        Scan(rig, now);                                                          // skip + ineligible issue
        var dump = DumpSync(rig);
        var memories = rig.Dump();

        var second = rig.Scanner(now + 1000);
        var summary = Assert.IsType<ScanOutcome.Done>(second.Scan()).Summary;
        var third = rig.Scanner(now + 2000);
        Assert.IsType<ScanOutcome.Done>(third.Scan());

        // A's content group is still skipped (the same change) and the ineligible id is still there; the far-future kind has its own test
        Assert.Equal(0, second.WriteTransactionCount);
        Assert.Equal(0, third.WriteTransactionCount);
        Assert.Equal(dump, DumpSync(rig));
        Assert.Equal(memories, rig.Dump());
        Assert.Equal(A, Assert.Single(summary.Skipped).Id);
        Assert.Equal(1, summary.Ineligible);
    }

    [Fact]
    public void AMixedSetOfClockConditions_IsWrittenOnce_AndLeftAlone()
    {
        var (rig, now) = ExhaustedSetup();
        using var _ = rig;
        rig.AddMemory(Tier.Warm, C, "far");
        SeedRow(rig, C, BaseJson(C, Tier.Warm, Stamp(now + 5 * Day), Stamp(1), Stamp(2)), Tier.Warm);
        rig.Store.Update(Tier.Warm, ContentType.Memory, C, new UpdateEntryOpts { Content = "far, edited" });
        rig.SetMeta("git_sync_hlc", ClockText(now + 3 * Day));                   // reset

        Scan(rig, now);

        Assert.Equal("clock: clock-reset,exhausted,far-future", rig.Meta("git_sync_clock_warning"));
        var dump = DumpSync(rig);
        var next = rig.Scanner(now + 1000);
        Assert.IsType<ScanOutcome.Done>(next.Scan());
        Assert.Equal(0, next.WriteTransactionCount);
        Assert.Equal(dump, DumpSync(rig));
    }

    // ---------------------------------------------------------------------------------------
    // the issue rows (decision 2)

    [Fact]
    public void IneligibleIdIssues_AreWrittenOnlyOnADifference_AndRemovedWhenTheMemoryIsGone()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, "not-an-id");

        var first = rig.Scanner(T1);
        var summary = Assert.IsType<ScanOutcome.Done>(first.Scan()).Summary;
        Assert.Equal(1, first.WriteTransactionCount);
        Assert.Equal(1, summary.Ineligible);
        Assert.Equal([("not-an-id", "ineligible-id", (string?)"warm")], rig.Issues());
        Assert.Empty(rig.Rows());

        var same = rig.Scanner(T2);
        same.Scan();
        Assert.Equal(0, same.WriteTransactionCount);

        rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Cold, ContentType.Memory, "not-an-id");
        var moved = rig.Scanner(T2);
        moved.Scan();
        Assert.Equal(1, moved.WriteTransactionCount);
        Assert.Equal([("not-an-id", "ineligible-id", (string?)"cold")], rig.Issues());

        rig.Store.Delete(Tier.Cold, ContentType.Memory, "not-an-id");
        var gone = rig.Scanner(T3);
        gone.Scan();
        Assert.Equal(1, gone.WriteTransactionCount);
        Assert.Empty(rig.Issues());
    }

    [Fact]
    public void OtherIssueKindsForTheSameId_AreNeverTouched()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, "not-an-id");
        rig.Exec("INSERT INTO git_sync_issue (entry_id, kind, attempts, detail) VALUES ('not-an-id', 'apply-failed', 2, 'x')");
        rig.Exec("INSERT INTO git_sync_issue (entry_id, kind, record_hash, attempts, detail) VALUES ('not-an-id', 'rejected-record', 'h', 1, 'y')");
        Scan(rig, T1);
        Assert.Equal(3, rig.Issues().Count);

        rig.Store.Delete(Tier.Warm, ContentType.Memory, "not-an-id");
        Scan(rig, T2);

        Assert.Equal(["apply-failed", "rejected-record"], rig.Issues().Select(i => i.Kind));
        Assert.Equal((2, "x"), (Convert.ToInt32(Scalar(rig, "SELECT attempts FROM git_sync_issue WHERE kind = 'apply-failed'")), (string)Scalar(rig, "SELECT detail FROM git_sync_issue WHERE kind = 'apply-failed'")));
    }

    private static object Scalar(ScanRig rig, string sql)
    {
        using var cmd = rig.Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()!;
    }

    [Fact]
    public void IneligibleIdInTwoTiers_GivesOneIssueRow_StableAcrossScans()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, "not-an-id");
        rig.AddMemory(Tier.Cold, "not-an-id");

        Scan(rig, T1);
        var dump = DumpSync(rig);
        var again = rig.Scanner(T2);
        again.Scan();

        Assert.Equal([("not-an-id", "ineligible-id", (string?)"warm")], rig.Issues());
        Assert.Equal(0, again.WriteTransactionCount);
        Assert.Equal(dump, DumpSync(rig));
    }

    // ---------------------------------------------------------------------------------------
    // idempotence, concurrency and atomicity (decisions 3, 13, 14)

    [Fact]
    public void ANoOpScan_OpensNoWriteTransaction()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Hot, A, "one");
        rig.AddMemory(Tier.Cold, B, "two");
        var first = rig.Scanner(T1);
        first.Scan();
        Assert.Equal(1, first.WriteTransactionCount);
        var dump = DumpSync(rig);

        var second = rig.Scanner(T2);
        Assert.IsType<ScanOutcome.Done>(second.Scan());

        Assert.Equal(0, second.WriteTransactionCount);
        Assert.Equal(dump, DumpSync(rig));
    }

    [Fact]
    public void AMoveIntoAnAlreadyReadTier_IsSeenOnce_AndNeverADeletion()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        var before = DumpSync(rig);
        var (otherConn, otherStore) = rig.Other();
        using (otherConn)
        {
            var scanner = rig.Scanner(T2);
            // after the hot table was read, the memory moves INTO it: an unsnapshotted scan finds it nowhere
            scanner.AfterFirstTableRead = () => otherStore.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, A);

            var summary = Assert.IsType<ScanOutcome.Done>(scanner.Scan()).Summary;

            Assert.Empty(summary.LocalDeletions);
            Assert.Empty(summary.Skipped);
            Assert.Equal(before, DumpSync(rig));
        }
        // the next scan sees the promotion once
        Assert.Equal(1, Scan(rig, T3).Tier);
    }

    [Fact]
    public void AMoveOutOfAnAlreadyReadTier_IsNotSeenTwice()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Hot, A, "one");
        Scan(rig, T1);
        var first = rig.Rows()[A];
        rig.Store.Update(Tier.Hot, ContentType.Memory, A, new UpdateEntryOpts { Content = "two" });
        var (otherConn, otherStore) = rig.Other();
        using (otherConn)
        {
            var scanner = rig.Scanner(T2);
            // after the hot table was read, the memory leaves it for warm: an unsnapshotted scan would see it twice
            scanner.AfterFirstTableRead = () => otherStore.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, A);

            var summary = Assert.IsType<ScanOutcome.Done>(scanner.Scan()).Summary;

            Assert.Empty(summary.Skipped);
            Assert.Empty(summary.LocalDeletions);
            Assert.Equal(1, summary.Content);
            Assert.True(Cmp(rig.Rows()[A].ContentStamp, first.ContentStamp) > 0);
        }
    }

    [Fact]
    public void ALockTimeoutOnTheWrite_ReturnsFailed_NotARollbackError()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        var csb = new SqliteConnectionStringBuilder { DataSource = rig.DbPath, Pooling = false, DefaultTimeout = 1 };
        using var scannerConn = new MsSqliteConnection(csb.ConnectionString);
        scannerConn.Open();
        ScanRig.Exec(scannerConn, "PRAGMA busy_timeout = 50");
        rig.Exec("BEGIN IMMEDIATE");                                              // another process holds the write lock

        var sw = Stopwatch.StartNew();
        var outcome = new GitSyncScanner(scannerConn, () => T1).Scan();
        sw.Stop();

        var failed = Assert.IsType<ScanOutcome.Failed>(outcome);
        Assert.Contains("locked", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rollback", failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        rig.Exec("COMMIT");
        Assert.Empty(rig.Rows());
        Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(scannerConn, () => T1).Scan());
        Assert.Single(rig.Rows());
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("issues")]
    [InlineData("clock")]
    [InlineData("warning")]
    public void AFaultInEachWriteStep_LeavesEverythingAsBefore(string step)
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "two" });
        rig.AddMemory(Tier.Warm, "not-an-id");
        rig.SetMeta("git_sync_hlc", ClockText(T2 + 3 * Day));                    // a reset: the clock and the warning are written too
        var before = DumpSync(rig);
        var scanner = rig.Scanner(T2);
        scanner.BeforeWrite = s => { if (s == step) throw new InvalidOperationException("fault at " + s); };

        var outcome = Assert.IsType<ScanOutcome.Failed>(scanner.Scan());

        Assert.Equal("fault at " + step, outcome.Reason);
        Assert.Equal(before, DumpSync(rig));
        // a later scan without the fault stamps the same changes
        scanner.BeforeWrite = null;
        var summary = Assert.IsType<ScanOutcome.Done>(scanner.Scan()).Summary;
        Assert.Equal(1, summary.Content);
        Assert.Equal(1, summary.Ineligible);
        Assert.Equal(T2, rig.Rows()[A].ContentStamp!.TimeMs);
        Assert.Equal("clock: clock-reset", rig.Meta("git_sync_clock_warning"));
    }
}
