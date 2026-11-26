using System.Runtime.InteropServices;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Conflict review D6 and D7: <c>keep</c>, <c>swap</c> and <c>merge</c> of a <c>conflict</c> item in the review service, with
/// their checks in the spec's order (<c>no-conflict-item</c>, <c>shown-hash-required</c>, the entry read, the row for
/// <c>swap</c>/<c>merge</c>, the live hash, the loser index, <c>unshowable-text</c>, <c>bad-merge-text</c>,
/// <c>over-pin-cap</c>), a stale hash that commits only the item's update, the write sets of each answer, and the list of
/// the open and unseen conflict items. Every item is made by a pull over an <see cref="ApplyRig"/>.
/// </summary>
public sealed class ReviewServiceConflictTests : IDisposable
{
    private const string Id = "3f9c2a7e-0000-4000-8000-000000000001";
    private const string Id2 = "3f9c2a7e-0000-4000-8000-000000000002";
    private const string ThisMachine = ScanRig.MachineId;
    private const string Other = ApplyRig.OtherMachine;
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Warning =
        "this replaces that change in the repository and on your other machines where the memory is not pinned; "
        + "where it is pinned it is held there as an edit";

    // A local edit later than any record stamp (2027) wins (FC4); one earlier (2026-05) loses (FC5).
    private const long Later = 1_800_000_000_000L;
    private const long Earlier = 1_780_000_000_000L;

    private readonly ApplyRig _rig = new();

    public void Dispose()
    {
        _rig.Dispose();
        MsSqliteConnection.ClearAllPools();
    }

    private ReviewService Service(ApplyRig? rig = null, IEmbedder? embedder = null, int hotMax = 0, int pinnedMax = 500)
    {
        var r = rig ?? _rig;
        return ReviewServices.For(r.Scan.DbPath, () => r.Now, embedder, hotMaxContentChars: hotMax, pinnedMaxContentChars: pinnedMax);
    }

    private static T Is<T>(EntryResult r) where T : EntryResult =>
        r is EntryResult.Failed f && typeof(T) != typeof(EntryResult.Failed)
            ? throw new Xunit.Sdk.XunitException(f.Detail)
            : Assert.IsType<T>(r);

    private static string Shown(string? applied, params string[] losers) =>
        SyncConflict.shownHash(applied is null ? null : FSharpOption<string>.Some(applied), ListModule.OfSeq(losers));

    private static string StampText(SyncStamp.Stamp stamp) => SyncStamp.StampModule.format(stamp);

    private static SyncRecordModule.SyncRecord Rec(string text, int minute, Tier? tier = null, string id = Id) =>
        ApplyRig.Rec(id, text, tier: tier, tags: ["a", "b"], project: "o/r", scope: "s", source: SourceTool.ClaudeCode, minute: minute,
            writtenBy: Other);

    private GitSyncRow Row(ApplyRig? rig = null) => (rig ?? _rig).Scan.Rows()[Id];

    private SyncRecordModule.SyncRecord Base(ApplyRig? rig = null) => ApplyPlanning.ParseBase(Id, Row(rig).BaseJson!).Parsed!.Record;

    private ConflictItem? Item(ApplyRig? rig = null) => GitSyncReviews.ReadConflictItem((rig ?? _rig).Scan.Connection, Id);

    private string Text(ApplyRig? rig = null) => (rig ?? _rig).Entry(Id)!.Row.Content;

    /// <summary>The full dump without the <c>git_sync_review</c> lines.</summary>
    private string NonReview(ApplyRig? rig = null) =>
        string.Join('\n', (rig ?? _rig).Full().Split('\n').Where(line => !line.StartsWith("review ", StringComparison.Ordinal)));

    private static void AssertRefused(SetupResult result, SyncSetup.Refusal refusal)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(SyncSetup.refusalName(refusal), SyncSetup.refusalName(result.Refusal!));
        Assert.Empty(result.Lines);
    }

    private void AssertScanQuiet(ApplyRig? rig = null)
    {
        var scan = (rig ?? _rig).RunScan();
        Assert.Equal(0, scan.New + scan.Content + scan.Tags + scan.Sticky + scan.Tier + scan.Left + scan.Reactivated);
    }

    private static string VectorHex(ApplyRig rig, Tier tier)
    {
        var t = tier.ToString().ToLowerInvariant();
        using var cmd = rig.Scan.Connection.CreateCommand();
        cmd.CommandText = $"SELECT v.embedding FROM {t}_memories_vec v JOIN {t}_memories m ON m.rowid = v.rowid WHERE m.id = $id";
        cmd.Parameters.AddWithValue("$id", Id);
        return Convert.ToHexString((byte[])cmd.ExecuteScalar()!);
    }

    private static string ExpectedVectorHex(string text) =>
        Convert.ToHexString(MemoryMarshal.AsBytes(new CountingEmbedder().Embed(text).AsSpan()).ToArray());

    // ---- making items ----

    private static void Edit(ApplyRig r, string text)
    {
        r.Scan.Store.Update(r.Entry(Id)!.Tier, ContentType.Memory, Id, new UpdateEntryOpts { Content = text });
        r.RunScan();
    }

    /// <summary>A synced memory (base at minute 10) edited here at <paramref name="editAt"/>, then the record's edit at minute 20 pulled.</summary>
    private static SyncRecordModule.SyncRecord Conflict(ApplyRig r, string localText, long editAt, string theirText = "Their edit.", Tier? tier = null)
    {
        Is<EntryResult.Carried>(r.Apply(Rec("Use bun, not npm.", 10, tier)));
        r.Now = editAt;
        Edit(r, localText);
        Assert.NotNull(r.Scan.Rows()[Id].ContentStamp);
        var record = Rec(theirText, 20, tier);
        Is<EntryResult.Carried>(r.Apply(record));
        Assert.NotNull(GitSyncReviews.ReadConflictItem(r.Scan.Connection, Id));
        return record;
    }

    /// <summary>FC4: the local text <c>Local edit.</c> kept with its pending stamp, the record's <c>Their edit.</c> a remote loser.</summary>
    private string Fc4(ApplyRig? rig = null)
    {
        Conflict(rig ?? _rig, "Local edit.", Later);
        return Shown("Local edit.", "Their edit.");
    }

    /// <summary>FC5: the record's <c>Their edit.</c> applied, the local <c>Local edit.</c> a local loser.</summary>
    private string Fc5(ApplyRig? rig = null)
    {
        Conflict(rig ?? _rig, "Local edit.", Earlier);
        return Shown("Their edit.", "Local edit.");
    }

    /// <summary>Two FC5 conflicts: the entry has <c>Third edit.</c>, the losers (oldest first) <c>Local edit.</c>, <c>Second local edit.</c>.</summary>
    private string TwoLosers()
    {
        Fc5();
        _rig.Now = Earlier + 60_000;
        Edit(_rig, "Second local edit.");
        Is<EntryResult.Carried>(_rig.Apply(Rec("Third edit.", 30)));
        Assert.Equal("unseen", Item()!.State);
        return Shown("Third edit.", "Local edit.", "Second local edit.");
    }

    // ---- keep ----

    [Fact]
    public async Task Keep_RemovesTheItem_AndNothingElse()
    {
        var shown = Fc4();
        var pending = Row().ContentStamp;
        Assert.NotNull(pending);
        var before = NonReview();
        var service = Service();

        var result = await service.KeepAsync(Id, shown);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"kept {Id}: the memory keeps its text; the review is closed; nothing is pushed"], result.Lines);
        Assert.Null(Item());
        Assert.Equal(0, _rig.ReviewRows());
        Assert.Equal(before, NonReview());
        Assert.Equal(pending, Row().ContentStamp);
        Assert.Equal(0, service.EmbedderCreations);
    }

    [Fact]
    public async Task Keep_NeedsNoRow_WhileSwapAndMergeDo()
    {
        var shown = Fc5();
        GitSyncRows.Delete(_rig.Scan.Connection, Id);
        var before = _rig.Full();

        var swap = await Service().SwapAsync(Id, shown, null);
        AssertRefused(swap, SyncSetup.Refusal.Failed);
        Assert.Contains("not synced yet; run a sync first", swap.RefusalText);
        var merge = await Service().MergeAsync(Id, shown, "Merged.");
        AssertRefused(merge, SyncSetup.Refusal.Failed);
        Assert.Contains("not synced yet; run a sync first", merge.RefusalText);
        Assert.Equal(before, _rig.Full());

        Assert.Equal(0, (await Service().KeepAsync(Id, shown)).ExitCode);
        Assert.Null(Item());
    }

    // ---- swap and merge ----

    [Fact]
    public async Task Swap_MakesTheLoserTheText_ReEmbedded_AndOnlyTheText()
    {
        var shown = Fc5();
        var embedder = new CountingEmbedder();
        var before = _rig.Entry(Id)!;
        Assert.Equal("Their edit.", before.Row.Content);

        var result = await Service(embedder: embedder).SwapAsync(Id, shown, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"swapped {Id}: the chosen text is now the memory's text here; the next sync pushes it", Warning], result.Lines);
        var after = _rig.Entry(Id)!;
        Assert.Equal("Local edit.", after.Row.Content);
        Assert.Equal(before.Tier, after.Tier);
        Assert.Equal(before.Row.Project, after.Row.Project);
        Assert.Equal(before.Row.Scope, after.Row.Scope);
        Assert.Equal(before.Row.EntryType, after.Row.EntryType);
        Assert.Equal(before.Row.SourceTool, after.Row.SourceTool);
        Assert.Equal(before.Row.Source, after.Row.Source);
        Assert.Equal(before.Row.Tags, after.Row.Tags);
        Assert.Equal(before.Row.CreatedAt, after.Row.CreatedAt);
        Assert.False(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        Assert.Equal(1, embedder.Calls);
        Assert.Equal(1, _rig.VectorCount(before.Tier));
        Assert.Equal(ExpectedVectorHex("Local edit."), VectorHex(_rig, before.Tier));
        Assert.Null(Item());
    }

    /// <summary>
    /// Review Focus 3: with a pending local content stamp newer than the base (FC4), the swap stamp exceeds both, even when
    /// the persisted clock is gone and the wall clock is behind both (only the floor can lift it).
    /// </summary>
    [Fact]
    public async Task ASwap_IssuesAStampAboveTheBaseAndThePendingStamp()
    {
        var shown = Fc4();
        var pending = Row().ContentStamp!;
        var baseStamp = Base().ContentStamp;
        Assert.True(SyncStamp.StampModule.compare(pending, baseStamp) > 0);
        using (var cmd = _rig.Scan.Connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM _meta WHERE key = 'git_sync_hlc'";
            cmd.ExecuteNonQuery();
        }
        _rig.Now = Earlier; // before the base's stamp and the pending one

        var result = await Service().SwapAsync(Id, shown, 1);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Their edit.", Text());
        var stamp = Row().ContentStamp;
        Assert.NotNull(stamp);
        Assert.True(SyncStamp.StampModule.compare(stamp!, pending) > 0);
        Assert.True(SyncStamp.StampModule.compare(stamp!, baseStamp) > 0);
    }

    [Fact]
    public async Task ASwap_LeavesTheComparisonColumnsCurrent_AndTheScanStampsNothing()
    {
        var shown = Fc5();
        var rowBefore = Row();

        Assert.Equal(0, (await Service().SwapAsync(Id, shown, 1)).ExitCode);

        var row = Row();
        var stored = Assert.IsType<LocalRead.Present>(
            LocalEntryReader.Read(t => _rig.Scan.Store.Get(t, ContentType.Memory, Id), () => _rig.Scan.Store.IsSticky(ContentType.Memory, Id)));
        Assert.Equal(ApplyPlanning.WithComparison(rowBefore, stored.View) with { ContentStamp = row.ContentStamp }, row);
        Assert.NotNull(row.ContentStamp);
        AssertScanQuiet();
        Assert.Equal(row, Row());
    }

    [Fact]
    public async Task AfterASwap_ThePullTreatsTheEntryAsAnOrdinaryLocalEdit()
    {
        var record = Conflict(_rig, "Local edit.", Earlier);
        Assert.Equal(0, (await Service().SwapAsync(Id, Shown("Their edit.", "Local edit."), 1)).ExitCode);
        var pending = Row().ContentStamp;
        Assert.NotNull(pending);

        var result = _rig.Apply(record);

        Assert.False(result is EntryResult.Deferred or EntryResult.Failed, result.ToString());
        Assert.Null(Item());
        Assert.Equal("Local edit.", Text());
        Assert.Equal(pending, Row().ContentStamp);   // still pending: the push writes it
    }

    [Fact]
    public async Task Merge_MakesTheGivenTextTheText()
    {
        var shown = Fc5();
        var embedder = new CountingEmbedder();
        var rowBefore = Row();

        var result = await Service(embedder: embedder).MergeAsync(Id, shown, "Their edit, and mine.");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"merged {Id}: the merged text is now the memory's text here; the next sync pushes it", Warning], result.Lines);
        Assert.Equal("Their edit, and mine.", Text());
        Assert.Equal(1, embedder.Calls);
        Assert.Equal(ExpectedVectorHex("Their edit, and mine."), VectorHex(_rig, Tier.Warm));
        Assert.NotNull(Row().ContentStamp);
        Assert.True(SyncStamp.StampModule.compare(Row().ContentStamp!, Base().ContentStamp) > 0);
        Assert.Equal(rowBefore.BaseJson, Row().BaseJson);
        Assert.Null(Item());
        AssertScanQuiet();
    }

    [Theory]
    [InlineData("swap")]
    [InlineData("merge")]
    public async Task ATextEqualToTheEntry_OnlyRemovesTheItem(string verb)
    {
        var shown = Fc5();
        // the entry's text made equal to the loser's here (not yet scanned or pulled); the item still shows both
        _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new UpdateEntryOpts { Content = "Local edit." });
        shown = Shown("Local edit.", "Local edit.");
        var before = NonReview();
        var service = Service();

        var result = verb == "swap"
            ? await service.SwapAsync(Id, shown, 1)
            : await service.MergeAsync(Id, shown, "Local edit.");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [$"{(verb == "swap" ? "swapped" : "merged")} {Id}: the memory already has this text; the review is closed; nothing is pushed"],
            result.Lines);
        Assert.Equal(before, NonReview());
        Assert.Null(Item());
        Assert.Equal(0, service.EmbedderCreations);
    }

    [Fact]
    public async Task ASwapOverTheHotLimit_StoresTheEntryWarm()
    {
        using var rig = new ApplyRig(hotMaxContentChars: 30);
        var longText = new string('l', 40);
        Conflict(rig, longText, Earlier, tier: Tier.Hot);
        Assert.Equal(Tier.Hot, rig.Entry(Id)!.Tier);
        var shown = Shown("Their edit.", longText);

        var result = await Service(rig, hotMax: 30).SwapAsync(Id, shown, null);

        Assert.Equal(0, result.ExitCode);
        var found = rig.Entry(Id)!;
        Assert.Equal(Tier.Warm, found.Tier);
        Assert.Equal(longText, found.Row.Content);
        Assert.Equal(0, rig.VectorCount(Tier.Hot));
        Assert.Equal(1, rig.VectorCount(Tier.Warm));
        Assert.Equal(ExpectedVectorHex(longText), VectorHex(rig, Tier.Warm));
        Assert.Equal(Tier.Warm, Row(rig).LocalTier);
        AssertScanQuiet(rig);
    }

    [Fact]
    public async Task ASwapWithSeveralLosers_TakesTheNamedOne_NewestFirst()
    {
        var shown = TwoLosers();

        var listed = Assert.Single(Service().List().Items);
        Assert.Equal(["Second local edit.", "Local edit."], listed.Losers.Select(l => l.Text));

        Assert.Equal(0, (await Service().SwapAsync(Id, shown, 2)).ExitCode);
        Assert.Equal("Local edit.", Text());
        Assert.Null(Item());
    }

    // ---- the refusals ----

    /// <summary>
    /// Review Focus 5: an answer with a hash that is not the item's live one is <c>stale-item</c> with what the item shows
    /// now, and the only write is the item's update to that hash and <c>unseen</c>.
    /// </summary>
    [Theory]
    [InlineData("keep", "entry")]
    [InlineData("swap", "entry")]
    [InlineData("merge", "entry")]
    [InlineData("keep", "loser")]
    [InlineData("swap", "loser")]
    [InlineData("merge", "loser")]
    public async Task AStaleHash_IsStaleItem_AndOnlyTheItemIsWritten(string verb, string change)
    {
        var shown = Fc5();
        string live;
        if (change == "entry")
        {
            _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new UpdateEntryOpts { Content = "Edited here." });
            live = Shown("Edited here.", "Local edit.");
            Assert.Equal("open", Item()!.State);
        }
        else
        {
            _rig.Now = Earlier + 60_000;
            Edit(_rig, "Second local edit.");
            Is<EntryResult.Carried>(_rig.Apply(Rec("Third edit.", 30)));
            live = Shown("Third edit.", "Local edit.", "Second local edit.");
        }
        var item = Item()!;
        var before = NonReview();
        var service = Service();

        var result = verb switch
        {
            "keep" => await service.KeepAsync(Id, shown),
            "swap" => await service.SwapAsync(Id, shown, 1),
            _ => await service.MergeAsync(Id, shown, "Merged."),
        };

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.EndsWith($"now: unseen conflict {live}", result.RefusalText);
        Assert.Equal(item with { State = "unseen", RecordHash = live }, Item());
        Assert.Equal(before, NonReview());
        Assert.Equal(0, service.EmbedderCreations);
    }

    [Theory]
    [InlineData("no-item")]
    [InlineData("no-hash-null")]
    [InlineData("no-hash-empty")]
    [InlineData("absent")]
    [InlineData("absent-held")]
    [InlineData("no-row")]
    [InlineData("unreadable-base-swap")]
    [InlineData("unreadable-base-merge")]
    [InlineData("several-no-index")]
    [InlineData("index-0")]
    [InlineData("index-3")]
    [InlineData("merge-empty")]
    [InlineData("merge-null")]
    [InlineData("merge-whitespace")]
    [InlineData("merge-oversized")]
    [InlineData("merge-nul")]
    [InlineData("cap-50")]
    [InlineData("cap-0")]
    public async Task EveryOtherRefusal_WritesNothing(string kase)
    {
        string shown;
        SyncSetup.Refusal refusal;
        string? contains = null;
        var pinnedMax = 500;
        Func<ReviewService, Task<SetupResult>> answer;
        switch (kase)
        {
            case "no-item":
                shown = Fc5();
                refusal = SyncSetup.Refusal.NoConflictItem;
                answer = s => s.KeepAsync(Id2, shown);
                break;
            case "no-hash-null" or "no-hash-empty":
                Fc5();
                refusal = SyncSetup.Refusal.ShownHashRequired;
                answer = s => s.SwapAsync(Id, kase == "no-hash-null" ? null : "", 1);
                break;
            case "absent":
                shown = Fc5();
                _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
                refusal = SyncSetup.Refusal.StaleItem;
                contains = "the next sync closes the item";
                answer = s => s.KeepAsync(Id, shown);
                break;
            case "absent-held":
            {
                Conflict(_rig, "Local edit.", Earlier);
                var record = Rec("Their edit.", 20);
                _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
                _rig.RunScan();
                Is<EntryResult.Carried>(_rig.Apply(record, proven: false, blobSha: Sha)); // W8.4
                Assert.Equal("local", _rig.Held(Id)?.Direction);
                shown = Shown(null, "Local edit.");
                refusal = SyncSetup.Refusal.StaleItem;
                contains = "restore or accept the held deletion first";
                answer = s => s.SwapAsync(Id, shown, 1);
                break;
            }
            case "no-row":
                shown = Fc5();
                GitSyncRows.Delete(_rig.Scan.Connection, Id);
                refusal = SyncSetup.Refusal.Failed;
                contains = "not synced yet; run a sync first";
                answer = s => s.SwapAsync(Id, shown, 1);
                break;
            case "unreadable-base-swap" or "unreadable-base-merge":
                shown = Fc5();
                GitSyncRows.Write(_rig.Scan.Connection, Row() with { BaseJson = "not a record" });
                refusal = SyncSetup.Refusal.Failed;
                contains = "the row's base cannot be read";
                answer = kase == "unreadable-base-swap" ? s => s.SwapAsync(Id, shown, 1) : s => s.MergeAsync(Id, shown, "Merged.");
                break;
            case "several-no-index" or "index-0" or "index-3":
                shown = TwoLosers();
                refusal = SyncSetup.Refusal.UnknownLoser;
                int? index = kase switch { "index-0" => 0, "index-3" => 3, _ => null };
                answer = s => s.SwapAsync(Id, shown, index);
                break;
            case "cap-50" or "cap-0":
            {
                shown = PinnedFc4();
                pinnedMax = kase == "cap-50" ? 50 : 0;
                var length = kase == "cap-50" ? 51 : 501;
                refusal = SyncSetup.Refusal.OverPinCap;
                contains = PinnedTierLimits.ContentLimitMessage(kase == "cap-50" ? 50 : 500, length);
                answer = s => s.MergeAsync(Id, shown, new string('m', length));
                break;
            }
            default:
            {
                shown = Fc5();
                refusal = SyncSetup.Refusal.BadMergeText;
                var text = kase switch
                {
                    "merge-empty" => "",
                    "merge-null" => null,
                    "merge-whitespace" => " \t\n ",
                    "merge-oversized" => new string('é', 131072) + "x", // 262145 UTF-8 bytes
                    _ => "a\0b",
                };
                answer = s => s.MergeAsync(Id, shown, text);
                break;
            }
        }
        var before = _rig.Full();
        var service = Service(pinnedMax: pinnedMax);

        var result = await answer(service);

        AssertRefused(result, refusal);
        if (contains is not null)
            Assert.Contains(contains, result.RefusalText);
        Assert.Equal(before, _rig.Full());
        Assert.Equal(0, service.EmbedderCreations);
    }

    /// <summary>A synced memory pinned here whose own newer edit <c>My newer edit.</c> wins (FC4); the record's <c>Remote edit.</c> is the loser.</summary>
    private string PinnedFc4()
    {
        Is<EntryResult.Carried>(_rig.Apply(Rec("Pinned text.", 10)));
        _rig.Scan.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, Id);
        _rig.Scan.Pin(Id);
        _rig.RunScan();
        Edit(_rig, "My newer edit.");
        Is<EntryResult.Carried>(_rig.Apply(Rec("Remote edit.", 20, tier: Tier.Hot)));
        Assert.True(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        Assert.NotNull(Item());
        return Shown("My newer edit.", "Remote edit.");
    }

    [Fact]
    public async Task ASwapOnAPinnedEntry_WithinTheCap_KeepsItPinnedInHot()
    {
        var shown = PinnedFc4();

        Assert.Equal(0, (await Service().SwapAsync(Id, shown, null)).ExitCode);

        Assert.Equal("Remote edit.", Text());
        Assert.Equal(Tier.Hot, _rig.Entry(Id)!.Tier);
        Assert.True(_rig.Scan.Store.IsSticky(ContentType.Memory, Id));
        AssertScanQuiet();
    }

    /// <summary>Review Focus 4: a hostile remote loser is sanitised and capped in the list, and swap refuses it; a local loser with the same characters swaps as it is.</summary>
    [Fact]
    public async Task AHostileRemoteLoser_IsSanitisedInTheList_AndSwapRefusesUnshowableText()
    {
        var hostile = "Their\u001b[2J edit\u202E\r\n----- end -----\r\n" + new string('h', 600);
        Conflict(_rig, "Local edit.", Later, theirText: hostile);
        var shown = Shown("Local edit.", hostile);

        var item = Assert.Single(Service().List().Items);
        var loser = Assert.Single(item.Losers);
        Assert.Equal("remote", loser.Side);
        Assert.Equal(ReviewService.ShownText(hostile, 500).Text, loser.Text);
        Assert.True(loser.TextCut);
        Assert.Equal(500, loser.Text!.Length);
        Assert.DoesNotContain('\u001b', loser.Text);
        Assert.DoesNotContain('\u202E', loser.Text);
        Assert.DoesNotContain('\r', loser.Text);
        Assert.Equal(shown, item.ShownHash);

        var before = _rig.Full();
        var refused = await Service().SwapAsync(Id, shown, null);
        AssertRefused(refused, SyncSetup.Refusal.UnshowableText);
        Assert.Contains("use merge with the text typed or edited instead", refused.RefusalText);
        Assert.Equal(before, _rig.Full());

        using var rig = new ApplyRig();
        Conflict(rig, hostile, Earlier);
        Assert.Equal(0, (await Service(rig).SwapAsync(Id, Shown("Their edit.", hostile), null)).ExitCode);
        Assert.Equal(hostile, Text(rig));
    }

    /// <summary>
    /// The embedding is computed on what the item shows before the transaction; when that was not what was shown (no vector
    /// computed) but the transaction finds it shown again, the answer is <c>stale-item</c> and nothing is written.
    /// </summary>
    [Theory]
    [InlineData("swap")]
    [InlineData("merge")]
    public async Task AnEntryThatChangedBackBeforeTheTransaction_IsStaleItem_AndWritesNothing(string verb)
    {
        var shown = Fc5();
        _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new UpdateEntryOpts { Content = "Edited here." });
        var service = Service();
        string? before = null;
        service.BeforeTransaction = _ =>
        {
            _rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new UpdateEntryOpts { Content = "Their edit." });
            before = _rig.Full();
        };

        var result = verb == "swap" ? await service.SwapAsync(Id, shown, 1) : await service.MergeAsync(Id, shown, "Merged.");

        AssertRefused(result, SyncSetup.Refusal.StaleItem);
        Assert.Contains("changed while the text was prepared", result.RefusalText);
        Assert.Equal(before, _rig.Full());
        Assert.Equal(0, service.EmbedderCreations);
    }

    // ---- the frame ----

    [Fact]
    public async Task AHeldSyncLock_IsBusy()
    {
        var shown = Fc5();
        var before = _rig.Full();
        var service = Service();
        var paths = GitSyncPaths.ForDatabase(_rig.Scan.DbPath, false);
        using (var held = await SyncLock.TryAcquireAsync(paths.LockPath, TimeSpan.Zero))
        {
            Assert.NotNull(held);
            AssertRefused(await service.KeepAsync(Id, shown), SyncSetup.Refusal.Busy);
            AssertRefused(await service.SwapAsync(Id, shown, 1), SyncSetup.Refusal.Busy);
            AssertRefused(await service.MergeAsync(Id, shown, "Merged."), SyncSetup.Refusal.Busy);
        }
        Assert.Equal(before, _rig.Full());
    }

    [Theory]
    [InlineData("keep")]
    [InlineData("swap")]
    [InlineData("merge")]
    public async Task AFaultAtTheLastWrite_RollsTheAnswerBack(string verb)
    {
        var shown = Fc5();
        var before = _rig.Full();
        var service = Service();
        var steps = new List<string>();
        service.BeforeStep = step =>
        {
            steps.Add(step);
            if (step == "resolve")
                throw new InvalidOperationException("fault at resolve");
        };

        var result = verb switch
        {
            "keep" => await service.KeepAsync(Id, shown),
            "swap" => await service.SwapAsync(Id, shown, 1),
            _ => await service.MergeAsync(Id, shown, "Merged."),
        };

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Contains("fault at resolve", result.RefusalText);
        Assert.Equal("resolve", steps[^1]);
        if (verb != "keep")
            Assert.Contains("row", steps);
        Assert.Equal(before, _rig.Full());
    }

    // ---- held deletions, the list, other kinds ----

    /// <summary>Review Focus 2: an FC5 item whose entry is deleted here and held survives, and is listed with its loser after restore.</summary>
    [Fact]
    public async Task AConflictItem_SurvivesAHeldLocalDeletion_AndIsListedAfterRestore()
    {
        Conflict(_rig, "Local edit.", Earlier);
        var record = Rec("Their edit.", 20);
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        _rig.RunScan();
        Is<EntryResult.Carried>(_rig.Apply(record, proven: false, blobSha: Sha)); // W8.4
        Assert.Equal("local", _rig.Held(Id)?.Direction);

        var listed = Service().List().Items.Single(i => i.Kind == "conflict");
        Assert.Null(listed.Text);
        Assert.Equal("held deletion", listed.TextSource);
        Assert.Equal(Shown(null, "Local edit."), listed.ShownHash);
        Assert.Equal(["Local edit."], listed.Losers.Select(l => l.Text));

        Assert.Equal(0, (await Service().RestoreAsync(Id, Sha)).ExitCode);

        var after = Assert.Single(Service().List().Items);
        Assert.Equal("conflict", after.Kind);
        Assert.Equal("Their edit.", after.Text);
        Assert.Equal("local entry", after.TextSource);
        Assert.Equal(Shown("Their edit.", "Local edit."), after.ShownHash);
        Assert.Equal(["Local edit."], after.Losers.Select(l => l.Text));
    }

    [Fact]
    public void List_ShowsAConflict_WithTheAppliedTextAndNumberedLosers()
    {
        Fc5();
        var firstStamp = GitSyncReviews.ReadConflictLosers(Item()!.PayloadJson).Single().Stamp;
        _rig.Now = Earlier + 60_000;
        Edit(_rig, "Second local edit.");
        var secondStamp = StampText(Row().ContentStamp!);
        Is<EntryResult.Carried>(_rig.Apply(Rec("Third edit.", 30)));
        var stored = Item()!;

        var listing = Service(pinnedMax: 12).List();

        Assert.Null(listing.Failed);
        var item = Assert.Single(listing.Items);
        Assert.Equal(
            new ReviewItem(
                "conflict", Id, "unseen", null, null, Shown("Third edit.", "Local edit.", "Second local edit."), stored.ArrivedAt,
                ThisMachine, null, null, "Third edit.", false, "local entry", null, false, item.Losers),
            item);
        Assert.Equal(
            [
                new ConflictLoser("local", ThisMachine, secondStamp, "Second local", true),
                new ConflictLoser("local", ThisMachine, firstStamp, "Local edit.", false),
            ],
            item.Losers);
    }

    [Fact]
    public async Task AnIdWithAPinItemAndAConflict_EachVerbAnswersItsOwnKind()
    {
        var shown = Fc5();
        Is<EntryResult.Carried>(_rig.Apply(ApplyRig.Rec(Id2, "Pin this.", sticky: true, writtenBy: "machine-a")));
        var pinShown = SyncPin.shownHash("Pin this.", null, null);
        var before = _rig.Full();
        AssertRefused(await Service().KeepAsync(Id2, pinShown), SyncSetup.Refusal.NoConflictItem);
        AssertRefused(await Service().ApproveAsync(Id, "new-pin", shown), SyncSetup.Refusal.NoPinItem);
        AssertRefused(await Service().DeclineAsync(Id, "new-pin", shown), SyncSetup.Refusal.NoPinItem);
        Assert.Equal(before, _rig.Full());

        // a pin item on the conflict's id too: each kind is answered by its own verbs
        GitSyncReviews.UpsertPinItem(_rig.Scan.Connection, new PinItem(Id, "new-pin", "open", pinShown, "machine-a", 1));
        var listing = Service().List();
        Assert.Equal(
            [(Id, "conflict"), (Id, "new-pin"), (Id2, "new-pin")],
            listing.Items.Select(i => (i.EntryId, i.Kind)));
        Assert.All(listing.Items.Where(i => i.Kind != "conflict"), i => Assert.Same(ReviewItem.NoLosers, i.Losers));

        Assert.Equal(0, (await Service().KeepAsync(Id, shown)).ExitCode);
        Assert.Null(Item());
        Assert.Equal("open", GitSyncReviews.ReadPinItem(_rig.Scan.Connection, Id, "new-pin")!.State);
        AssertRefused(await Service().KeepAsync(Id, shown), SyncSetup.Refusal.NoConflictItem);
    }
}
