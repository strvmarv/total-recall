using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-iii part 2 (held deletions), D3, D5 and D6: <c>Restore</c> of a held remote deletion keeps the memory and
/// rewrites its row as a new one whose stamps are above the old base's (a base that does not parse, or no sync identity,
/// is <c>failed</c>); <c>Restore</c> of a held local deletion re-inserts the memory from the row's base as a non-sticky
/// entry with an embedding computed before the transaction, the row the apply writes for an insert, and a <c>new-pin</c>
/// item for a sticky base (only with the shown hash; a base changed after the embedding, a knowledge entry with the id or
/// a hash from before is <c>stale-item</c>; an embedder that throws is <c>failed</c>). Only a local restore creates the embedder.
/// </summary>
public sealed class ReviewServiceRestoreTests : IDisposable
{
    private const string Id = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string Id2 = "3f9c2a7e-0000-4000-8000-0000000000b2";
    private const string Id3 = "3f9c2a7e-0000-4000-8000-0000000000c3";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherSha = "89abcdef0123456789abcdef0123456789abcdef";
    private const string PinnedText = "Pinned here, then deleted.";
    private const string NewPinLine = "the record is pinned: a new-pin review was opened; the memory is not pinned here";

    private readonly ApplyRig _rig = new();

    public void Dispose()
    {
        _rig.Dispose();
        MsSqliteConnection.ClearAllPools();
    }

    private ReviewService Service(IEmbedder? embedder = null, int hotMaxContentChars = 0, long? nowMs = null) =>
        ReviewServices.For(_rig.Scan.DbPath, () => nowMs ?? _rig.Now, embedder, hotMaxContentChars);

    private Task<SetupResult> Restore(string id, string? shown = null) => Service().RestoreAsync(id, shown);

    private GitSyncRow Row(string id) => GitSyncRows.Read(_rig.Scan.Connection, id)!;

    private HeldItem? Item(string id) => GitSyncReviews.ReadHeldItem(_rig.Scan.Connection, id);

    private bool IsSticky(string id) => _rig.Scan.Store.IsSticky(ContentType.Memory, id);

    private int Vectors() => _rig.VectorCount(Tier.Hot) + _rig.VectorCount(Tier.Warm) + _rig.VectorCount(Tier.Cold);

    private static string Render(SyncRecordModule.SyncRecord record) => Encoding.UTF8.GetString(RecordWriter.Write(record).Bytes!);

    private void SetBase(string id, string baseJson) =>
        _rig.Scan.Exec($"UPDATE git_sync_entry SET base_json = '{baseJson.Replace("'", "''")}' WHERE entry_id = '{id}'");

    private static void AssertRefused(SetupResult result, SyncSetup.Refusal refusal)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(SyncSetup.refusalName(refusal), SyncSetup.refusalName(result.Refusal!));
        Assert.Empty(result.Lines);
    }

    /// <summary>The <c>new-pin</c> rows of <paramref name="id"/>, one line each: state, record hash, direction, machine, payload, arrived_at.</summary>
    private List<string> NewPinRows(string id)
    {
        using var cmd = _rig.Scan.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT state, record_hash, direction, machine, payload_json, arrived_at FROM git_sync_review WHERE entry_id = $id AND kind = 'new-pin'";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add($"{r.GetString(0)}|{r.GetString(1)}|{(r.IsDBNull(2) ? "NULL" : r.GetString(2))}|{(r.IsDBNull(3) ? "NULL" : r.GetString(3))}|{r.GetString(4)}|{r.GetInt64(5)}");
        return rows;
    }

    /// <summary>An open remote hold of <see cref="Id"/> with the cause <paramref name="cause"/>, made the way the pull makes it.</summary>
    private void RemoteHold(string cause)
    {
        switch (cause)
        {
            case "guard":
                _rig.Apply(ApplyRig.Rec(Id, "Kept here."));
                _rig.Applier.ApplyOne(Id, null, proven: true, Item(Id), null, new GitSyncApplier.DeleteGuardVerdict(RemoteTripped: true, LocalTripped: false));
                break;
            case "unproven":
                _rig.RemoteHold(Id);
                break;
            case "sticky": // a pinned entry under a proven pull
                _rig.Apply(ApplyRig.Rec(Id, "Kept here.", tier: Tier.Hot));
                _rig.Scan.Pin(Id);
                _rig.RunScan();
                _rig.Apply(Id, null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cause));
        }
        Assert.Equal(new HeldItem(Id, "remote", null, cause, _rig.Now), Item(Id));
    }

    /// <summary>
    /// The memory pulled into hot and pinned here, its pin published (the base becomes the sticky record and the pending
    /// stamps clear, as the push leaves them), then deleted here and held by an unproven pull: an open local hold of
    /// <see cref="Id"/> with the hash <see cref="Sha"/> and a sticky base whose row says the entry was pinned.
    /// </summary>
    private SyncRecordModule.SyncRecord PinnedLocalHold()
    {
        _rig.Apply(ApplyRig.Rec(Id, PinnedText, tier: Tier.Hot, tags: ["b", "a"]));
        _rig.Scan.Pin(Id);
        _rig.RunScan();
        var pinned = ApplyRig.Rec(Id, PinnedText, sticky: true, tags: ["b", "a"], tierMinute: 20, stickyMinute: 20, writtenBy: "machine-a");
        SetBase(Id, Render(pinned));
        _rig.Scan.Exec($"UPDATE git_sync_entry SET content_stamp = NULL, sticky_stamp = NULL, tier_stamp = NULL WHERE entry_id = '{Id}'");
        _rig.Scan.Store.Delete(Tier.Hot, ContentType.Memory, Id);
        _rig.RunScan();
        _rig.Apply(pinned, proven: false, blobSha: Sha);
        Assert.Equal(new HeldItem(Id, "local", Sha, "unproven", _rig.Now), Item(Id));
        Assert.True(Row(Id).LocalSticky);
        return pinned;
    }

    // ---- restore, remote hold (D3) -------------------------------------------------------------------------------

    [Theory]
    [InlineData("guard")]
    [InlineData("unproven")]
    [InlineData("sticky")]
    public async Task RestoringARemoteHold_KeepsTheEntry_AndRewritesTheRowAsNew(string cause)
    {
        RemoteHold(cause);
        // a pushing_stamps text and a repush marker the rewrite must clear
        _rig.Scan.Exec(
            "UPDATE git_sync_entry SET pushing_stamps = '{\"content\":\"" + SyncStamp.StampModule.format(ApplyRig.St(10))
            + $"\"}}', repush_groups = 'content' WHERE entry_id = '{Id}'");
        var entry = _rig.Entry(Id);
        var sticky = IsSticky(Id);
        Assert.Equal(cause == "sticky", sticky);
        var before = Row(Id);

        var result = await Restore(Id);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"restored {Id}: the next sync publishes it again"], result.Lines);
        Assert.Equal(entry, _rig.Entry(Id));
        Assert.Equal(sticky, IsSticky(Id));
        var row = Row(Id);
        Assert.Equal(SyncMerge.RowState.Active, row.State);
        Assert.Null(row.BaseJson);
        Assert.NotNull(row.ContentStamp);
        Assert.NotNull(row.StickyStamp);
        Assert.NotNull(row.TierStamp);
        Assert.Null(row.PushingStamps);
        Assert.Null(row.RepushGroups);
        Assert.Equal(
            before with { BaseJson = null, ContentStamp = row.ContentStamp, StickyStamp = row.StickyStamp, TierStamp = row.TierStamp, PushingStamps = null, RepushGroups = null },
            row);
        Assert.Null(Item(Id));
        Assert.Equal(0, _rig.ReviewRows());
    }

    [Fact]
    public async Task TheNewStamps_AreAboveTheOldBase_EvenWithAClockSetBehind()
    {
        _rig.Apply(ApplyRig.Rec(Id, "Kept here.", minute: 10, stickyMinute: 30, tierMinute: 40));
        _rig.Apply(Id, null, proven: false);
        Assert.Equal("remote", Item(Id)?.Direction);
        var oldBase = ApplyPlanning.ParseBase(Id, Row(Id).BaseJson!).Parsed!.Record;

        var result = await Service(nowMs: 1_700_000_000_000L).RestoreAsync(Id, null);

        Assert.Equal(0, result.ExitCode);
        var row = Row(Id);
        Assert.True(SyncStamp.StampModule.compare(row.ContentStamp!, oldBase.ContentStamp) > 0);
        Assert.True(SyncStamp.StampModule.compare(row.StickyStamp!, oldBase.Placement.StickyStamp) > 0);
        Assert.True(SyncStamp.StampModule.compare(row.TierStamp!, oldBase.Placement.TierStamp) > 0);
    }

    [Fact]
    public async Task ARemoteRestoreWithAnUnparsableBase_IsFailed_AndChangesNothing()
    {
        _rig.RemoteHold(Id);
        SetBase(Id, "{not a record");
        var before = _rig.Full();

        var result = await Restore(Id);

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Contains("the row's base cannot be read", result.RefusalText);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task ARemoteRestoreWithNoMachineId_IsFailed_AndChangesNothing()
    {
        _rig.RemoteHold(Id);
        _rig.Scan.Exec($"DELETE FROM _meta WHERE key = '{SyncIdentity.MetaKeys.Machine}'");
        var before = _rig.Full();

        var result = await Restore(Id);

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Contains("cannot issue a stamp", result.RefusalText);
        Assert.Equal(before, _rig.Full());
    }

    // ---- restore, local hold (D5, D6) ----------------------------------------------------------------------------

    [Fact]
    public async Task RestoringALocalHold_ReinsertsTheMemoryNonSticky_FromTheBase()
    {
        PinnedLocalHold();
        var service = Service();

        var result = await service.RestoreAsync(Id, Sha);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"restored {Id}: the memory is back on this machine", NewPinLine], result.Lines);
        var found = _rig.Entry(Id)!;
        Assert.Equal(Tier.Hot, found.Tier);
        Assert.False(IsSticky(Id));
        Assert.Equal(PinnedText, found.Row.Content);
        Assert.Equal(Id, found.Row.Id);
        Assert.Equal(new[] { "a", "b" }, found.Row.Tags.Order(StringComparer.Ordinal));
        Assert.Equal(1, _rig.VectorCount(Tier.Hot));
        Assert.Equal(1, Vectors());
        Assert.Equal(1, _rig.ImportLogRows(Id));
        var row = Row(Id);
        Assert.Equal(SyncMerge.RowState.Active, row.State);
        Assert.False(row.LocalSticky);
        Assert.Equal(Tier.Hot, row.LocalTier);
        Assert.Null(row.ContentStamp);
        Assert.Null(row.StickyStamp);
        Assert.Null(row.TierStamp);
        Assert.Null(row.PushingStamps);
        Assert.Null(Item(Id));
        Assert.Equal(1, service.EmbedderCreations);
    }

    [Fact]
    public async Task AHotBaseOverTheHotLimit_IsInsertedWarm()
    {
        _rig.LocalHold(Id, Sha, new string('h', 40));
        SetBase(Id, Render(ApplyRig.Rec(Id, new string('h', 40), tier: Tier.Hot)));

        var result = await Service(hotMaxContentChars: 10).RestoreAsync(Id, Sha);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Tier.Warm, _rig.Entry(Id)!.Tier);
        Assert.Equal(Tier.Warm, Row(Id).LocalTier);
    }

    [Fact]
    public async Task AStickyBase_OpensANewPinItem_WithTheShownHashAndTheRecordsMachine()
    {
        PinnedLocalHold();

        Assert.Equal(0, (await Restore(Id, Sha)).ExitCode);

        Assert.Equal([$"open|{SyncPin.shownHash(PinnedText, null, null)}|NULL|machine-a|{{\"origin\":\"restore\"}}|{_rig.Now}"], NewPinRows(Id));
        Assert.Null(Item(Id));
        Assert.Equal(Tier.Hot, _rig.Entry(Id)!.Tier);
        Assert.False(IsSticky(Id));
    }

    [Fact]
    public async Task AStickyBase_ThenTheSameRecordUnderAnUnprovenPull_IsNotPostponed_AndLeavesTheEntryAndTheNewPinItem()
    {
        var pinned = PinnedLocalHold();
        Assert.Equal(0, (await Restore(Id, Sha)).ExitCode);
        var entry = _rig.Entry(Id);
        var newPin = NewPinRows(Id);
        var row = Row(Id);

        // An unproven pull decides every accepted record, the restored id included.
        var result = _rig.Apply(pinned, proven: false, blobSha: Sha);

        Assert.IsType<EntryResult.NoOp>(result);
        Assert.False(IsSticky(Id));
        Assert.Equal(entry, _rig.Entry(Id));
        Assert.Equal(newPin, NewPinRows(Id));
        Assert.Equal(row, Row(Id));
        Assert.Null(Item(Id));
    }

    [Fact]
    public async Task ANonStickyBase_OpensNoNewPinItem()
    {
        _rig.LocalHold(Id, Sha);

        var result = await Restore(Id, Sha);

        Assert.Equal([$"restored {Id}: the memory is back on this machine"], result.Lines);
        Assert.Empty(NewPinRows(Id));
        Assert.Equal(0, _rig.ReviewRows());
    }

    [Fact]
    public async Task AnApprovedItem_IsRestoredToo()
    {
        _rig.LocalHold(Id, Sha, "Deleted here.", approve: true);

        var result = await Restore(Id, Sha);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Deleted here.", _rig.Entry(Id)!.Row.Content);
        Assert.Null(Item(Id));
    }

    [Fact]
    public async Task ALocalRestoreWithAnUnparsableBase_IsFailed_AndChangesNothing()
    {
        _rig.LocalHold(Id, Sha);
        SetBase(Id, "{not a record");
        var before = _rig.Full();
        var service = Service();

        var result = await service.RestoreAsync(Id, Sha);

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Contains("the row's base cannot be read", result.RefusalText);
        Assert.Equal(before, _rig.Full());
        Assert.Equal(0, service.EmbedderCreations);
    }

    [Fact]
    public async Task ABaseThatChangedAfterTheEmbedding_IsStaleItem()
    {
        _rig.LocalHold(Id, Sha, "Deleted here.");
        var service = Service();
        string? before = null;
        service.BeforeTransaction = _ =>
        {
            SetBase(Id, Render(ApplyRig.Rec(Id, "Another text.")));
            before = _rig.Full();
        };

        var result = await service.RestoreAsync(Id, Sha);

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.Equal(before, _rig.Full());
        Assert.Null(_rig.Entry(Id));
        Assert.Equal("open", Item(Id)!.State);
    }

    [Fact]
    public async Task ALocalRestoreWithAStaleHash_IsStaleItem()
    {
        _rig.LocalHold(Id, Sha);
        var before = _rig.Full();
        var service = Service();

        var result = await service.RestoreAsync(Id, OtherSha);

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.EndsWith($"now: open local unproven {Sha}", result.RefusalText);
        Assert.Equal(before, _rig.Full());
        Assert.Equal(0, service.EmbedderCreations); // a hash that is not the item's loads no model
    }

    [Fact]
    public async Task ALocalRestoreWithoutAShownHash_IsShownHashRequired()
    {
        _rig.LocalHold(Id, Sha);
        var before = _rig.Full();

        AssertRefused(await Restore(Id), SyncSetup.Refusal.ShownHashRequired);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AnEmbedderThatThrows_IsFailed_AndChangesNothing()
    {
        _rig.LocalHold(Id, Sha);
        var before = _rig.Full();
        var embedder = new CountingEmbedder { Fails = _ => true };

        var result = await Service(embedder).RestoreAsync(Id, Sha);

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Contains("the embedder failed", result.RefusalText);
        Assert.Equal(1, embedder.Calls);
        Assert.Equal("open", Item(Id)!.State);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AnIdThatIsNowAKnowledgeEntry_IsStaleItem_AndNoMemoryIsInserted()
    {
        _rig.LocalHold(Id, Sha);
        _rig.Scan.AddKnowledge(Tier.Warm, Id);
        var before = _rig.Full();

        var result = await Restore(Id, Sha);

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.Contains("knowledge entry", result.RefusalText);
        Assert.Null(_rig.Entry(Id));
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task OnlyALocalRestore_CreatesTheEmbedder()
    {
        _rig.LocalHold(Id, Sha);
        _rig.RemoteHold(Id2);
        _rig.RemoteHold(Id3);
        var service = Service();

        Assert.Equal(0, (await service.RestoreAsync(Id2, null)).ExitCode);
        Assert.Equal(0, (await service.AcceptAsync(Id3, null)).ExitCode);
        Assert.Equal(0, (await service.AcceptAsync(Id, Sha)).ExitCode);
        Assert.Equal(0, service.EmbedderCreations);

        Assert.Equal(0, (await service.RestoreAsync(Id, Sha)).ExitCode);
        Assert.Equal(1, service.EmbedderCreations);
    }

    // ---- over a real repository --------------------------------------------------------------------------------

    /// <summary>
    /// A PushRig whose memory was pulled into hot, pinned here and pushed (the base is the sticky record), then deleted
    /// here and held by an unproven pull; returns the held item's hash.
    /// </summary>
    private static async Task<string> PinnedLocalHold(PushRig rig)
    {
        rig.Seed(ApplyRig.Rec(Id, PinnedText, tier: Tier.Hot));
        await rig.PullAsync();
        rig.Pin(Id);
        rig.Now += 1000;
        var (apply, scan) = await rig.PullAsync();
        Assert.IsType<PushOutcome.Pushed>(await rig.Push(apply, scan));
        Assert.True(ApplyPlanning.ParseBase(Id, GitSyncRows.Read(rig.Pull.Connection, Id)!.BaseJson!).Parsed!.Record.Placement.Sticky);

        rig.Store.Delete(Tier.Hot, ContentType.Memory, Id);
        rig.Pull.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        rig.Now += 1000;
        var (held, _) = await rig.PullAsync();
        Assert.False(held.Proven);
        var item = GitSyncReviews.ReadHeldItem(rig.Pull.Connection, Id)!;
        Assert.Equal("local", item.Direction);
        return item.RecordHash!;
    }

    [GitFact]
    public async Task AStickyBase_OpensANewPinItem_AndAFollowingUnprovenPull_NeitherPostponesTheIdNorChangesTheItem()
    {
        using var rig = new PushRig();
        var hash = await PinnedLocalHold(rig);
        var service = ReviewServices.For(rig.Pull.DbPath, () => rig.Now);

        Assert.Equal(0, (await service.RestoreAsync(Id, hash)).ExitCode);
        string Review()
        {
            using var cmd = rig.Pull.Connection.CreateCommand();
            cmd.CommandText = "SELECT kind, state, record_hash, payload_json, arrived_at FROM git_sync_review ORDER BY entry_id, kind";
            using var r = cmd.ExecuteReader();
            var sb = new StringBuilder();
            while (r.Read())
                sb.Append($"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}|{r.GetString(3)}|{r.GetInt64(4)}\n");
            return sb.ToString();
        }
        var items = Review();
        Assert.Equal($"new-pin|open|{SyncPin.shownHash(PinnedText, null, null)}|{{\"origin\":\"restore\"}}|{rig.Now}\n", items);

        // Unproven, so the pull decides every accepted record, the restored id included.
        rig.Pull.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        rig.Now += 1000;
        var (summary, _) = await rig.PullAsync();

        Assert.False(summary.Proven);
        Assert.DoesNotContain(summary.Postponed, p => p.Id == Id);
        Assert.True(summary.Complete);
        Assert.Equal(items, Review());
        Assert.NotNull(rig.Store.Get(Tier.Hot, ContentType.Memory, Id));
        Assert.False(rig.Store.IsSticky(ContentType.Memory, Id));
    }

    [GitFact]
    public async Task AnOpenLocalItemWhoseEntryWasRecreated_IsClosedByOneProvenPull_AndBothAnswersThenRefuseNoHeldItem()
    {
        using var rig = new PushRig();
        rig.Seed(ApplyRig.Rec(Id));
        await rig.PullAsync();
        rig.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        rig.Pull.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        rig.Now += 1000;
        Assert.False((await rig.PullAsync()).Apply.Proven);
        var hash = GitSyncReviews.ReadHeldItem(rig.Pull.Connection, Id)!.RecordHash!;
        rig.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(
            Content: "Use bun, not npm.", Id: Id, EntryType: EntryType.Correction, CreatedAt: 1_700_000_000_000L));

        rig.Now += 1000;
        var (summary, _) = await rig.PullAsync();

        Assert.True(summary.Proven);
        Assert.Equal([Id], summary.ClosedHeld);
        Assert.Null(GitSyncReviews.ReadHeldItem(rig.Pull.Connection, Id));
        var service = ReviewServices.For(rig.Pull.DbPath, () => rig.Now);
        AssertRefused(await service.AcceptAsync(Id, hash), SyncSetup.Refusal.NoHeldItem);
        AssertRefused(await service.RestoreAsync(Id, hash), SyncSetup.Refusal.NoHeldItem);
        Assert.NotNull(rig.Store.Get(Tier.Warm, ContentType.Memory, Id));
    }
}
