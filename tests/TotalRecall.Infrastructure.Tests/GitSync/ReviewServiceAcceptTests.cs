using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-iii part 2 (held deletions), D2, D4, D6 and D9: <c>Accept</c> of a held remote deletion deletes the
/// memory here and forgets the row as a proven pull would (refused <c>edited-locally</c> when the memory was edited after
/// the hold), and <c>Accept</c> of a held local deletion approves the item and changes nothing else (only with the shown
/// hash; a repeat answers <c>already accepted</c>). Answering one item leaves the others held.
/// </summary>
public sealed class ReviewServiceAcceptTests : IDisposable
{
    private const string Id = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string Id2 = "3f9c2a7e-0000-4000-8000-0000000000b2";
    private const string Id3 = "3f9c2a7e-0000-4000-8000-0000000000c3";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherSha = "89abcdef0123456789abcdef0123456789abcdef";
    private const string DroppedLine = "note: pending-changes-dropped (a tag, tier, project, scope or type change went with the memory)";

    private readonly ApplyRig _rig = new();

    public void Dispose()
    {
        _rig.Dispose();
        MsSqliteConnection.ClearAllPools();
    }

    private ReviewService Service() => ReviewServices.For(_rig.Scan.DbPath, () => _rig.Now);

    private Task<SetupResult> Accept(string id, string? shown = null) => Service().AcceptAsync(id, shown);

    private GitSyncRow Row(string id) => GitSyncRows.Read(_rig.Scan.Connection, id)!;

    private HeldItem? Item(string id) => GitSyncReviews.ReadHeldItem(_rig.Scan.Connection, id);

    private int Vectors() => _rig.VectorCount(Tier.Hot) + _rig.VectorCount(Tier.Warm) + _rig.VectorCount(Tier.Cold);

    private void Edit(string id, UpdateEntryOpts opts) => _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, id, opts);

    private static void AssertRefused(SetupResult result, SyncSetup.Refusal refusal)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(SyncSetup.refusalName(refusal), SyncSetup.refusalName(result.Refusal!));
        Assert.Empty(result.Lines);
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

    // ---- accept, remote hold (D2) ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("guard")]
    [InlineData("unproven")]
    [InlineData("sticky")]
    public async Task AcceptingARemoteHold_DeletesTheEntry(string cause)
    {
        RemoteHold(cause);
        // a pushing_stamps text and a repush marker the deletion must clear, as a proven pull's does
        _rig.Scan.Exec(
            "UPDATE git_sync_entry SET pushing_stamps = '{\"content\":\"" + SyncStamp.StampModule.format(ApplyRig.St(10))
            + $"\"}}', repush_groups = 'content' WHERE entry_id = '{Id}'");
        var baseJson = Row(Id).BaseJson;
        Assert.NotNull(baseJson);
        var vectors = Vectors();

        var result = await Accept(Id);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"accepted {Id}: the memory was deleted here"], result.Lines);
        Assert.Null(_rig.Entry(Id));
        Assert.Equal(vectors - 1, Vectors());
        var row = Row(Id);
        Assert.Equal(SyncMerge.RowState.Forgotten, row.State);
        Assert.Equal(baseJson, row.BaseJson);
        Assert.Null(row.ContentStamp);
        Assert.Null(row.StickyStamp);
        Assert.Null(row.TierStamp);
        Assert.Null(row.PushingStamps);
        Assert.Null(row.RepushGroups);
        Assert.Null(Item(Id));
        Assert.IsType<EntryResult.NoOp>(_rig.Apply(Id, null)); // the next pull decides nothing for the id
    }

    [Theory]
    [InlineData(false)] // before the scan: the content hash differs from the row's scanned hash
    [InlineData(true)]  // after the scan: a pending content stamp and a text that differs from the base's
    public async Task ATextEditedAfterTheHold_IsEditedLocally(bool scanned)
    {
        _rig.RemoteHold(Id);
        Edit(Id, new UpdateEntryOpts { Content = "A local edit." });
        if (scanned)
        {
            _rig.RunScan();
            Assert.NotNull(Row(Id).ContentStamp);
        }
        var before = _rig.Full();

        var result = await Accept(Id);

        AssertRefused(result, SyncSetup.Refusal.EditedLocally);
        Assert.Equal(before, _rig.Full());
        Assert.Equal("open", Item(Id)!.State);
        Assert.Equal("A local edit.", _rig.Entry(Id)!.Row.Content);
    }

    [Theory]
    [InlineData("delete-vec")]
    [InlineData("delete-row")]
    [InlineData("row")]
    public async Task AFaultInsideARemoteAccept_RollsEverythingBack_AndIsFailed(string step)
    {
        RemoteHold("unproven");
        var before = _rig.Full();
        var service = Service();
        var seen = new List<string>();
        service.BeforeStep = current =>
        {
            seen.Add(current);
            if (current == step)
                throw new InvalidOperationException("fault at " + step);
        };

        var result = await service.AcceptAsync(Id, null);

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Contains("fault at " + step, result.RefusalText);
        Assert.Contains(step, seen);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AnIdInTwoTiers_IsUnreadableEntry()
    {
        _rig.RemoteHold(Id);
        _rig.Scan.AddMemory(Tier.Cold, Id, "A second copy.");
        var before = _rig.Full();

        AssertRefused(await Accept(Id), SyncSetup.Refusal.UnreadableEntry);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AnEntryAlreadyGone_IsStaleItem()
    {
        _rig.RemoteHold(Id);
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        var before = _rig.Full();

        AssertRefused(await Accept(Id), SyncSetup.Refusal.StaleItem);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task ARowNoLongerActive_IsFailed()
    {
        _rig.RemoteHold(Id);
        _rig.Scan.Exec($"UPDATE git_sync_entry SET state = 'forgotten' WHERE entry_id = '{Id}'");
        var before = _rig.Full();

        AssertRefused(await Accept(Id), SyncSetup.Refusal.Failed);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AnUnparsableBase_IsFailed()
    {
        _rig.RemoteHold(Id);
        _rig.Scan.Exec($"UPDATE git_sync_entry SET base_json = '{{not a record' WHERE entry_id = '{Id}'");
        var before = _rig.Full();

        var result = await Accept(Id);

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Contains("the row's base cannot be read", result.RefusalText);
        Assert.Equal(before, _rig.Full());
        Assert.NotNull(_rig.Entry(Id));
    }

    [Fact]
    public async Task AProjectEditWithAPendingContentStamp_AndTheTextUnchanged_IsAccepted()
    {
        _rig.RemoteHold(Id);
        Edit(Id, new UpdateEntryOpts { Project = "o/r" });
        _rig.RunScan();
        Assert.NotNull(Row(Id).ContentStamp);

        var result = await Accept(Id);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"accepted {Id}: the memory was deleted here", DroppedLine], result.Lines);
        Assert.Null(_rig.Entry(Id));
        Assert.Null(Row(Id).ContentStamp);
    }

    [Fact]
    public async Task ABaseTextThatDiffers_WithNoStamp_AndAnUnchangedHash_IsAccepted()
    {
        _rig.RemoteHold(Id);
        // the state a pin-edit merge leaves: the base took the record's text, the entry kept its own
        var baseJson = Encoding.UTF8.GetString(RecordWriter.Write(ApplyRig.Rec(Id, "The record's text.")).Bytes!);
        _rig.Scan.Exec($"UPDATE git_sync_entry SET base_json = '{baseJson.Replace("'", "''")}' WHERE entry_id = '{Id}'");
        Assert.Null(Row(Id).ContentStamp);

        var result = await Accept(Id);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(_rig.Entry(Id));
        Assert.Equal(baseJson, Row(Id).BaseJson);
    }

    [Fact]
    public async Task ATagEdit_IsAccepted_AndReportedAsDropped()
    {
        _rig.RemoteHold(Id);
        Edit(Id, new UpdateEntryOpts { Tags = ["local"] });
        _rig.RunScan();

        var result = await Accept(Id);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("pending-changes-dropped", StringComparison.Ordinal));
        Assert.Null(_rig.Entry(Id));
    }

    [Fact]
    public async Task AHashPassedForARemoteHold_IsIgnored()
    {
        _rig.RemoteHold(Id);

        var result = await Accept(Id, OtherSha);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(_rig.Entry(Id));
        Assert.Null(Item(Id));
    }

    [Fact]
    public async Task ARemoteHold_NeverBecomesUnseen()
    {
        _rig.RemoteHold(Id);
        Edit(Id, new UpdateEntryOpts { Content = "A local edit." });

        AssertRefused(await Accept(Id), SyncSetup.Refusal.EditedLocally);
        Assert.Equal("open", Item(Id)!.State);
    }

    // ---- accept, local hold (D4, D6) -----------------------------------------------------------------------------

    [Fact]
    public async Task AcceptingALocalHold_ApprovesIt_AndNothingElseChanges()
    {
        _rig.LocalHold(Id, Sha);
        var before = _rig.Full();
        Assert.Contains("|held-delete|open|", before);

        var result = await Accept(Id, Sha);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"accepted {Id}: the next sync removes its record from the repository"], result.Lines);
        Assert.Equal(before.Replace("|held-delete|open|", "|held-delete|approved|"), _rig.Full());
        Assert.Equal(new HeldItem(Id, "local", Sha, "unproven", _rig.Now, "approved"), Item(Id));
    }

    [Fact]
    public async Task ALocalHoldWithoutAShownHash_IsShownHashRequired()
    {
        _rig.LocalHold(Id, Sha);
        var before = _rig.Full();

        AssertRefused(await Accept(Id), SyncSetup.Refusal.ShownHashRequired);
        AssertRefused(await Accept(Id, ""), SyncSetup.Refusal.ShownHashRequired);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AcceptingAnApprovedItemAgain_IsAlreadyAccepted()
    {
        _rig.LocalHold(Id, Sha);
        Assert.Equal(0, (await Accept(Id, Sha)).ExitCode);
        var before = _rig.Full();

        var result = await Accept(Id, Sha);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"already accepted {Id}"], result.Lines);
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AShownHashFromBeforeARefresh_IsStaleItem_AndTheItemStaysOpen()
    {
        _rig.LocalHold(Id, Sha, "Deleted here.");
        _rig.Apply(ApplyRig.Rec(Id, "Deleted here."), proven: false, blobSha: OtherSha); // the pull refreshes the hash (D10)
        Assert.Equal(OtherSha, Item(Id)!.RecordHash);
        var before = _rig.Full();

        var result = await Accept(Id, Sha);

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.EndsWith($"now: open local unproven {OtherSha}", result.RefusalText);
        Assert.Equal(before, _rig.Full());
        Assert.Equal("open", Item(Id)!.State);
    }

    // ---- some answered, the rest held (D9) -----------------------------------------------------------------------

    [Fact]
    public async Task AnsweringOneOfThreeItems_LeavesTheOtherTwoOpen_AndProtectedOnTheNextPull()
    {
        foreach (var id in new[] { Id, Id2, Id3 })
            _rig.RemoteHold(id);

        Assert.Equal(0, (await Accept(Id2)).ExitCode);

        Assert.Null(_rig.Entry(Id2));
        foreach (var id in new[] { Id, Id3 })
        {
            Assert.IsType<EntryResult.NoOp>(_rig.Apply(id, null)); // a proven pull: W6.3 keeps the item and the entry
            Assert.Equal("open", Item(id)!.State);
            Assert.NotNull(_rig.Entry(id));
        }
    }
}
