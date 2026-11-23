using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-iii (held deletions), D4: how the pull treats an approved local item. In the shape of W8 (row active with
/// a base, entry gone, record present) the item is decided as proven with no held item, so its record's blob id equal to
/// the item's hash gives an uncounted <c>PushDeletion</c> even in an unproven pull and a changed record re-opens the item;
/// a newer text, a vanished record, a re-created entry, a scan-skipped id and a re-created entry whose record is rejected or
/// invalid remove the item. Each test starts from a local hold of an unproven pull that is then approved.
/// </summary>
public sealed class GitSyncApplierApprovedTests : IDisposable
{
    private const string A = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string B = "3f9c2a7e-0000-4000-8000-0000000000b2";

    private readonly PullRig _rig = new();
    private readonly GitSyncApplier _applier;
    private long _now = 1_780_000_000_000L;

    public GitSyncApplierApprovedTests()
    {
        _applier = new GitSyncApplier(_rig.Client, _rig.Store, new CountingEmbedder(), new ApplyOptions(0), () => _now);
        _rig.Join();
        _rig.PutManifest();
        _rig.Commit("manifest");
        _rig.Clone();
    }

    public void Dispose() => _rig.Dispose();

    private ScanSummary Scan() => Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(_rig.Connection, () => _now).Scan()).Summary;

    private async Task<ApplySummary> Pull()
    {
        _now += 60_000;
        var fetch = await _rig.Fetcher().FetchAsync();
        var result = fetch switch
        {
            FetchOutcome.Fetched f => f.Result,
            FetchOutcome.NothingNew n => n.Result,
            _ => throw new Xunit.Sdk.XunitException("the fetch did not succeed: " + fetch),
        };
        return Assert.IsType<ApplyOutcome.Done>(await _applier.ApplyAsync(result, Scan())).Summary;
    }

    private static string IdOf(int i) => $"3f9c2a7e-0000-4000-8000-{i:x12}";

    private static SyncRecordModule.SyncRecord Rec(string id, string content = "Use bun, not npm.", IEnumerable<string>? tags = null, int minute = 10) =>
        ApplyRig.Rec(id, content, tags: tags, minute: minute);

    private string BlobOf(string id) => _rig.Source.Git("rev-parse", "HEAD:" + SyncValidation.recordPath(id)).Trim();

    private HeldItem? Item(string id) => GitSyncReviews.ReadHeldItem(_rig.Connection, id);

    private GitSyncRow Row(string id) => GitSyncRows.Read(_rig.Connection, id)!;

    private bool Has(string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(t => _rig.Store.Get(t, ContentType.Memory, id) is not null);

    private void Unproven() => _rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");

    private void Recreate(string id) =>
        _rig.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(
            Content: "Use bun, not npm.", Id: id, EntryType: EntryType.Correction, CreatedAt: 1_700_000_000_000L));

    /// <summary>
    /// The ids pulled (rows with bases, <c>L</c> set), then deleted here, held by an unproven pull (open local items with
    /// the records' blob ids) and approved. Returns the approved items.
    /// </summary>
    private async Task<Dictionary<string, HeldItem>> ApprovedLocalHolds(params string[] ids)
    {
        foreach (var id in ids)
            _rig.PutRecord(Rec(id));
        _rig.PutRecord(Rec(B, "Other."));
        _rig.Commit("records");
        await Pull();
        foreach (var id in ids)
            _rig.Store.Delete(Tier.Warm, ContentType.Memory, id);
        Unproven();
        var held = await Pull();
        Assert.False(held.Proven);
        var items = new Dictionary<string, HeldItem>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            Assert.Equal(new HeldItem(id, "local", BlobOf(id), "unproven", _now), Item(id));
            GitSyncReviews.ApproveHeld(_rig.Connection, id);
            items[id] = Item(id)!;
            Assert.Equal("approved", items[id].State);
        }
        return items;
    }

    [GitFact]
    public async Task AnApprovedItem_InAnUnprovenPull_IsAPushDeletion_AndNotHeldAgain()
    {
        var approved = (await ApprovedLocalHolds(A))[A];
        Unproven();

        var summary = await Pull();

        Assert.False(summary.Proven);
        Assert.Equal([A], summary.PushDeletions);
        Assert.Empty(summary.Held);
        Assert.Empty(summary.GuardTripped);
        Assert.Equal(approved, Item(A));
        Assert.True(summary.Complete);
    }

    [GitFact]
    public async Task ApprovedItems_AreNotCountedByThePreview()
    {
        var ids = Enumerable.Range(1, 11).Select(IdOf).ToArray(); // eleven is over the small-store limit of ten
        var approved = await ApprovedLocalHolds(ids);

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.Empty(summary.GuardTripped);
        Assert.Equal(ids, summary.PushDeletions);
        Assert.Empty(summary.Held);
        Assert.All(ids, id => Assert.Equal(approved[id], Item(id)));
        Assert.True(summary.Complete);
    }

    [GitFact]
    public async Task AChangedRecord_ReopensTheItem_WithTheNewHash_AndDeletesNothing()
    {
        var approved = (await ApprovedLocalHolds(A))[A];
        _rig.PutRecord(Rec(A, tags: ["retagged"]));
        _rig.Commit("retag a on another machine");

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.Empty(summary.PushDeletions);
        Assert.Empty(summary.Held);
        var item = Item(A)!;
        Assert.Equal("open", item.State);
        Assert.Equal(BlobOf(A), item.RecordHash);
        Assert.NotEqual(approved.RecordHash, item.RecordHash);
        Assert.Equal(approved with { State = "open", RecordHash = item.RecordHash }, item);
        Assert.False(Has(A));
        Assert.Equal(SyncMerge.RowState.Active, Row(A).State);
        Assert.True(summary.Complete);
    }

    [GitFact]
    public async Task ANewerText_BeatsTheApprovedDeletion()
    {
        await ApprovedLocalHolds(A);
        _rig.PutRecord(Rec(A, "A newer text.", minute: 20));
        _rig.Commit("edit a on another machine");

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.Equal("A newer text.", _rig.Store.Get(Tier.Warm, ContentType.Memory, A)!.Content);
        Assert.Null(Item(A));
        Assert.Equal([A], summary.ClosedHeld);
        Assert.Empty(summary.PushDeletions);
        Assert.True(summary.Complete);
    }

    [GitFact]
    public async Task AVanishedRecord_ForgetsTheRow_AndRemovesTheItem()
    {
        await ApprovedLocalHolds(A);
        _rig.Remove(SyncValidation.recordPath(A));
        _rig.Commit("remove a on another machine");

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.Equal(SyncMerge.RowState.Forgotten, Row(A).State);
        Assert.Null(Item(A));
        Assert.Equal([A], summary.ClosedHeld);
        Assert.Empty(summary.PushDeletions);
        Assert.True(summary.Complete);
    }

    [GitFact]
    public async Task ARecreatedEntry_RemovesTheItem_AndALaterDeletionIsHeldAsUsual()
    {
        await ApprovedLocalHolds(A);
        Recreate(A); // under the still-active row, with the same content: the record did not change

        var proven = await Pull();

        Assert.True(proven.Proven);
        Assert.Null(Item(A));
        Assert.Equal([A], proven.ClosedHeld);
        Assert.Empty(proven.PushDeletions);
        Assert.True(proven.Complete);

        _rig.Store.Delete(Tier.Warm, ContentType.Memory, A);
        Unproven();

        var unproven = await Pull();

        Assert.False(unproven.Proven);
        Assert.Empty(unproven.PushDeletions);
        Assert.Equal([new HeldEntry(A, "local", "unproven")], unproven.Held);
        Assert.Equal(new HeldItem(A, "local", BlobOf(A), "unproven", _now), Item(A));
    }

    [GitFact]
    public async Task AScanSkippedId_RemovesItsApprovedItem_AndDeletesNothing()
    {
        await ApprovedLocalHolds(A);
        Recreate(A);
        ScanRig.Exec(_rig.Connection, $"INSERT INTO cold_memories SELECT * FROM warm_memories WHERE id = '{A}'"); // in two tiers

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.Contains(summary.Skipped, s => s.Id == A);
        Assert.Null(Item(A));
        Assert.Contains(A, summary.ClosedHeld);
        Assert.Empty(summary.PushDeletions);
        Assert.NotNull(_rig.Store.Get(Tier.Warm, ContentType.Memory, A));
        Assert.NotNull(_rig.Store.Get(Tier.Cold, ContentType.Memory, A));
    }

    /// <summary>Replaces A's record in the source with one the pull does not read: a rejected path or an invalid record.</summary>
    private void UnreadableRecordOfA(string shape)
    {
        var path = SyncValidation.recordPath(A);
        if (shape == "rejected")
        {
            // the record's own bytes under a mode the listing rejects: only the mode differs
            var bytes = RecordWriter.Write(Rec(A)).Bytes!;
            _rig.Remove(path);
            _rig.PutRaw(path, bytes, "120000");
        }
        else
        {
            _rig.PutText(path, "{\"format\":\"total-recall/memory/v9\"}");
        }
        _rig.Commit("a record of A that is not read");
    }

    [GitTheory]
    [InlineData("rejected", true)]
    [InlineData("rejected", false)]
    [InlineData("invalid", true)]
    [InlineData("invalid", false)]
    public async Task ARecreatedEntry_WhoseRecordIsNotRead_RemovesTheItem_AndChangesNothingElse(string shape, bool proven)
    {
        await ApprovedLocalHolds(A);
        Recreate(A);
        UnreadableRecordOfA(shape);
        if (!proven)
            Unproven();
        Scan(); // the scan of the re-created entry, which the pull's own scan repeats unchanged
        var row = Row(A);
        var entry = _rig.Store.Get(Tier.Warm, ContentType.Memory, A);

        var summary = await Pull();

        Assert.Equal(proven, summary.Proven);
        Assert.Null(Item(A));
        Assert.Equal([A], summary.ClosedHeld);
        Assert.Empty(summary.Deferred);
        Assert.Empty(summary.PushDeletions);
        Assert.Empty(summary.Held);
        Assert.Empty(summary.Deleted);
        Assert.DoesNotContain(summary.Skipped, s => s.Id == A);
        Assert.Single(summary.Rejected);
        Assert.Equal(row, Row(A));
        Assert.Equal(entry, _rig.Store.Get(Tier.Warm, ContentType.Memory, A));
        Assert.True(summary.Complete);
    }

    [GitTheory]
    [InlineData("scan-skipped")]
    [InlineData("rejected")]
    public async Task AFaultAtTheCloseOfAnApprovedItem_LeavesItApproved_AndDefersTheId(string shape)
    {
        var approved = (await ApprovedLocalHolds(A))[A];
        Recreate(A);
        if (shape == "scan-skipped")
            ScanRig.Exec(_rig.Connection, $"INSERT INTO cold_memories SELECT * FROM warm_memories WHERE id = '{A}'"); // in two tiers
        else
            UnreadableRecordOfA(shape);
        _applier.BeforeEntryStep = (id, step) =>
        {
            if (id == A && step == "close")
                throw new InvalidOperationException("fault at close");
        };

        var summary = await Pull();

        Assert.Equal(approved, Item(A));
        var deferred = Assert.Single(summary.Deferred);
        Assert.Equal(A, deferred.Id);
        Assert.Contains("fault at close", deferred.Reason);
        Assert.DoesNotContain(A, summary.ClosedHeld);
        Assert.False(summary.Complete);
    }

    [GitFact]
    public async Task AnApprovedItem_DoesNotDeferTheApply()
    {
        var approved = (await ApprovedLocalHolds(A))[A];

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.Empty(summary.Deferred);
        Assert.True(summary.Complete);
        Assert.Equal(approved, Item(A));
        Assert.Contains(A, summary.PushDeletions);
    }
}
