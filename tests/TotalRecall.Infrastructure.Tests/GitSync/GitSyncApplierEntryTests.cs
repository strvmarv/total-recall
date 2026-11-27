using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncApplierEntryTests : IDisposable
{
    private const string Id = "3f9c2a7e-0000-4000-8000-000000000001";
    private const string Id2 = "3f9c2a7e-0000-4000-8000-000000000002";

    private readonly ApplyRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static T Is<T>(EntryResult r) where T : EntryResult => Assert.IsType<T>(r);

    private void AssertScanQuiet()
    {
        var before = _rig.Scan.Rows();
        var scan = _rig.RunScan();
        Assert.Equal(0, scan.New + scan.Content + scan.Tags + scan.Sticky + scan.Tier + scan.Left + scan.Reactivated);
        Assert.Equal(before.Count, _rig.Scan.Rows().Count);
        foreach (var (id, row) in before)
            Assert.Equal(row, _rig.Scan.Rows()[id]);
    }

    // ---- insert ----

    [Theory]
    [InlineData("Hot")]
    [InlineData("Warm")]
    [InlineData("Cold")]
    public void ANewRecord_IsInserted_InItsTier_WithAVector_AnImportRow_AndARowThatTheScanLeavesAlone(string tierName)
    {
        var tier = tierName switch { "Hot" => Tier.Hot, "Warm" => Tier.Warm, _ => Tier.Cold };
        var rec = ApplyRig.Rec(Id, tier: tier, minute: 10);

        var carried = Is<EntryResult.Carried>(_rig.Apply(rec));

        Assert.Equal(CarriedKind.Inserted, carried.Kind);
        Assert.False(carried.Colder);
        var found = _rig.Entry(Id)!;
        Assert.Equal(tier, found.Tier);
        Assert.Equal("Use bun, not npm.", found.Row.Content);
        Assert.Equal(1_700_000_000_000L, found.Row.CreatedAt);
        Assert.Equal(1, _rig.VectorCount(tier));
        Assert.Equal(1, _rig.ImportLogRows(Id));
        var row = _rig.Scan.Rows()[Id];
        Assert.Equal(SyncMerge.RowState.Active, row.State);
        Assert.NotNull(row.BaseJson);
        Assert.Null(row.ContentStamp); // nothing is pending: the entry is the record
        Assert.False(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        AssertScanQuiet();
    }

    [Fact]
    public void AnInsert_CarriesEveryField_AndAnAbsentScope_IsEmpty()
    {
        var rec = ApplyRig.Rec(Id, tags: ["b", "a"], project: "o/r", scope: "s", source: SourceTool.ClaudeCode);

        _rig.Apply(rec);

        var e = _rig.Entry(Id)!.Row;
        Assert.Equal(["a", "b"], e.Tags.ToArray());
        Assert.Equal("o/r", e.Project!.Value);
        Assert.Equal(SourceTool.ClaudeCode, e.SourceTool!.Value);
        _rig.Apply(ApplyRig.Rec(Id2));
        Assert.Null(_rig.Entry(Id2)!.Row.Project);
    }

    [Fact]
    public void AnImportedRecord_LogsItsOriginHash_AndAnOtherRecordTheContentHash()
    {
        _rig.Apply(ApplyRig.Rec(Id, originHash: "abc123"));
        _rig.Apply(ApplyRig.Rec(Id2, content: "other"));

        Assert.Equal(1, _rig.ImportLogRows(Id));
        using var cmd = _rig.Scan.Connection.CreateCommand();
        cmd.CommandText = "SELECT content_hash, source_path FROM import_log WHERE target_entry_id = $id";
        cmd.Parameters.AddWithValue("$id", Id);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("abc123", r.GetString(0));
        Assert.Equal("sync:" + Id, r.GetString(1));
    }

    [Fact]
    public void ApplyingACarriedOutRecordTwice_ChangesNothing_AndOpensNoTransactionTheSecondTime()
    {
        var rec = ApplyRig.Rec(Id);
        _rig.Apply(rec);
        var before = _rig.Full();
        var opened = _rig.Applier.TransactionCount;

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(rec));

        Assert.Equal(before, _rig.Full());
        Assert.Equal(opened, _rig.Applier.TransactionCount);
    }

    // ---- edit ----

    [Fact]
    public void ANewerText_ReplacesTheContent_AndTheVector_AndClearsWhatTheRecordLacks()
    {
        _rig.Apply(ApplyRig.Rec(Id, project: "o/r", scope: "s", source: SourceTool.Cursor, minute: 10));
        var calls = _rig.Embedder.Calls;

        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, content: "New text.", minute: 20)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        var e = _rig.Entry(Id)!.Row;
        Assert.Equal("New text.", e.Content);
        Assert.Null(e.Project);
        Assert.Null(e.SourceTool);
        Assert.Equal(calls + 1, _rig.Embedder.Calls);
        Assert.Equal(1, _rig.VectorCount(Tier.Warm));
        AssertScanQuiet();
    }

    [Fact]
    public void ANoBaseFirstSync_WithEqualContentAndADifferentCreatedAt_AdoptsTheCreatedAt_WithoutANewVector()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, createdAt: 1_700_000_000_000L);
        var calls = _rig.Embedder.Calls;

        _rig.Apply(ApplyRig.Rec(Id, createdAt: 1_600_000_000_000L, minute: 11));

        Assert.Equal(1_600_000_000_000L, _rig.Entry(Id)!.Row.CreatedAt);
        Assert.Equal(calls, _rig.Embedder.Calls);
        Assert.NotNull(_rig.Scan.Rows()[Id].BaseJson);
        AssertScanQuiet();
    }

    [Fact]
    public void ANoBaseFirstSync_WithDifferingText_TakesTheRecord_AndFilesTheLocalText()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id, content: "Local text.");

        var result = _rig.Apply(ApplyRig.Rec(Id, content: "Their text.", minute: 11));

        // conflict review D3: NC4 is carried out; the local text is kept as the conflict item's loser
        Assert.Equal(CarriedKind.Updated, Is<EntryResult.Carried>(result).Kind);
        Assert.Equal("Their text.", _rig.Entry(Id)!.Row.Content);
        Assert.NotNull(_rig.Scan.Rows()[Id].BaseJson);
        var item = GitSyncReviews.ReadConflictItem(_rig.Scan.Connection, Id)!;
        Assert.Equal("open", item.State);
        Assert.Equal(["Local text."], GitSyncReviews.ReadConflictLosers(item.PayloadJson).Select(l => l.Text).ToArray());
        AssertScanQuiet();
    }

    [Fact]
    public void Tags_AreMergedThreeWay_LocalAdditionsSurvive_AndTheBaseTakesTheRecordsTags()
    {
        _rig.Apply(ApplyRig.Rec(Id, tags: ["a", "c"], minute: 10));
        _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new TotalRecall.Infrastructure.Storage.UpdateEntryOpts { Tags = ["a", "c", "local"] });
        _rig.RunScan();

        var result = _rig.Apply(ApplyRig.Rec(Id, tags: ["a", "theirs"], minute: 10)); // theirs removed c, added theirs

        Assert.IsType<EntryResult.Carried>(result);
        Assert.Equal(["a", "local", "theirs"], _rig.Entry(Id)!.Row.Tags.OrderBy(t => t, StringComparer.Ordinal).ToArray());
        AssertScanQuiet();
    }

    [Fact]
    public void AnUnpin_FromTheRecord_UnpinsTheLocalEntry()
    {
        _rig.Apply(ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10)); // only a hot entry can be pinned
        _rig.Scan.Pin(Id);
        _rig.Now = 1_780_000_000_000L; // the local pin is older than the record's unpin (2026-11-12)
        var scan = _rig.RunScan(); // the pin is stamped locally
        var local = _rig.Scan.Rows()[Id];
        Assert.True(local.StickyStamp is not null, $"scan sticky={scan.Sticky} tier={scan.Tier} skipped={scan.Skipped.Count} row={local} tier={_rig.Entry(Id)!.Tier}");

        var result = _rig.Apply(ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10, stickyMinute: 59));

        Assert.IsType<EntryResult.Carried>(result);
        Assert.False(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        AssertScanQuiet();
    }

    [Theory]
    [InlineData("text")]
    [InlineData("pin")]
    [InlineData("move")]
    public void AnEditBetweenTheScanAndTheApply_IsDeferred_NotOverwritten_AndTheNextScanStampsIt(string edit)
    {
        _rig.Now = 1_780_000_000_000L;
        _rig.Apply(ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10)); // hot: it can be pinned
        _rig.RunScan(); // the scan ran: nothing pending
        switch (edit)
        {
            case "text": _rig.Scan.Store.Update(Tier.Hot, ContentType.Memory, Id, new TotalRecall.Infrastructure.Storage.UpdateEntryOpts { Content = "A local edit." }); break;
            case "pin": _rig.Scan.Pin(Id); break;
            default: _rig.Scan.Store.Move(Tier.Hot, ContentType.Memory, Tier.Cold, ContentType.Memory, Id); break;
        }
        var before = _rig.Full();

        // a newer record: without the guard a text edit would be taken as "no change since the base" and overwritten
        var result = _rig.Apply(ApplyRig.Rec(Id, content: "Their newer text.", tier: Tier.Hot, minute: 20));

        Assert.IsType<EntryResult.Deferred>(result);
        Assert.Equal(before, _rig.Full());
        var scan = _rig.RunScan();
        if (edit == "move")
            Assert.Equal(Tier.Cold, _rig.Scan.Rows()[Id].LocalTier); // a demotion is recorded, not stamped
        else
            Assert.True(scan.Content + scan.Sticky >= 1, "the next scan stamps the local edit");
    }

    // ---- tier ----

    [Fact]
    public void APromotion_MovesTheEntry_KeepsTheVector_AndTheNextScanIsQuiet()
    {
        _rig.Apply(ApplyRig.Rec(Id, tier: Tier.Warm, minute: 10));
        var calls = _rig.Embedder.Calls;

        var result = _rig.Apply(ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10, tierMinute: 20));
        var carried = result is EntryResult.Failed f ? throw new Xunit.Sdk.XunitException(f.Detail) : Is<EntryResult.Carried>(result);

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal(Tier.Hot, _rig.Entry(Id)!.Tier);
        Assert.Equal(1, _rig.VectorCount(Tier.Hot));
        Assert.Equal(0, _rig.VectorCount(Tier.Warm));
        Assert.Equal(calls, _rig.Embedder.Calls);
        AssertScanQuiet();
    }

    [Fact]
    public void APromotionWithANewerText_MovesWithTheNewVector()
    {
        _rig.Apply(ApplyRig.Rec(Id, tier: Tier.Warm, minute: 10));
        var calls = _rig.Embedder.Calls;

        _rig.Apply(ApplyRig.Rec(Id, content: "Changed.", tier: Tier.Hot, minute: 20, tierMinute: 20));

        var found = _rig.Entry(Id)!;
        Assert.Equal(Tier.Hot, found.Tier);
        Assert.Equal("Changed.", found.Row.Content);
        Assert.Equal(calls + 1, _rig.Embedder.Calls);
        Assert.Equal(1, _rig.VectorCount(Tier.Hot));
        Assert.Equal(0, _rig.VectorCount(Tier.Warm));
    }

    // ---- the hot limit ----

    [Fact]
    public void TheHotLimit_StoresALongHotInsertWarm_AndListsItColder()
    {
        using var limited = new ApplyRig(hotMaxContentChars: 10);
        var rec = ApplyRig.Rec(Id, content: "A text that is longer than ten characters.", tier: Tier.Hot);

        var carried = Is<EntryResult.Carried>(limited.Apply(rec));

        Assert.True(carried.Colder);
        Assert.Equal(Tier.Warm, limited.Entry(Id)!.Tier);
        Assert.Equal(Tier.Warm, limited.Scan.Rows()[Id].LocalTier);
        var scan = limited.RunScan();
        Assert.Equal(0, scan.Tier);
    }

    [Fact]
    public void TheHotLimit_DoesNotDemoteALongHotEntry_ForAProjectOnlyChange()
    {
        using var limited = new ApplyRig(hotMaxContentChars: 10);
        limited.Scan.AddMemory(Tier.Hot, Id, content: "A text that is longer than ten characters.");
        // a record with the same text and a project: a hot entry that is already long is not demoted by this
        var result = limited.Apply(ApplyRig.Rec(Id, content: "A text that is longer than ten characters.", tier: Tier.Hot, project: "o/r"));

        Assert.IsType<EntryResult.Carried>(result);
        Assert.Equal("o/r", limited.Entry(Id)!.Row.Project!.Value);
        Assert.Equal(Tier.Hot, limited.Entry(Id)!.Tier);
    }

    [Fact]
    public void TheHotLimit_StoresAPromotionOverTheLimitWarm_AndListsItColder()
    {
        using var limited = new ApplyRig(hotMaxContentChars: 10);
        const string text = "A text that is longer than ten characters.";
        limited.Apply(ApplyRig.Rec(Id, text, tier: Tier.Warm, minute: 10));

        var carried = Is<EntryResult.Carried>(limited.Apply(ApplyRig.Rec(Id, text, tier: Tier.Hot, minute: 10, tierMinute: 20)));

        Assert.True(carried.Colder);
        Assert.Equal(Tier.Warm, limited.Entry(Id)!.Tier);
        var scan = limited.RunScan();
        Assert.Equal(0, scan.Tier);
    }

    [Fact]
    public void TheHotLimit_MovesAHotEntryWarm_WhenANewerTextIsOverTheLimit()
    {
        using var limited = new ApplyRig(hotMaxContentChars: 10);
        limited.Apply(ApplyRig.Rec(Id, "short", tier: Tier.Hot, minute: 10));

        var carried = Is<EntryResult.Carried>(limited.Apply(ApplyRig.Rec(Id, "A text that is longer than ten characters.", tier: Tier.Hot, minute: 20)));

        Assert.True(carried.Colder);
        var found = limited.Entry(Id)!;
        Assert.Equal(Tier.Warm, found.Tier);
        Assert.StartsWith("A text", found.Row.Content);
        Assert.Equal(1, limited.VectorCount(Tier.Warm));
        Assert.Equal(0, limited.VectorCount(Tier.Hot));
        var scan = limited.RunScan();
        Assert.Equal(0, scan.Tier + scan.Content);
    }

    [Fact]
    public void TheHotLimit_WithAPromotionAndANewerTextTogether_StoresTheNewTextWarm()
    {
        using var limited = new ApplyRig(hotMaxContentChars: 10);
        limited.Apply(ApplyRig.Rec(Id, "short", tier: Tier.Warm, minute: 10));

        var carried = Is<EntryResult.Carried>(limited.Apply(
            ApplyRig.Rec(Id, "A text that is longer than ten characters.", tier: Tier.Hot, minute: 20, tierMinute: 20)));

        Assert.True(carried.Colder);
        var found = limited.Entry(Id)!;
        Assert.Equal(Tier.Warm, found.Tier);
        Assert.StartsWith("A text", found.Row.Content);
        Assert.Equal(1, limited.VectorCount(Tier.Warm));
    }

    // ---- the clock ----

    /// <summary>A local hot entry and a warm record for it, with no base: the local tier wins and its stamp is issued here.</summary>
    private void LocalHotEntry() => _rig.Scan.AddMemory(Tier.Hot, Id);

    [Fact]
    public void ARestamp_OpensATransaction_IssuesAStampFromThePersistedClock_AndTheNextScanIsQuiet()
    {
        LocalHotEntry();
        Assert.Null(_rig.Scan.Meta("git_sync_hlc"));

        var result = _rig.Apply(ApplyRig.Rec(Id, tier: Tier.Warm, minute: 10));

        Assert.IsType<EntryResult.Carried>(result);
        Assert.Equal(1, _rig.Applier.TransactionCount); // the row differs from none and a stamp is issued
        var row = _rig.Scan.Rows()[Id];
        Assert.NotNull(row.TierStamp);
        Assert.NotNull(_rig.Scan.Meta("git_sync_hlc"));
        Assert.Equal(Tier.Hot, _rig.Entry(Id)!.Tier);
        AssertScanQuiet();
    }

    [Fact]
    public void ARolledBackRestamp_LeavesTheClockAsItWas()
    {
        LocalHotEntry();
        _rig.Applier.BeforeEntryStep = (_, step) => { if (step == "commit") throw new InvalidOperationException("boom"); };
        var before = _rig.Full(issues: false);

        Is<EntryResult.Failed>(_rig.Apply(ApplyRig.Rec(Id, tier: Tier.Warm, minute: 10)));

        Assert.Equal(before, _rig.Full(issues: false));
        Assert.Null(_rig.Scan.Meta("git_sync_hlc"));
        Assert.Empty(_rig.Scan.Rows());
    }

    [Fact]
    public void AStampThatCannotBeIssued_IsTheEntrysFailure_AndNothingChanges()
    {
        LocalHotEntry();
        _rig.Scan.Exec("DELETE FROM _meta WHERE key = 'git_sync_machine'");
        var before = _rig.Full(issues: false);

        var failed = Is<EntryResult.Failed>(_rig.Apply(ApplyRig.Rec(Id, tier: Tier.Warm, minute: 10)));

        Assert.NotEmpty(failed.Detail);
        Assert.Equal(before, _rig.Full(issues: false));
    }

    // ---- defer ----

    [Fact]
    public void ALeftRow_WithTheMemoryPresent_IsDeferred_AndNothingChanges()
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        _rig.Scan.Exec($"UPDATE git_sync_entry SET state = 'left' WHERE entry_id = '{Id}'");
        var before = _rig.Full();

        var result = _rig.Apply(ApplyRig.Rec(Id, content: "Newer.", minute: 20));

        Assert.IsType<EntryResult.Deferred>(result);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public void AKnowledgeEntryThatAppearsAfterTheCheck_DefersTheInsert()
    {
        _rig.Applier.BeforeTransaction = _ => _rig.Scan.Store.Insert(Tier.Warm, ContentType.Knowledge, new TotalRecall.Infrastructure.Storage.InsertEntryOpts(Content: "k", Id: Id));

        Assert.IsType<EntryResult.Deferred>(_rig.Apply(ApplyRig.Rec(Id)));
        Assert.Null(_rig.Entry(Id));
    }

    // ---- local rules ----

    [Fact]
    public void ARecordWithNoSourceTool_ClearsALocalOne()
    {
        _rig.Apply(ApplyRig.Rec(Id, source: SourceTool.Cursor, minute: 10));

        _rig.Apply(ApplyRig.Rec(Id, source: null, minute: 20, content: "Edited."));

        Assert.Null(_rig.Entry(Id)!.Row.SourceTool);
    }

    [Fact]
    public void AKnowledgeId_WithNoRow_IsNeverInsertedAsAMemory()
    {
        _rig.Scan.AddKnowledge(Tier.Warm, Id);
        var before = _rig.Full();

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(ApplyRig.Rec(Id)));

        Assert.Equal(before, _rig.Full());
        Assert.Null(_rig.Entry(Id));
    }

    [Fact]
    public void NoRecordNoRowNoEntry_WritesNoRow_OpensNoTransaction_AlsoTwice()
    {
        var before = _rig.Full();

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(Id, null));
        Assert.IsType<EntryResult.NoOp>(_rig.Apply(Id, null));

        Assert.Equal(before, _rig.Full());
        Assert.Equal(0, _rig.Applier.TransactionCount);
    }

    [Fact]
    public void AnEntryWithNoRecordAndNoRow_WritesNoRow()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id);
        var before = _rig.Full();

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(Id, null));

        Assert.Equal(before, _rig.Full());
        Assert.Empty(_rig.Scan.Rows());
    }

    [Fact]
    public void ARowWithNoBase_WhenNothingExistsAnymore_IsDropped_InATransactionOfItsOwn()
    {
        _rig.Scan.Store.Insert(Tier.Warm, ContentType.Memory, new TotalRecall.Infrastructure.Storage.InsertEntryOpts(Content: "x", Id: Id));
        _rig.RunScan(); // a row with no base
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        Assert.Contains(Id, _rig.Scan.Rows().Keys);

        var carried = Is<EntryResult.Carried>(_rig.Apply(Id, null));

        Assert.Equal(CarriedKind.Dropped, carried.Kind);
        Assert.DoesNotContain(Id, _rig.Scan.Rows().Keys);
        Assert.Equal(1, _rig.Applier.TransactionCount);
    }

    // ---- formerly postponed, now carried out ----

    [Fact]
    public void ANewPin_IsInsertedNonSticky_WithAnOpenNewPinItem()
    {
        // pin review D3: a record that pins is no longer postponed; it is inserted as an ordinary entry with a new-pin item
        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, sticky: true)));

        Assert.Equal(CarriedKind.Inserted, carried.Kind);
        Assert.Equal(Tier.Hot, _rig.Entry(Id)!.Tier);
        Assert.False(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        var item = GitSyncReviews.ReadPinItem(_rig.Scan.Connection, Id, "new-pin");
        Assert.Equal("open", item!.State);
        Assert.Equal(SyncPin.shownHash("Use bun, not npm.", null, null), item.RecordHash);
        AssertScanQuiet();
    }

    [Fact]
    public void ARecordOlderThanTheBase_IsCarriedOut_MarksTheRevertedGroups_AndLeavesTheEntryAndTheBase()
    {
        _rig.Apply(ApplyRig.Rec(Id, content: "Newer.", minute: 20));
        var before = Row();
        var calls = _rig.Embedder.Calls;

        // a record older than the base (a reset reverted it): no longer postponed, the groups are marked for the push to
        // re-publish from the base (milestone 1b-ii, D16 and D19)
        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, content: "Older.", minute: 10)));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal("content,sticky,tier", carried.Effects.Repush);
        Assert.Equal(before with { RepushGroups = "content,sticky,tier" }, Row());
        Assert.Equal("Newer.", _rig.Entry(Id)!.Row.Content);
        Assert.Equal(calls, _rig.Embedder.Calls);
        AssertScanQuiet();
    }

    [Fact]
    public void AnFC5Conflict_TakesTheRecord_AndFilesTheLocalEdit()
    {
        _rig.Now = 1_780_000_000_000L;
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new TotalRecall.Infrastructure.Storage.UpdateEntryOpts { Content = "Local edit." });
        _rig.RunScan();

        var result = _rig.Apply(ApplyRig.Rec(Id, content: "Their edit.", minute: 20));

        // conflict review D3: the record's newer text is applied, the pending stamp cleared and the local edit filed
        Assert.Equal(CarriedKind.Updated, Is<EntryResult.Carried>(result).Kind);
        Assert.Equal("Their edit.", _rig.Entry(Id)!.Row.Content);
        Assert.Null(Row().ContentStamp);
        var item = GitSyncReviews.ReadConflictItem(_rig.Scan.Connection, Id)!;
        Assert.Equal(["Local edit."], GitSyncReviews.ReadConflictLosers(item.PayloadJson).Select(l => l.Text).ToArray());
        AssertScanQuiet();
    }

    // ---- remote deletions (milestone 1b-ii, D6 to D12) ----

    private static EntryEffects Effects(EntryResult r)
    {
        var carried = r is EntryResult.Failed f ? throw new Xunit.Sdk.XunitException(f.Detail) : Is<EntryResult.Carried>(r);
        Assert.Equal(CarriedKind.Updated, carried.Kind);
        return carried.Effects;
    }

    private GitSyncRow Row(string id = Id) => _rig.Scan.Rows()[id];

    private static int Cmp(SyncStamp.Stamp a, SyncStamp.Stamp b) => SyncStamp.StampModule.compare(a, b);

    private void SetRowColumn(string column, string? value) =>
        _rig.Scan.Exec($"UPDATE git_sync_entry SET {column} = {(value is null ? "NULL" : "'" + value + "'")} WHERE entry_id = '{Id}'");

    /// <summary>A synced entry (row active with a base at minute 10), then a local change the scan stamps.</summary>
    private void SyncedThenChanged(string change)
    {
        _rig.Apply(ApplyRig.Rec(Id, tier: change == "pin" ? Tier.Hot : Tier.Warm, minute: 10));
        switch (change)
        {
            case "text": _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new TotalRecall.Infrastructure.Storage.UpdateEntryOpts { Content = "A local edit." }); break;
            case "tags": _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new TotalRecall.Infrastructure.Storage.UpdateEntryOpts { Tags = ["local"] }); break;
            case "project": _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new TotalRecall.Infrastructure.Storage.UpdateEntryOpts { Project = "o/r" }); break;
            case "tier": _rig.Scan.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, Id); break;
            case "pin": _rig.Scan.Pin(Id); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        _rig.RunScan();
    }

    [Fact]
    public void ProvenRemoteDeletion_DeletesTheEntryAndItsVector_AndMarksTheRowForgottenKeepingTheBase()
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        SetRowColumn("pushing_stamps", "{\"content\":\"" + SyncStamp.StampModule.format(ApplyRig.St(10)) + "\"}");
        var baseJson = Row().BaseJson;
        Assert.NotNull(baseJson);
        Assert.Equal(1, _rig.VectorCount(Tier.Warm));

        var effects = Effects(_rig.Apply(Id, null));

        Assert.True(effects.Deleted);
        Assert.Null(effects.Held);
        Assert.False(effects.Closed);
        Assert.Empty(effects.Notes);
        Assert.Null(_rig.Entry(Id));
        Assert.Equal(0, _rig.VectorCount(Tier.Warm));
        var row = Row();
        Assert.Equal(SyncMerge.RowState.Forgotten, row.State);
        Assert.Equal(baseJson, row.BaseJson);
        Assert.Null(row.ContentStamp);
        Assert.Null(row.StickyStamp);
        Assert.Null(row.TierStamp);
        Assert.Null(row.PushingStamps);
        Assert.Equal(1, _rig.ImportLogRows(Id)); // import_log is left as it is
        Assert.Equal(0, _rig.ReviewRows());
        var scan = _rig.RunScan();
        Assert.Empty(scan.LocalDeletions);
        AssertScanQuiet();
    }

    [Theory]
    [InlineData("tags")]
    [InlineData("tier")]
    [InlineData("project")]
    public void RemoteDeletion_WithPendingTagTierProjectChange_StillDeletes_AndNotesTheDroppedChanges(string change)
    {
        SyncedThenChanged(change);

        var effects = Effects(_rig.Apply(Id, null));

        Assert.True(effects.Deleted);
        Assert.Equal(["pending-changes-dropped"], effects.Notes);
        Assert.Null(_rig.Entry(Id));
        if (change != "tier") // the plain store's Move, used to promote here, leaves the vector behind in the warm table
            Assert.Equal(0, _rig.VectorCount(Tier.Warm));
        var row = Row();
        Assert.Equal(SyncMerge.RowState.Forgotten, row.State);
        Assert.Null(row.ContentStamp);
        Assert.Null(row.TierStamp);
    }

    private void AssertHeld(EntryResult result, string cause)
    {
        var effects = Effects(result);
        Assert.Equal(new HeldEntry(Id, "remote", cause), effects.Held);
        Assert.False(effects.Deleted);
        var item = Assert.Single(GitSyncReviews.ReadOpenHeld(_rig.Scan.Connection)).Value;
        Assert.Equal(new HeldItem(Id, "remote", null, cause, _rig.Now), item);
        Assert.NotNull(_rig.Entry(Id));
    }

    [Fact]
    public void RemoteDeletion_OfAStickyEntry_IsHeldWithCauseSticky()
    {
        SyncedThenChanged("pin");
        SetRowColumn("sticky_stamp", null); // the pin is in the repository: no pending stamp, the entry is sticky
        var row = Row();

        AssertHeld(_rig.Apply(Id, null), "sticky");

        Assert.True(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        Assert.Equal(row, Row());
    }

    [Fact]
    public void RemoteDeletion_WithAPendingStickyStamp_IsHeldWithCauseSticky()
    {
        SyncedThenChanged("pin"); // the pin is only pending, not yet in the repository
        var row = Row();
        Assert.NotNull(row.StickyStamp);

        AssertHeld(_rig.Apply(Id, null), "sticky");

        Assert.True(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        Assert.Equal(row, Row()); // the pending pin stays pending
    }

    [Fact]
    public void RemoteDeletion_InAnUnprovenApply_IsHeldWithCauseUnproven_AndNothingIsDeleted()
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        var row = Row();

        AssertHeld(_rig.Apply(Id, null, proven: false), "unproven");

        Assert.Equal(row, Row());
        Assert.Equal(1, _rig.VectorCount(Tier.Warm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteDeletion_WithAPendingTextEdit_KeepsTheEntry_IssuesAFreshContentStamp_AndNotesEditBeatsDeletion(bool baseAbovePending)
    {
        SyncedThenChanged("text");
        if (baseAbovePending)
            SetRowColumn("content_stamp", SyncStamp.StampModule.format(ApplyRig.St(5))); // older than the base's (minute 10)
        _rig.SeedHeld(Id);
        var oldPending = Row().ContentStamp!;
        var entryBefore = _rig.Entry(Id)!;
        var dump = _rig.Scan.Dump();

        var effects = Effects(_rig.Apply(Id, null));

        Assert.False(effects.Deleted);
        Assert.Null(effects.Held);
        Assert.True(effects.Closed);
        Assert.Equal(["edit-beats-deletion"], effects.Notes);
        Assert.Equal(dump, _rig.Scan.Dump()); // the entry, its vector and the import log are untouched
        Assert.Equal("A local edit.", _rig.Entry(Id)!.Row.Content);
        Assert.Equal(entryBefore, _rig.Entry(Id));
        var fresh = Row().ContentStamp!;
        Assert.True(Cmp(fresh, oldPending) > 0, "above the old pending stamp");
        Assert.True(Cmp(fresh, ApplyRig.St(10)) > 0, "above the base's content stamp");
        Assert.Equal(SyncMerge.RowState.Active, Row().State);
        Assert.Null(_rig.Held(Id));
        Assert.NotNull(_rig.Scan.Meta("git_sync_hlc"));
    }

    [Theory]
    [InlineData("unseen")]
    [InlineData("declined")]
    [InlineData("resolved")]
    public void AHeldDeleteRowInAnotherState_IsNotAnItem_TheRemoteDeletionIsCarriedOut(string state)
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        using (var cmd = _rig.Scan.Connection.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO git_sync_review (entry_id, kind, state, direction, arrived_at) VALUES ($id, 'held-delete', $state, 'remote', 1)";
            cmd.Parameters.AddWithValue("$id", Id);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.ExecuteNonQuery();
        }

        // the pull reads only open and approved items, so it passes none for this id
        var effects = Effects(_rig.Applier.ApplyOne(Id, null, proven: true, held: null, blobSha: null, guard: default));

        Assert.True(effects.Deleted);
        Assert.Null(_rig.Entry(Id));
    }

    [Fact]
    public void RecordReappearing_ClosesTheOpenRemoteItem()
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        _rig.SeedHeld(Id);
        var row = Row();

        var effects = Effects(_rig.Apply(ApplyRig.Rec(Id, minute: 10)));

        Assert.True(effects.Closed);
        Assert.Equal(0, _rig.ReviewRows());
        Assert.Equal(row, Row());
    }

    [Fact]
    public void PendingTextEdit_ClosesTheOpenRemoteItem()
    {
        SyncedThenChanged("text");
        _rig.SeedHeld(Id);
        _rig.SeedHeld(Id2); // an unrelated entry's item is untouched

        Assert.True(Effects(_rig.Apply(Id, null)).Closed);

        Assert.Null(_rig.Held(Id));
        Assert.NotNull(_rig.Held(Id2));
        Assert.NotNull(_rig.Entry(Id));
    }

    [Fact]
    public void PendingTagChange_DoesNotCloseTheOpenItem()
    {
        SyncedThenChanged("tags");
        _rig.SeedHeld(Id);
        var before = _rig.Full();

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(Id, null));

        Assert.Equal(before, _rig.Full());
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("tier")]
    [InlineData("project")]
    public void PendingPinTierOrProjectChange_DoesNotCloseTheOpenItem(string change)
    {
        SyncedThenChanged(change);
        _rig.SeedHeld(Id);
        var before = _rig.Full();

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(Id, null));

        Assert.Equal(before, _rig.Full());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenRemoteItem_LeavesTheEntryAlone_NeitherDeletedNorRehold(bool proven)
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        _rig.SeedHeld(Id);
        var before = _rig.Full();
        var opened = _rig.Applier.TransactionCount;

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(Id, null, proven));

        Assert.Equal(before, _rig.Full());
        Assert.Equal(opened, _rig.Applier.TransactionCount);
    }

    /// <summary>The entry is gone here and its row is forgotten (deleted by a remote deletion) or still active with a base (deleted locally).</summary>
    private void EntryGone(string rowState)
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        if (rowState == "forgotten")
            Effects(_rig.Apply(Id, null));
        else
            _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        Assert.Null(_rig.Entry(Id));
    }

    [Theory]
    [InlineData("forgotten")]
    [InlineData("active")]
    public void NewerTextThanTheDeletion_ReinsertsNonSticky_ClosesTheItem_AndNotes(string rowState)
    {
        EntryGone(rowState);
        _rig.SeedHeld(Id);

        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, content: "Newer text.", tier: Tier.Hot, minute: 20)));

        Assert.Equal(CarriedKind.Inserted, carried.Kind);
        Assert.True(carried.Effects.Closed);
        Assert.Equal(["edit-beats-deletion"], carried.Effects.Notes);
        Assert.Equal("Newer text.", _rig.Entry(Id)!.Row.Content);
        Assert.False(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        Assert.Equal(SyncMerge.RowState.Active, Row().State);
        Assert.Equal(0, _rig.ReviewRows());
        AssertScanQuiet();
    }

    [Theory]
    [InlineData("forgotten")]
    [InlineData("active")]
    public void NewerTextThanTheDeletion_ThatPins_IsInsertedNonSticky_WithANewPinItem(string rowState)
    {
        EntryGone(rowState);

        // W3 (forgotten) and W8.1 (active): pin review D3 inserts the record non-sticky and opens a new-pin item
        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, content: "Newer text.", sticky: true, minute: 20)));

        Assert.Equal(CarriedKind.Inserted, carried.Kind);
        Assert.Equal(["edit-beats-deletion"], carried.Effects.Notes);
        Assert.Equal("Newer text.", _rig.Entry(Id)!.Row.Content);
        Assert.False(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        Assert.Equal(SyncMerge.RowState.Active, Row().State);
        var item = GitSyncReviews.ReadPinItem(_rig.Scan.Connection, Id, "new-pin");
        Assert.Equal("open", item!.State);
        Assert.Equal(SyncPin.shownHash("Newer text.", null, null), item.RecordHash);
        AssertScanQuiet();
    }

    [Fact]
    public void ForgottenRow_WithTheSameTextRecord_IsNotReinserted()
    {
        EntryGone("forgotten");
        var before = _rig.Full();

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(ApplyRig.Rec(Id, minute: 20))); // a newer stamp, the same text

        Assert.Equal(before, _rig.Full());
        Assert.Null(_rig.Entry(Id));
    }

    [Fact]
    public void ForgottenRow_WithOlderTextAfterAReset_IsNotReinserted()
    {
        EntryGone("forgotten");
        var before = _rig.Full();

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(ApplyRig.Rec(Id, content: "Older text.", minute: 5)));

        Assert.Equal(before, _rig.Full());
        Assert.Null(_rig.Entry(Id));
    }

    [Fact]
    public void RowActiveWithBase_RecordAbsent_EntryGone_BecomesForgotten()
    {
        EntryGone("active");
        SetRowColumn("tier_stamp", SyncStamp.StampModule.format(ApplyRig.St(30)));
        var baseJson = Row().BaseJson;
        var dump = _rig.Scan.Dump();

        var effects = Effects(_rig.Apply(Id, null));

        Assert.False(effects.Deleted); // the deletion was already made
        Assert.Equal(dump, _rig.Scan.Dump());
        var row = Row();
        Assert.Equal(SyncMerge.RowState.Forgotten, row.State);
        Assert.Equal(baseJson, row.BaseJson);
        Assert.Null(row.TierStamp);
        Assert.IsType<EntryResult.NoOp>(_rig.Apply(Id, null)); // and the next pull changes nothing
    }

    [Fact]
    public void ADroppedRow_ClosesItsOpenItem_InTheSameTransaction()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id);
        _rig.RunScan(); // a row with no base
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        _rig.SeedHeld(Id, "local");

        var carried = Is<EntryResult.Carried>(_rig.Apply(Id, null));

        Assert.Equal(CarriedKind.Dropped, carried.Kind);
        Assert.True(carried.Effects.Closed);
        Assert.DoesNotContain(Id, _rig.Scan.Rows().Keys);
        Assert.Equal(0, _rig.ReviewRows());
        Assert.Equal(1, _rig.Applier.TransactionCount);
    }

    [Fact]
    public void ANoBaseRow_WhoseEntryIsGone_AndAnotherMachinesRecordIsPresent_BecomesForgottenWithTheRecordAsBase()
    {
        // an entry that was scanned (a row with no base), then deleted, while another record is present (W9)
        _rig.Scan.AddMemory(Tier.Warm, Id);
        _rig.RunScan();
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);

        Effects(_rig.Apply(ApplyRig.Rec(Id, minute: 10), proven: false));

        var row = Row();
        Assert.Equal(SyncMerge.RowState.Forgotten, row.State);
        Assert.NotNull(row.BaseJson);
        Assert.Null(row.ContentStamp);
        Assert.Null(_rig.Entry(Id));
    }

    // ---- local deletions (milestone 1b-ii, D7 to D9 and D12) ----

    private const string BlobSha = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>A synced entry (row active with a base at minute 10) that is then deleted here; the scan reports it.</summary>
    private void SyncedThenDeletedHere()
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        Assert.Contains(_rig.RunScan().LocalDeletions, d => d.Id == Id);
    }

    private static SyncRecord.SyncRecord BaseOf(GitSyncRow row) =>
        RecordReader.Parse(System.Text.Encoding.UTF8.GetBytes(row.BaseJson!), SyncValidation.recordPath(row.EntryId)).Parsed!.Record;

    [Fact]
    public void ProvenLocalDeletion_WithTheRecordUnchanged_IsListedInPushDeletions_AndChangesNoStoreRow()
    {
        SyncedThenDeletedHere();
        SetRowColumn("tier_stamp", SyncStamp.StampModule.format(ApplyRig.St(30))); // a change made before the deletion
        var baseJson = Row().BaseJson;
        var dump = _rig.Scan.Dump();

        var effects = Effects(_rig.Apply(ApplyRig.Rec(Id, minute: 10)));

        Assert.True(effects.PushDeletion);
        Assert.False(effects.Deleted);
        Assert.Null(effects.Held);
        Assert.False(effects.Closed);
        Assert.Empty(effects.Notes);
        Assert.Equal(dump, _rig.Scan.Dump()); // no store row is written
        Assert.Null(_rig.Entry(Id));
        var row = Row();
        Assert.Equal(SyncMerge.RowState.Active, row.State); // forgotten only once the push removed the record
        Assert.Equal(baseJson, row.BaseJson);               // W8.3: the record equals the base
        Assert.Null(row.ContentStamp);
        Assert.Null(row.StickyStamp);
        Assert.Null(row.TierStamp);
        Assert.Equal(0, _rig.ReviewRows());

        // the row now agrees with the decision: the id is still reported, as an unchanged entry
        var again = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, minute: 10)));
        Assert.Equal(CarriedKind.Unchanged, again.Kind);
        Assert.True(again.Effects.PushDeletion);
        Assert.Equal(row, Row());
        Assert.Equal(dump, _rig.Scan.Dump());
    }

    [Fact]
    public void ProvenLocalDeletion_TakesTheRecordsNewerPlacementIntoTheBase()
    {
        SyncedThenDeletedHere();

        var effects = Effects(_rig.Apply(ApplyRig.Rec(Id, tier: Tier.Hot, minute: 10, tierMinute: 20)));

        Assert.True(effects.PushDeletion);
        var b = BaseOf(Row());
        Assert.Equal(Tier.Hot, b.Placement.Tier);
        Assert.Equal(ApplyRig.St(20), b.Placement.TierStamp);
        Assert.Equal(ApplyRig.St(10), b.ContentStamp);
        Assert.Null(_rig.Entry(Id));
    }

    [Fact]
    public void ProvenLocalDeletion_WithAnOpenFailure_RemovesTheFailureRow()
    {
        SyncedThenDeletedHere();
        _rig.Scan.Exec($"INSERT INTO git_sync_issue (entry_id, kind, record_hash, attempts, detail) VALUES ('{Id}', 'apply-failed', 'old', 2, 'old')");

        Assert.True(Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, minute: 10))).Effects.PushDeletion);

        Assert.Empty(_rig.Scan.Issues());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnPushWithNoBase_AndTheRecordInR_IsAPushDeletion(bool proven)
    {
        _rig.Scan.AddMemory(Tier.Warm, Id);
        _rig.RunScan(); // a row with no base and pending stamps
        SetRowColumn("pushing_stamps", "{\"content\":\"" + SyncStamp.StampModule.format(ApplyRig.St(10)) + "\"}"); // this row sent the record
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        var dump = _rig.Scan.Dump();

        var effects = Effects(_rig.Apply(ApplyRig.Rec(Id, minute: 10), proven));

        Assert.True(effects.PushDeletion);
        Assert.Null(effects.Held);
        Assert.Equal(dump, _rig.Scan.Dump());
        var row = Row();
        Assert.Equal(SyncMerge.RowState.Active, row.State);
        Assert.Equal(ApplyRig.St(10), BaseOf(row).ContentStamp); // the own push is adopted as the base
        Assert.Null(row.ContentStamp);
        Assert.Null(row.StickyStamp);
        Assert.Null(row.TierStamp);
        Assert.Equal(0, _rig.ReviewRows());
    }

    [Fact]
    public void NoBaseRecordPresentNotOwnPush_BecomesForgottenWithTheBaseTheRecord()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id);
        _rig.RunScan(); // a row with no base, never pushed
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);

        var effects = Effects(_rig.Apply(ApplyRig.Rec(Id, minute: 10), proven: true));

        Assert.False(effects.PushDeletion);
        Assert.Null(effects.Held);
        var row = Row();
        Assert.Equal(SyncMerge.RowState.Forgotten, row.State);
        Assert.Equal(ApplyRig.St(10), BaseOf(row).ContentStamp);
        Assert.Null(row.ContentStamp);
        Assert.Empty(_rig.RunScan().LocalDeletions);
        Assert.Null(_rig.Entry(Id));
    }

    [Fact]
    public void LocalDeletion_InAnUnprovenApply_IsHeldLocalWithCauseUnproven_AndNotListedForPush()
    {
        SyncedThenDeletedHere();
        SetRowColumn("tier_stamp", SyncStamp.StampModule.format(ApplyRig.St(30)));
        var baseJson = Row().BaseJson;
        var dump = _rig.Scan.Dump();

        var effects = Effects(_rig.Apply(ApplyRig.Rec(Id, minute: 10), proven: false, blobSha: BlobSha));

        Assert.Equal(new HeldEntry(Id, "local", "unproven"), effects.Held);
        Assert.False(effects.PushDeletion);
        Assert.False(effects.Deleted);
        var item = Assert.Single(GitSyncReviews.ReadOpenHeld(_rig.Scan.Connection)).Value;
        Assert.Equal(new HeldItem(Id, "local", BlobSha, "unproven", _rig.Now), item);
        Assert.Equal(dump, _rig.Scan.Dump()); // the entry stays absent
        var row = Row();
        Assert.Equal(SyncMerge.RowState.Active, row.State);
        Assert.Equal(baseJson, row.BaseJson); // the base keeps the text a later restore needs
        Assert.Null(row.TierStamp);           // the pending stamps are cleared: the entry is gone
    }

    [Fact]
    public void LocalDeletion_WhenTheRecordTextIsNewer_ReinsertsTheEntry()
    {
        SyncedThenDeletedHere();

        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, content: "Newer text.", minute: 20)));

        Assert.Equal(CarriedKind.Inserted, carried.Kind);
        Assert.False(carried.Effects.PushDeletion);
        Assert.Equal(["edit-beats-deletion"], carried.Effects.Notes);
        Assert.Equal("Newer text.", _rig.Entry(Id)!.Row.Content);
        Assert.False(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        Assert.Equal(SyncMerge.RowState.Active, Row().State);
        Assert.Empty(_rig.RunScan().LocalDeletions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenLocalItem_LeavesTheDeletionHeld(bool proven)
    {
        SyncedThenDeletedHere();
        _rig.SeedHeld(Id, "local", hash: BlobSha);
        var before = _rig.Full();

        Assert.IsType<EntryResult.NoOp>(_rig.Apply(ApplyRig.Rec(Id, minute: 10), proven, BlobSha));

        Assert.Equal(before, _rig.Full());
        Assert.NotNull(_rig.Held(Id));
    }

    [Fact]
    public void OpenLocalItem_WithAStaleHash_SkipsTheNoOpShortcut_AndRefreshesTheHashOnly()
    {
        SyncedThenDeletedHere();
        _rig.SeedHeld(Id, "local", hash: "89abcdef0123456789abcdef0123456789abcdef");
        var row = Row();
        _rig.NewApplier(); // count only this apply's transactions

        var carried = Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, minute: 10), proven: true, BlobSha));

        Assert.Equal(CarriedKind.Updated, carried.Kind);
        Assert.Equal(BlobSha, _rig.Held(Id)!.RecordHash);
        Assert.Equal(row, Row());
        Assert.Equal(1, _rig.Applier.TransactionCount);
    }

    [Fact]
    public void OpenRemoteItem_ThenEntryDeletedHere_ReplacesItWithOneLocalItem()
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        _rig.SeedHeld(Id, "remote");
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        _rig.RunScan();

        var effects = Effects(_rig.Apply(ApplyRig.Rec(Id, minute: 10), proven: false, blobSha: BlobSha));

        Assert.True(effects.Closed);
        Assert.Equal(new HeldEntry(Id, "local", "unproven"), effects.Held);
        Assert.Equal(1, _rig.ReviewRows());
        Assert.Equal(new HeldItem(Id, "local", BlobSha, "unproven", _rig.Now), _rig.Held(Id));
    }

    [Fact]
    public void ForgottenRow_FromADeletionThatPushed_IsNotAVisitedLocalDeletionAgain()
    {
        SyncedThenDeletedHere();
        Assert.True(Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id, minute: 10))).Effects.PushDeletion);
        SetRowColumn("state", "forgotten"); // what the finalize of the push that removed the record does

        Assert.Empty(_rig.RunScan().LocalDeletions);
        var before = _rig.Full();
        Assert.IsType<EntryResult.NoOp>(_rig.Apply(ApplyRig.Rec(Id, minute: 10))); // the record still at R
        Assert.IsType<EntryResult.NoOp>(_rig.Apply(Id, null));                      // the record removed
        Assert.Equal(before, _rig.Full());
    }

    // ---- defer and skip ----

    [Fact]
    public void AnIdInTwoTiers_IsSkipped_AndNothingChanges()
    {
        _rig.Scan.AddMemory(Tier.Warm, Id);
        _rig.Scan.AddMemory(Tier.Cold, Id);
        var before = _rig.Full(issues: false);

        Is<EntryResult.Skipped>(_rig.Apply(ApplyRig.Rec(Id)));

        Assert.Equal(before, _rig.Full(issues: false)); // the skip is recorded as an issue, nothing else
    }

    [Fact]
    public void AnIdThatIsNotEligible_IsSkipped()
    {
        Is<EntryResult.Skipped>(_rig.Apply("../x", null));
    }

    [Fact]
    public void ABaseThatCannotBeRead_IsTheEntrysFailure_NeverNoBase()
    {
        _rig.Apply(ApplyRig.Rec(Id, minute: 10));
        _rig.Scan.Exec($"UPDATE git_sync_entry SET base_json = 'not json' WHERE entry_id = '{Id}'");
        var before = _rig.Full();

        var failed = Is<EntryResult.Failed>(_rig.Apply(ApplyRig.Rec(Id, content: "Newer.", minute: 20)));

        Assert.Contains("base", failed.Detail);
        Assert.Equal(before, _rig.Full(issues: false)); // only the failure's issue row was written
        Assert.Equal(1, failed.Attempts);
    }

    [Fact]
    public void AnEmbedderThatFails_IsFailed_BeforeAnyTransaction()
    {
        _rig.Embedder.Fails = _ => true;
        var before = _rig.Full(issues: false);

        var failed = Is<EntryResult.Failed>(_rig.Apply(ApplyRig.Rec(Id)));

        Assert.Contains("embedder", failed.Detail);
        Assert.Equal(before, _rig.Full(issues: false));
        Assert.Equal(1, _rig.Applier.TransactionCount); // only the one that writes the failure
    }
}
