using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-iii part 2 (held deletions), D1 and D7: the review service's read-only list (the open and approved
/// held-delete items by id, with the memory's text cleaned and capped) and the checks every answer runs inside its one
/// immediate transaction, in the spec's order, each a refusal that changes nothing; the sync lock, a busy database, a load
/// error and a missing database or sync directory.
/// </summary>
public sealed class ReviewServiceTests : IDisposable
{
    private const string Local = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string Remote = "3f9c2a7e-0000-4000-8000-0000000000b2";
    private const string Other = "3f9c2a7e-0000-4000-8000-0000000000c3";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string OtherSha = "89abcdef0123456789abcdef0123456789abcdef";

    private readonly ApplyRig _rig = new();
    private readonly string _empty = Path.Combine(Path.GetTempPath(), "tr-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _rig.Dispose();
        MsSqliteConnection.ClearAllPools();
        if (Directory.Exists(_empty))
            GitTestEnvironment.ForceDelete(_empty);
    }

    private ReviewService Service(bool syncDirectory = true, IEmbedder? embedder = null) =>
        ReviewServices.For(_rig.Scan.DbPath, () => _rig.Now, embedder, syncDirectory: syncDirectory);

    /// <summary>A database path in a directory of its own that the test has not created.</summary>
    private string EmptyDbPath => Path.Combine(_empty, "data", "total-recall.db");

    private static void AssertRefused(SetupResult result, SyncSetup.Refusal refusal)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(SyncSetup.refusalName(refusal), SyncSetup.refusalName(result.Refusal!));
    }

    // ---- the list (D7) ------------------------------------------------------------------------------------------

    [Fact]
    public void List_ShowsOpenAndApprovedItems_InIdOrder_WithStateDirectionCauseHashAndSource()
    {
        _rig.RemoteHold(Remote, "Kept here.");
        _rig.LocalHold(Local, Sha, "Deleted here.", approve: true);
        GitSyncReviews.UpsertPinItem(_rig.Scan.Connection, new PinItem(Other, "new-pin", "declined", Sha, null, 1)); // another kind is not listed

        var listing = Service().List();

        Assert.Null(listing.Failed);
        Assert.Equal(
            [
                new ReviewItem(Local, "approved", "local", "unproven", Sha, _rig.Now, "Deleted here.", false, "repository record"),
                new ReviewItem(Remote, "open", "remote", "unproven", null, _rig.Now, "Kept here.", false, "local entry"),
            ],
            listing.Items);
    }

    [Fact]
    public void List_WithNoDatabase_IsEmpty_AndCreatesNothing()
    {
        var listing = ReviewServices.For(EmptyDbPath, () => _rig.Now, syncDirectory: false).List();

        Assert.Empty(listing.Items);
        Assert.Null(listing.Failed);
        Assert.False(Directory.Exists(_empty), "something was created");
    }

    [Fact]
    public void List_WithNoReviewTable_IsEmpty()
    {
        SyncDb.CreatePlain(EmptyDbPath, "CREATE TABLE t (x INTEGER)");

        var listing = ReviewServices.For(EmptyDbPath, () => _rig.Now, syncDirectory: false).List();

        Assert.Empty(listing.Items);
        Assert.Null(listing.Failed);
    }

    [Fact]
    public void List_WithOneSidecar_IsFailed()
    {
        SyncDb.CreatePlain(EmptyDbPath, "CREATE TABLE t (x INTEGER)");
        File.WriteAllText(EmptyDbPath + "-wal", "");

        var listing = ReviewServices.For(EmptyDbPath, () => _rig.Now, syncDirectory: false).List();

        Assert.Empty(listing.Items);
        Assert.Equal(ReadOnlyDatabase.PendingWalText, listing.Failed);
    }

    [Fact]
    public void List_AnUnreadableText_IsNull_AndTheItemIsListed()
    {
        _rig.RemoteHold(Remote);
        _rig.Scan.AddMemory(Tier.Cold, Remote, "A second copy."); // the id is in two tiers
        _rig.LocalHold(Local, Sha);
        _rig.Scan.Exec($"UPDATE git_sync_entry SET base_json = '{{not a record' WHERE entry_id = '{Local}'");

        var listing = Service().List();

        Assert.Null(listing.Failed);
        Assert.Equal([Local, Remote], listing.Items.Select(i => i.EntryId));
        Assert.All(listing.Items, i => Assert.Null(i.Text));
        Assert.All(listing.Items, i => Assert.False(i.TextCut));
        Assert.Equal(["repository record", "local entry"], listing.Items.Select(i => i.TextSource));
    }

    [Fact]
    public void List_CutsTheTextAtThePinnedCap()
    {
        _rig.RemoteHold(Remote, new string('y', 30));

        var item = Assert.Single(ReviewServices.For(_rig.Scan.DbPath, () => _rig.Now, pinnedMaxContentChars: 12).List().Items);

        Assert.Equal(new string('y', 12), item.Text);
        Assert.True(item.TextCut);
    }

    [Fact]
    public void ShownText_KeepsNewlineAndTab_AndRemovesOtherControlCharacters()
    {
        var (text, cut) = ReviewService.ShownText("a\u001b[31mb\r\nc\td\u0007", 500);

        Assert.Equal("a[31mb\nc\td", text);
        Assert.False(cut);
    }

    [Fact]
    public void ShownText_RemovesTheLineAndParagraphSeparators_AndTheBidiControls()
    {
        var (text, cut) = ReviewService.ShownText(
            "a\u2028b\u2029c\u202Ad\u202Be\u202Cf\u202Dg\u202Eh\u2066i\u2067j\u2068k\u2069l\n\u200Bm", 500);

        Assert.Equal("abcdefghijkl\n\u200Bm", text); // a zero-width space is neither: it is kept
        Assert.False(cut);
    }

    [Fact]
    public void ShownText_CutsAtTheCap_WithoutSplittingASurrogatePair()
    {
        var (text, cut) = ReviewService.ShownText(new string('x', 499) + "😀", 500);

        Assert.Equal(499, text.Length);
        Assert.True(cut);
        Assert.False(char.IsSurrogate(text[^1]));
    }

    [Fact]
    public void ShownText_WithNoCap_UsesThePinnedDefault()
    {
        var (text, cut) = ReviewService.ShownText(new string('x', 600), 0);

        Assert.Equal(500, text.Length);
        Assert.True(cut);
    }

    // ---- the checks (D1) ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("no item", "no-held-item")]
    [InlineData("declined item", "no-held-item")]
    [InlineData("row forgotten", "failed")]
    [InlineData("row with no base", "failed")]
    [InlineData("two tiers", "unreadable-entry")]
    [InlineData("remote entry gone", "stale-item")]
    [InlineData("local entry back", "stale-item")]
    [InlineData("no hash", "shown-hash-required")]
    [InlineData("wrong hash", "stale-item")]
    public async Task TheChecks_RunInTheSpecOrder(string situation, string refusal)
    {
        // each local case passes no hash or a wrong one, so a later check would refuse too: the first in the order wins
        var id = Local;
        string? shown = null;
        switch (situation)
        {
            case "no item":
                _rig.Apply(ApplyRig.Rec(Local));
                break;
            case "declined item":
                _rig.LocalHold(Local, Sha);
                _rig.Scan.Exec($"UPDATE git_sync_review SET state = 'declined' WHERE entry_id = '{Local}'");
                break;
            case "row forgotten":
                _rig.LocalHold(Local, Sha);
                _rig.Scan.Exec($"UPDATE git_sync_entry SET state = 'forgotten' WHERE entry_id = '{Local}'");
                break;
            case "row with no base":
                _rig.LocalHold(Local, Sha);
                _rig.Scan.Exec($"UPDATE git_sync_entry SET base_json = NULL WHERE entry_id = '{Local}'");
                break;
            case "two tiers":
                _rig.LocalHold(Local, Sha);
                _rig.Scan.AddMemory(Tier.Warm, Local, "Back.");
                _rig.Scan.AddMemory(Tier.Cold, Local, "Back again.");
                break;
            case "remote entry gone":
                id = Remote;
                _rig.RemoteHold(Remote);
                _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Remote);
                break;
            case "local entry back":
                _rig.LocalHold(Local, Sha);
                _rig.Scan.AddMemory(Tier.Warm, Local, "Back.");
                break;
            case "no hash":
                _rig.LocalHold(Local, Sha);
                break;
            case "wrong hash":
                _rig.LocalHold(Local, Sha);
                shown = OtherSha;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(situation));
        }
        var before = _rig.Full();

        var result = await Service().CheckForTestsAsync(id, ReviewOperation.Accept, shown);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(refusal, SyncSetup.refusalName(result.Refusal!));
        if (refusal == "no-held-item")
            Assert.Equal("no open or approved held deletion for this id; nothing changed", result.RefusalText);
        if (situation is "row forgotten" or "row with no base")
            Assert.Contains("the row no longer matches the item", result.RefusalText);
        if (situation is "remote entry gone" or "local entry back")
            Assert.Contains("the memory changed since the hold; the next sync closes the item", result.RefusalText);
        if (situation == "wrong hash")
            Assert.EndsWith($"now: open local unproven {Sha}", result.RefusalText);
        Assert.Equal(before, _rig.Full());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryCheckPassed_IsChecked_AndRolledBack(bool approved)
    {
        _rig.RemoteHold(Remote);
        _rig.LocalHold(Local, Sha, approve: approved);
        var before = _rig.Full();
        var service = Service();

        Assert.Equal(["checked"], (await service.CheckForTestsAsync(Local, ReviewOperation.Restore, Sha)).Lines);
        Assert.Equal(["checked"], (await service.CheckForTestsAsync(Remote, ReviewOperation.Accept, OtherSha)).Lines); // a remote hold ignores the hash
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AHeldSyncLock_IsBusy_AndChangesNothing()
    {
        _rig.LocalHold(Local, Sha);
        var service = Service();
        var before = _rig.Full();
        var paths = GitSyncPaths.ForDatabase(_rig.Scan.DbPath, false);
        using (var held = await SyncLock.TryAcquireAsync(paths.LockPath, TimeSpan.Zero))
        {
            Assert.NotNull(held);
            AssertRefused(await service.CheckForTestsAsync(Local, ReviewOperation.Accept, Sha), SyncSetup.Refusal.Busy);
        }
        Assert.Equal(before, _rig.Full());
        Assert.Equal(0, (await service.CheckForTestsAsync(Local, ReviewOperation.Accept, Sha)).ExitCode);
    }

    [GitFact]
    public async Task ACyclesLock_IsRespected()
    {
        using var cycle = new CycleRig();
        var service = ReviewServices.For(cycle.Pull.DbPath, () => _rig.Now);
        using var held = await cycle.HoldLock();

        AssertRefused(await service.CheckForTestsAsync(Local, ReviewOperation.Accept, Sha), SyncSetup.Refusal.Busy);
    }

    [Fact]
    public async Task ABusyDatabase_IsBusy()
    {
        _rig.LocalHold(Local, Sha);
        var before = _rig.Full();
        var (other, _) = _rig.Scan.Other();
        using (other)
        {
            SyncDb.Exec(other, "BEGIN IMMEDIATE");
            var service = Service();
            var opened = 0;
            service.AfterOpen = conn =>
            {
                opened++;
                // fail at once instead of waiting out SQLite's busy timeout and the 30 s command timeout
                SyncDb.Exec(conn, "PRAGMA busy_timeout = 0");
                conn.DefaultTimeout = 1;
            };

            var result = await service.CheckForTestsAsync(Local, ReviewOperation.Accept, Sha);

            Assert.Equal(1, opened);
            AssertRefused(result, SyncSetup.Refusal.Busy);
            SyncDb.Exec(other, "ROLLBACK");
        }
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task ALoadError_IsFailed_AndCreatesNothing()
    {
        var service = ReviewServices.For(EmptyDbPath, () => _rig.Now, loadError: "config.toml is malformed", syncDirectory: false);

        var result = await service.CheckForTestsAsync(Local, ReviewOperation.Accept, Sha);

        AssertRefused(result, SyncSetup.Refusal.Failed);
        Assert.Equal("config.toml is malformed", result.RefusalText);
        Assert.Equal("config.toml is malformed", service.List().Failed);
        Assert.False(Directory.Exists(_empty), "something was created");
    }

    [Fact]
    public async Task AMissingDatabase_IsNoHeldItem_AndCreatesNothing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EmptyDbPath)!);

        var result = await ReviewServices.For(EmptyDbPath, () => _rig.Now, syncDirectory: false)
            .CheckForTestsAsync(Local, ReviewOperation.Restore, Sha);

        AssertRefused(result, SyncSetup.Refusal.NoHeldItem);
        Assert.Equal("no open or approved held deletion for this id; nothing changed", result.RefusalText);
        Assert.False(File.Exists(EmptyDbPath), "a database file was created");
        Assert.False(Directory.Exists(Path.Combine(_empty, "data", "sync")), "the sync directory was created");
    }

    [Fact]
    public async Task AMissingSyncDirectory_IsCreated_AndTheItemIsRead()
    {
        _rig.LocalHold(Local, Sha);
        var syncDirectory = ReviewServices.SyncDirectory(_rig.Scan.DbPath);
        Assert.False(Directory.Exists(syncDirectory));
        var before = _rig.Full();

        var result = await Service(syncDirectory: false).CheckForTestsAsync(Local, ReviewOperation.Accept, Sha);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["checked"], result.Lines);
        Assert.True(Directory.Exists(syncDirectory), "the sync directory was not created");
        Assert.Equal(before, _rig.Full());
    }

    [Fact]
    public async Task AnUnknownId_WithTheSyncDirectoryMissing_IsNoHeldItem()
    {
        _rig.Apply(ApplyRig.Rec(Local));
        var syncDirectory = ReviewServices.SyncDirectory(_rig.Scan.DbPath);

        var result = await Service(syncDirectory: false).CheckForTestsAsync(Remote, ReviewOperation.Accept, null);

        AssertRefused(result, SyncSetup.Refusal.NoHeldItem);
        Assert.True(Directory.Exists(syncDirectory), "the sync directory was not created");
    }

    // ---- the embedder -------------------------------------------------------------------------------------------

    [Fact]
    public void TheEmbedder_IsCreatedOnFirstUseOnly_AndDisposedWithTheService()
    {
        var embedder = new DisposableEmbedder();
        var service = Service(embedder: embedder);
        Assert.Equal(0, service.EmbedderCreations);

        Assert.Same(embedder, service.Embedder());
        Assert.Same(embedder, service.Embedder());
        Assert.Equal(1, service.EmbedderCreations);
        Assert.False(embedder.Disposed);

        service.Dispose();
        Assert.True(embedder.Disposed);
    }

    private sealed class DisposableEmbedder : IEmbedder, IDisposable
    {
        public bool Disposed { get; private set; }
        public EmbedderDescriptor Descriptor { get; } = new("test", "fake", "", 384);
        public float[] Embed(string text) => new float[384];
        public void Dispose() => Disposed = true;
    }
}
