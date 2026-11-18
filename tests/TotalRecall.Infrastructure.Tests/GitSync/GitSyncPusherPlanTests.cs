using System.Text;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncPusherPlanTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";
    private const string C = "c3d4e5f6";

    private readonly PushRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>Prepare and Plan on one pusher, from the summaries of a real scan and pull.</summary>
    private async Task<(PlanResult Result, GitSyncPusher Pusher)> PlanAsync(
        Action<GitSyncPusher>? configure = null, string machine = PushRig.Label, CancellationToken ct = default, Action? afterPull = null)
    {
        _rig.Now += 1000;
        var (apply, scan) = await _rig.PullAsync();
        afterPull?.Invoke();
        var pusher = _rig.Pusher(machine);
        var prepared = Assert.IsType<PrepareResult.Ready>(await pusher.PrepareAsync(apply, scan, ct)).Value;
        configure?.Invoke(pusher);
        return (pusher.Plan(prepared, ct), pusher);
    }

    private static PlanResult.Planned Planned(PlanResult result) => Assert.IsType<PlanResult.Planned>(result);

    private GitSyncRow Row(string id) => GitSyncRows.ReadAll(_rig.Pull.Connection)[id];

    private (MsSqliteConnection Connection, SqliteStore Store) Other()
    {
        var conn = SyncDb.OpenMigrated(_rig.Pull.DbPath);
        return (conn, new SqliteStore(conn));
    }

    private void LogImport(string id, string tool, string hash, long ts, string logId)
    {
        using var cmd = _rig.Pull.Connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO import_log (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type) " +
            "VALUES ($i, $t, $s, 'p', $h, $e, 'warm', 'memory')";
        cmd.Parameters.AddWithValue("$i", logId);
        cmd.Parameters.AddWithValue("$t", ts);
        cmd.Parameters.AddWithValue("$s", tool);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$e", id);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _rig.Pull.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void SeedChanged(params string[] ids)
    {
        _rig.Seed(ids.Select(id => Rec(id, content: "old")).ToArray());
    }

    /// <summary>The seeded entries are pulled, then every one is edited, so each has a pending content stamp.</summary>
    private async Task EditedAsync(params string[] ids)
    {
        SeedChanged(ids);
        await _rig.PullAsync();
        foreach (var id in ids)
            _rig.Edit(Tier.Warm, id, new UpdateEntryOpts { Content = "new text for " + id });
    }

    // ---- new entries ----

    [GitFact]
    public async Task ANewEntry_PlansOneRecord_WithTheLabel_AndTheFirstNonGitSyncOrigin()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, source: SourceTool.ClaudeCode);
        LogImport(A, "git-sync", "hhh", 1, "l0");
        LogImport(A, "claude-code", "second", 20, "l2");
        LogImport(A, "cursor", "first", 10, "l1");

        var (result, _) = await PlanAsync();

        var planned = Planned(result);
        var entry = Assert.Single(planned.Entries);
        Assert.True(entry.IsNew);
        Assert.Equal(A, entry.Id);
        Assert.Equal("testbox", entry.Record.WrittenBy.Value);
        Assert.Equal("cursor", entry.Record.Origin.Value.Tool);
        Assert.Equal("first", entry.Record.Origin.Value.Hash);
        Assert.Equal(SourceTool.ClaudeCode, entry.Record.Source.Value);
        Assert.Equal(1, planned.Summary(null).New);
    }

    [GitFact]
    public async Task ANewEntry_WithOnlyAGitSyncImportRow_HasNoOrigin()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        LogImport(A, "git-sync", "hhh", 1, "l0");

        var (result, _) = await PlanAsync();

        Assert.True(Planned(result).Entries.Single().Record.Origin is null);
    }

    [GitFact]
    public async Task APinnedNewEntry_IsAStickyHotRecord()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        await _rig.PullAsync();
        _rig.Pin(A);

        var (result, _) = await PlanAsync();

        var record = Planned(result).Entries.Single().Record;
        Assert.True(record.Placement.Sticky);
        Assert.Equal(Tier.Hot, record.Placement.Tier);
    }

    [GitFact]
    public async Task APendingStamp_IsWrittenAsPushingStamps_ForTheWrittenGroupsOnly()
    {
        await EditedAsync(A);

        var (result, _) = await PlanAsync();

        var entry = Assert.Single(Planned(result).Entries);
        Assert.False(entry.IsNew);
        var row = Row(A);
        var stored = SyncApply.PushingStamps.decode(row.PushingStamps);
        Assert.NotNull(stored.Content);
        Assert.Null(stored.Sticky);
        Assert.Null(stored.Tier);
        Assert.Equal(row.ContentStamp, stored.Content.Value);
        Assert.NotNull(row.ContentStamp);   // the pending stamps stay until the finalize
        Assert.Contains("new text for " + A, Encoding.UTF8.GetString(entry.Bytes));
    }

    // ---- limits (decision 8) ----

    [GitFact]
    public async Task EveryLimit_BlocksTheWholeEntry_AndTheOthersArePlanned()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, content: new string('x', 262_144 + 1));
        _rig.AddMemory(Tier.Warm, B, tags: Enumerable.Range(0, 65).Select(i => "t" + i).ToList());
        _rig.AddMemory(Tier.Warm, C, tags: [new string('t', 129)]);
        _rig.AddMemory(Tier.Warm, "d4e5f6a7", createdAt: 253_402_300_800_000L);
        _rig.AddMemory(Tier.Warm, "e5f6a7b8");

        var (result, _) = await PlanAsync();

        var planned = Planned(result);
        Assert.Equal(["e5f6a7b8"], planned.Entries.Select(e => e.Id));
        var reasons = planned.Blocked.ToDictionary(b => b.Id, b => b.Reason);
        Assert.Equal("too-large", reasons[A]);
        Assert.Equal("too-many-tags", reasons[B]);
        Assert.Equal("tag-too-long", reasons[C]);
        Assert.Equal("not-encodable", reasons["d4e5f6a7"]);
        foreach (var id in new[] { A, B, C, "d4e5f6a7" })
        {
            var row = Row(id);
            Assert.Equal(reasons[id], row.BlockedReason);
            Assert.NotNull(row.BlockedHash);
            Assert.Null(row.PushingStamps);
        }
        Assert.Null(Row("e5f6a7b8").BlockedReason);
    }

    [GitFact]
    public async Task SixtyFourDistinctTags_WithDuplicates_AreWithinTheLimit()
    {
        _rig.Seed();
        var tags = Enumerable.Range(0, 64).Select(i => "t" + i).Concat(["t1", "t2"]).ToList();
        _rig.AddMemory(Tier.Warm, A, tags: tags);

        var (result, _) = await PlanAsync();

        Assert.Single(Planned(result).Entries);
    }

    [GitFact]
    public async Task AnIneligibleId_IsBlocked_AsIneligibleId()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, "XYZ");
        _rig.Now += 1000;
        _rig.Scan();
        // the scan lists an ineligible id and writes no row: make the row by hand, as a repair would
        var stamp = St(1);
        GitSyncRows.Write(_rig.Pull.Connection, new GitSyncRow(
            "XYZ", SyncMerge.RowState.Active, null, null, null, null, null, stamp, stamp, stamp, null, null, null, null, null));

        var (result, _) = await PlanAsync();

        Assert.Equal("ineligible-id", Assert.Single(Planned(result).Blocked).Reason);
        Assert.Equal("ineligible-id", Row("XYZ").BlockedReason);
    }

    [GitFact]
    public async Task AFixedEntry_ClearsItsMarks_OnTheNextPlan()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A, content: new string('x', 262_144 + 1));
        await PlanAsync();
        Assert.Equal("too-large", Row(A).BlockedReason);
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "short now" });

        var (result, _) = await PlanAsync();

        Assert.Single(Planned(result).Entries);
        Assert.Null(Row(A).BlockedReason);
        Assert.Null(Row(A).BlockedHash);
    }

    [GitFact]
    public async Task ARowThatWentBackToItsBase_ClearsAWritersMark_ButNotAnotherReason()
    {
        _rig.Seed(Rec(A), Rec(B));
        await _rig.PullAsync();
        var rows = GitSyncRows.ReadAll(_rig.Pull.Connection);
        GitSyncRows.Write(_rig.Pull.Connection, rows[A] with { BlockedReason = "too-large", BlockedHash = "h" });
        GitSyncRows.Write(_rig.Pull.Connection, rows[B] with { BlockedReason = "secret", BlockedHash = "h" });

        var (result, pusher) = await PlanAsync();

        Planned(result);
        Assert.Null(Row(A).BlockedReason);
        Assert.Null(Row(A).BlockedHash);
        Assert.Equal("secret", Row(B).BlockedReason);
        Assert.Equal(1, pusher.TransactionCount);
    }

    [GitFact]
    public async Task ACandidateLeftOutByTheExclusions_KeepsItsMarks()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        await _rig.PullAsync();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { BlockedReason = "too-large", BlockedHash = "h" });

        var (result, _) = await PlanAsync(afterPull: () =>
            GitSyncIssues.Upsert(_rig.Pull.Connection, A, GitSyncIssues.ApplyFailed, null, 1, "x"));

        Assert.Equal("issue", Assert.Single(Planned(result).Skipped).Reason);
        Assert.Equal("too-large", Row(A).BlockedReason);
    }

    // ---- decision 9: the re-read ----

    [GitTheory]
    [InlineData("text")]
    [InlineData("tag")]
    [InlineData("pin")]
    [InlineData("move")]
    public async Task AnEditThatLandedAfterTheScan_DefersTheEntry_AndChangesNothing(string kind)
    {
        await EditedAsync(A);
        string? before = null;

        var (result, _) = await PlanAsync(p => p.BeforePlanTransaction = () =>
        {
            before = _rig.RowDump();
            var (conn, store) = Other();
            using (conn)
            {
                switch (kind)
                {
                    case "text": store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "later edit" }); break;
                    case "tag": store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Tags = ["late"] }); break;
                    case "pin": store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, A); store.SetSticky(ContentType.Memory, A, true); break;
                    default: store.Move(Tier.Warm, ContentType.Memory, Tier.Cold, ContentType.Memory, A); break;
                }
            }
            MsSqliteConnection.ClearAllPools();
        });

        var planned = Planned(result);
        Assert.Empty(planned.Entries);
        Assert.Equal(new PushItem(A, "changed-since-scan"), Assert.Single(planned.Deferred));
        Assert.Equal(before, _rig.RowDump());
    }

    [GitFact]
    public async Task AnEntryThatIsGoneOrInTwoTiers_IsDeferred_AsUnreadableEntry()
    {
        await EditedAsync(A, B);

        var (result, _) = await PlanAsync(p => p.BeforePlanTransaction = () =>
        {
            _rig.Store.Delete(Tier.Warm, ContentType.Memory, A);
            _rig.Store.Insert(Tier.Cold, ContentType.Memory, new InsertEntryOpts(Content: "twin", Id: B));
        });

        var planned = Planned(result);
        Assert.Empty(planned.Entries);
        Assert.Equal([new PushItem(A, "unreadable-entry"), new PushItem(B, "unreadable-entry")], planned.Deferred);
    }

    [GitFact]
    public async Task ABusyDatabase_IsFailedBusy_AndWritesNothing()
    {
        await EditedAsync(A);
        using var pragma = _rig.Pull.Connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 0";
        pragma.ExecuteNonQuery();
        var (conn, _) = Other();
        string? before = null;
        using (conn)
        {
            using var hold = conn.CreateCommand();

            var (result, _) = await PlanAsync(p => p.BeforePlanTransaction = () =>
            {
                before = _rig.RowDump();
                hold.CommandText = "BEGIN IMMEDIATE";
                hold.ExecuteNonQuery();
            });

            var failed = Assert.IsType<PushOutcome.Failed>(Assert.IsType<PlanResult.Stop>(result).Outcome);
            Assert.Equal("Busy", failed.Kind);
            Assert.Equal(SyncPush.Published.No, failed.Published);
            hold.CommandText = "ROLLBACK";
            hold.ExecuteNonQuery();
        }
        Assert.Equal(before, _rig.RowDump());
    }

    [GitTheory]
    [InlineData("reread")]
    [InlineData("plan")]
    [InlineData("pushing")]
    [InlineData("block")]
    public async Task AFaultAtAnyStep_RollsTheWholeTransactionBack(string step)
    {
        await EditedAsync(A, B);
        // a limit mark on a third entry that would be written in the same transaction
        _rig.AddMemory(Tier.Warm, C, content: new string('x', 262_144 + 1));

        var (result, _) = await PlanAsync(p => p.BeforeStep = (id, s) =>
        {
            if (s == step && id == B)
                throw new InvalidOperationException("injected fault at " + step);
        });

        var failed = Assert.IsType<PushOutcome.Failed>(Assert.IsType<PlanResult.Stop>(result).Outcome);
        Assert.Equal("Store", failed.Kind);
        Assert.Contains("injected fault", failed.Message);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        var after = GitSyncRows.ReadAll(_rig.Pull.Connection);
        Assert.Null(after[A].PushingStamps);
        Assert.Null(after[B].PushingStamps);
        Assert.False(after.ContainsKey(C) && after[C].BlockedReason is not null);
    }

    [GitFact]
    public async Task NothingToDo_OpensNoTransaction()
    {
        _rig.Seed(Rec(A));
        await _rig.PullAsync();

        var (result, pusher) = await PlanAsync();

        var planned = Planned(result);
        Assert.Empty(planned.Entries);
        Assert.Empty(planned.Unchanged);
        Assert.Equal(0, pusher.TransactionCount);
    }

    // ---- decision 6: groups ----

    [GitFact]
    public async Task AStaleGroup_IsNotWritten_AndTheOthersAre()
    {
        SeedChanged(A);
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Tags = ["x"] });
        _rig.Now += 1000;
        _rig.Scan();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { ContentStamp = St(5) });   // older than the record's St(10)

        var (result, _) = await PlanAsync();

        var planned = Planned(result);
        var entry = Assert.Single(planned.Entries);
        Assert.Equal(["x"], entry.Record.Tags);
        Assert.Equal(new PushItem(A, "stale-stamp"), Assert.Single(planned.Skipped));
        Assert.Contains("old", Encoding.UTF8.GetString(entry.Bytes));
    }

    [GitFact]
    public async Task TwoStaleGroups_AreListedOnce()
    {
        SeedChanged(A);
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Tags = ["x"] });
        _rig.Now += 1000;
        _rig.Scan();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { ContentStamp = St(5), TierStamp = St(5) });

        var (result, _) = await PlanAsync();

        Assert.Equal(new PushItem(A, "stale-stamp"), Assert.Single(Planned(result).Skipped));
    }

    [GitFact]
    public async Task AnEntryWithNoGroupToWrite_IsUnchanged_AndKeepsTheStalePendingStamp()
    {
        SeedChanged(A);
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "different" });
        _rig.Now += 1000;
        _rig.Scan();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { ContentStamp = St(5) });

        var (result, _) = await PlanAsync();

        var planned = Planned(result);
        Assert.Empty(planned.Entries);
        var same = Assert.Single(planned.Unchanged);
        Assert.Equal(A, same.Id);
        Assert.Equal("stale-stamp", Assert.Single(planned.Skipped).Reason);
        Assert.Contains("\"content\": \"old\"", same.BaseText.Replace("\"content\":\"old\"", "\"content\": \"old\""));
        Assert.Null(same.Pushed.Content);
        Assert.Equal(1, planned.Summary(null).Unchanged);
    }

    [GitFact]
    public async Task AMissingStamp_LeavesTheWholeEntryOut()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);
        await _rig.PullAsync();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { StickyStamp = null });

        var (result, _) = await PlanAsync();

        var planned = Planned(result);
        Assert.Empty(planned.Entries);
        Assert.Equal(new PushItem(A, "missing-stamp"), Assert.Single(planned.Skipped));
    }

    [GitFact]
    public async Task APinOnAColderRecord_WithNoTierStamp_IsMissingStamp()
    {
        SeedChanged(A);
        await _rig.PullAsync();
        _rig.Pin(A);
        _rig.Now += 1000;
        _rig.Scan();
        GitSyncRows.Write(_rig.Pull.Connection, Row(A) with { TierStamp = null });

        var (result, _) = await PlanAsync();

        Assert.Equal(new PushItem(A, "missing-stamp"), Assert.Single(Planned(result).Skipped));
    }

    [GitFact]
    public async Task APromotionAndAPin_AreWrittenTogether_WithBothStamps()
    {
        SeedChanged(A);
        await _rig.PullAsync();
        _rig.Pin(A);

        var (result, _) = await PlanAsync();

        var entry = Assert.Single(Planned(result).Entries);
        Assert.True(entry.Record.Placement.Sticky);
        Assert.Equal(Tier.Hot, entry.Record.Placement.Tier);
        var stored = SyncApply.PushingStamps.decode(Row(A).PushingStamps);
        Assert.NotNull(stored.Sticky);
        Assert.NotNull(stored.Tier);
        Assert.Null(stored.Content);
    }

    [GitFact]
    public async Task ProjectAndScope_AreWrittenWhole_AndAnAbsentOneClearsTheRecords()
    {
        _rig.Seed(Rec(A, content: "p", project: "o/r", scope: "s"), Rec(B, content: "q", project: "o/r", scope: "s"));
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "p2" });
        _rig.Edit(Tier.Warm, B, new UpdateEntryOpts { Content = "q2" });
        Exec($"UPDATE warm_memories SET project = NULL WHERE id = '{A}'");
        Exec($"UPDATE warm_memories SET scope = '' WHERE id = '{A}'");
        Exec($"UPDATE warm_memories SET project = 'other/repo' WHERE id = '{B}'");

        var (result, _) = await PlanAsync();

        var entries = Planned(result).Entries.ToDictionary(e => e.Id);
        Assert.True(entries[A].Record.Project is null);
        Assert.True(entries[A].Record.Scope is null);
        Assert.Equal("other/repo", entries[B].Record.Project.Value);
        Assert.True(entries[B].Record.Scope is not null);
    }

    [GitFact]
    public async Task TheSourceTool_IsKept_Set_Cleared_AndAnUnparsedOneSurvivesATextEdit()
    {
        var unparsed = Encoding.UTF8.GetString(RecordWriter.Write(Rec(C, content: "old", source: SourceTool.Cursor)).Bytes!)
            .Replace("\"cursor\"", "\"futuretool\"");
        Assert.Contains("futuretool", unparsed);
        _rig.SeedWith(pull =>
        {
            pull.PutRecord(Rec(A, content: "old", source: SourceTool.Cursor));
            pull.PutRecord(Rec(B, content: "old", source: SourceTool.Cursor));
            pull.PutText(SyncValidation.recordPath(C), unparsed);
            pull.PutRecord(Rec("d4e5f6a7", content: "old", source: SourceTool.Cursor));
        });
        await _rig.PullAsync();
        _rig.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "kept" });          // local source: none, record parsed: cleared
        _rig.Edit(Tier.Warm, B, new UpdateEntryOpts { Content = "set" });
        _rig.Edit(Tier.Warm, C, new UpdateEntryOpts { Content = "unparsed" });
        _rig.Edit(Tier.Warm, "d4e5f6a7", new UpdateEntryOpts { Content = "same tool" });
        Exec($"UPDATE warm_memories SET source_tool = 'claude-code' WHERE id = '{B}'");
        Exec($"UPDATE warm_memories SET source_tool = 'cursor' WHERE id = 'd4e5f6a7'");
        Exec($"UPDATE warm_memories SET source_tool = NULL WHERE id IN ('{A}', '{C}')");

        var (result, _) = await PlanAsync();

        var entries = Planned(result).Entries.ToDictionary(e => e.Id);
        Assert.True(entries[A].Record.Source is null);                               // cleared
        Assert.Equal(SourceTool.ClaudeCode, entries[B].Record.Source.Value);         // set
        Assert.Equal(SourceTool.Cursor, entries["d4e5f6a7"].Record.Source.Value);    // kept
        Assert.Contains("futuretool", Encoding.UTF8.GetString(entries[C].Bytes));    // unparsed text kept
    }

    [GitFact]
    public async Task AHostileMachineName_IsReducedInWrittenBy()
    {
        _rig.Seed();
        _rig.AddMemory(Tier.Warm, A);

        var (result, _) = await PlanAsync(machine: "Joe's PC\nönly");

        Assert.Equal("JoesPCnly", Planned(result).Entries.Single().Record.WrittenBy.Value);
    }

    // ---- cancellation ----

    [GitFact]
    public async Task ACancelledToken_BeforeTheTransaction_IsCancelled_AndWritesNothing()
    {
        await EditedAsync(A);
        using var cts = new CancellationTokenSource();
        string? before = null;

        var (result, pusher) = await PlanAsync(p => p.BeforePlanTransaction = () =>
        {
            before = _rig.RowDump();
            cts.Cancel();
        }, ct: cts.Token);

        var failed = Assert.IsType<PushOutcome.Failed>(Assert.IsType<PlanResult.Stop>(result).Outcome);
        Assert.Equal("Cancelled", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Equal(0, pusher.TransactionCount);
        Assert.Equal(before, _rig.RowDump());
        Assert.Empty(GitSyncIssues.ReadOpen(_rig.Pull.Connection, GitSyncIssues.ApplyFailed));
    }

    [GitFact]
    public async Task ACancelInsideTheTransaction_LetsItComplete()
    {
        await EditedAsync(A);
        using var cts = new CancellationTokenSource();

        var (result, pusher) = await PlanAsync(p => p.BeforeStep = (_, s) =>
        {
            if (s == "reread")
                cts.Cancel();
        }, ct: cts.Token);

        Assert.Single(Planned(result).Entries);
        Assert.NotNull(Row(A).PushingStamps);
        Assert.Equal(1, pusher.TransactionCount);
    }
}
