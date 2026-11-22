using TotalRecall.Core;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Git sync secret block (#161, spec D4): after the scan builds its plan, a pass checks every eligible active row's entry
/// for a token or a private key and stamps the mark on the row the plan writes (one write per id). Every token and key
/// comes from <see cref="FakeSecrets"/>; detector calls are counted through <see cref="GitSyncScanner.SecretDetector"/>.
/// </summary>
public sealed class GitSyncScannerSecretTests
{
    private const string A = "3f9c2a7e";
    private const string B = "aaaa1111";
    private const string C = "bbbb2222";
    private const long T1 = 1_800_000_000_000L;
    private const long T2 = T1 + 60_000;
    private const long T3 = T2 + 60_000;
    private const long T4 = T3 + 60_000;

    private static readonly FakeSecrets Fakes = new(23);

    /// <summary>The real detector, counting its calls.</summary>
    private sealed class CountingDetector
    {
        public int Calls { get; private set; }

        public IReadOnlyList<string> Detect(string? text)
        {
            Calls++;
            return SensitiveContentScanner.ScanSecrets(text);
        }
    }

    private static ScanSummary Scan(ScanRig rig, long now, CountingDetector? detector = null)
    {
        var scanner = rig.Scanner(now);
        if (detector is not null)
            scanner.SecretDetector = detector.Detect;
        return Assert.IsType<ScanOutcome.Done>(scanner.Scan()).Summary;
    }

    /// <summary>The secret key of the entry as the scan reads it now.</summary>
    private static string KeyOf(ScanRig rig, string id)
    {
        var view = rig.Scanner(T1).ReadSnapshot().Entries[id].View;
        return SyncSecret.secretKey(view.Content, view.Tags);
    }

    /// <summary>Counts every row write (an insert, or the update of an upsert) on <c>git_sync_entry</c> per entry id from now on.</summary>
    private static void CountWrites(ScanRig rig)
    {
        rig.Exec("CREATE TABLE test_row_writes (entry_id TEXT NOT NULL)");
        rig.Exec("CREATE TRIGGER test_row_insert AFTER INSERT ON git_sync_entry BEGIN INSERT INTO test_row_writes VALUES (NEW.entry_id); END");
        rig.Exec("CREATE TRIGGER test_row_update AFTER UPDATE ON git_sync_entry BEGIN INSERT INTO test_row_writes VALUES (NEW.entry_id); END");
    }

    private static long Writes(ScanRig rig, string id)
    {
        using var cmd = rig.Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM test_row_writes WHERE entry_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Edit(ScanRig rig, string id, UpdateEntryOpts opts) =>
        rig.Store.Update(Tier.Warm, ContentType.Memory, id, opts);

    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_TextWithAToken_IsBlockedWithTheKey()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "the token is " + Fakes.Get("github-token"));
        rig.AddMemory(Tier.Warm, B, "Use bun, not npm.");

        var summary = Scan(rig, T1);

        var row = rig.Rows()[A];
        Assert.Equal("secret-detected", row.BlockedReason);
        Assert.Equal(KeyOf(rig, A), row.BlockedHash);
        Assert.Null(row.AllowedHash);
        Assert.Equal([A], summary.Blocked);
        Assert.Empty(summary.Unblocked);
        var clean = rig.Rows()[B];
        Assert.Equal((null, null, null), (clean.BlockedReason, clean.BlockedHash, clean.AllowedHash));
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("project")]
    [InlineData("scope")]
    public void A_TokenInATag_ProjectOrScope_IsBlocked(string field)
    {
        using var rig = new ScanRig();
        var token = Fakes.Get("aws-key");
        rig.AddMemory(
            Tier.Warm, A, "A clean text.",
            tags: field == "tag" ? ["clean", token] : ["clean"],
            project: field == "project" ? token : null,
            scope: field == "scope" ? token : null);

        var summary = Scan(rig, T1);

        var row = rig.Rows()[A];
        Assert.Equal("secret-detected", row.BlockedReason);
        Assert.Equal(KeyOf(rig, A), row.BlockedHash);
        Assert.Equal([A], summary.Blocked);
    }

    [Fact]
    public void AnEntryTheMainLoopCallsNoChange_IsStillChecked()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "the key is " + Fakes.Get("openai-key"));
        // A first scan that finds nothing leaves an active row whose scanned values match the entry and whose block
        // columns are all null, as an answer path that wrote the text would.
        var blind = rig.Scanner(T1);
        blind.SecretDetector = _ => [];
        Assert.IsType<ScanOutcome.Done>(blind.Scan());
        var before = rig.Rows()[A];
        Assert.Equal((null, null, null), (before.BlockedReason, before.BlockedHash, before.AllowedHash));
        CountWrites(rig);

        var summary = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.Equal(before with { BlockedReason = "secret-detected", BlockedHash = KeyOf(rig, A) }, row);
        Assert.Equal((0, 0, 0, 0, 0), (summary.New, summary.Content, summary.Tags, summary.Sticky, summary.Tier));
        Assert.Equal([A], summary.Blocked);
        Assert.Equal(1, Writes(rig, A));
    }

    [Fact]
    public void ABlockedUnchangedEntry_IsNotScannedAgain()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, Fakes.Get("private-key"));
        Scan(rig, T1);
        var blocked = rig.Rows()[A];
        Assert.Equal("secret-detected", blocked.BlockedReason);
        var detector = new CountingDetector();

        var summary = Scan(rig, T2, detector);

        Assert.Equal(0, detector.Calls);
        Assert.Equal(blocked, rig.Rows()[A]);
        Assert.Empty(summary.Blocked);
        Assert.Empty(summary.Unblocked);
    }

    [Fact]
    public void AnAllowedUnchangedEntry_IsNotScanned_AndKeepsItsAllowedHash()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "the token is " + Fakes.Get("github-token"));
        Scan(rig, T1);
        var key = KeyOf(rig, A);
        var allowed = rig.Rows()[A] with { BlockedReason = null, BlockedHash = null, AllowedHash = key };
        GitSyncRows.Write(rig.Connection, allowed);
        var detector = new CountingDetector();

        var summary = Scan(rig, T2, detector);

        Assert.Equal(0, detector.Calls);
        Assert.Equal(allowed, rig.Rows()[A]);
        Assert.Empty(summary.Blocked);
        Assert.Empty(summary.Unblocked);
    }

    [Fact]
    public void AnAllowedEntryThatStillCarriesTheMark_IsClearedWithoutAScan()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "the token is " + Fakes.Get("github-token"));
        Scan(rig, T1);
        var key = KeyOf(rig, A);
        var marked = rig.Rows()[A] with { AllowedHash = key };
        GitSyncRows.Write(rig.Connection, marked);
        var detector = new CountingDetector();

        var summary = Scan(rig, T2, detector);

        Assert.Equal(0, detector.Calls);
        Assert.Equal(marked with { BlockedReason = null, BlockedHash = null }, rig.Rows()[A]);
        Assert.Equal([A], summary.Unblocked);
        Assert.Empty(summary.Blocked);
    }

    [Fact]
    public void ACleanEdit_LiftsTheBlock_AndListsItUnblocked()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "the token is " + Fakes.Get("github-token"));
        Scan(rig, T1);
        Assert.Equal("secret-detected", rig.Rows()[A].BlockedReason);
        Edit(rig, A, new UpdateEntryOpts { Content = "the token was rotated" });

        var summary = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.Equal((null, null, null), (row.BlockedReason, row.BlockedHash, row.AllowedHash));
        Assert.Equal(1, summary.Content);
        Assert.Equal([A], summary.Unblocked);
        Assert.Empty(summary.Blocked);
    }

    [Fact]
    public void ATokenAddedInATagAfterAnUnblock_IsBlockedAgain()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "the token is " + Fakes.Get("github-token"), tags: ["clean"]);
        Scan(rig, T1);
        var before = KeyOf(rig, A);
        GitSyncRows.Write(rig.Connection, rig.Rows()[A] with { BlockedReason = null, BlockedHash = null, AllowedHash = before });
        Assert.Empty(Scan(rig, T2).Blocked);
        Edit(rig, A, new UpdateEntryOpts { Tags = ["clean", Fakes.Get("aws-key")] });

        var summary = Scan(rig, T3);

        var row = rig.Rows()[A];
        var after = KeyOf(rig, A);
        Assert.NotEqual(before, after);
        Assert.Equal("secret-detected", row.BlockedReason);
        Assert.Equal(after, row.BlockedHash);
        Assert.Equal(before, row.AllowedHash);
        Assert.Equal([A], summary.Blocked);
    }

    [Fact]
    public void ASecretReplacesAWriterReason()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        var first = rig.Rows()[A];
        GitSyncRows.Write(rig.Connection, first with { BlockedReason = "too-large", BlockedHash = first.ScannedContentHash });
        Edit(rig, A, new UpdateEntryOpts { Content = "one, with " + Fakes.Get("openai-key") });

        var blocked = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.Equal("secret-detected", row.BlockedReason);
        Assert.Equal(KeyOf(rig, A), row.BlockedHash);
        Assert.Equal([A], blocked.Blocked);

        Edit(rig, A, new UpdateEntryOpts { Content = "one, without it" });

        var cleared = Scan(rig, T3);

        row = rig.Rows()[A];
        Assert.Equal((null, null), (row.BlockedReason, row.BlockedHash));
        Assert.Equal([A], cleared.Unblocked);
    }

    [Fact]
    public void AWriterReasonIsKept_WhenThereIsNoHit()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        var first = rig.Rows()[A];
        GitSyncRows.Write(rig.Connection, first with { BlockedReason = "too-large", BlockedHash = first.ScannedContentHash });
        Edit(rig, A, new UpdateEntryOpts { Content = "two" });
        var detector = new CountingDetector();

        var summary = Scan(rig, T2, detector);

        var row = rig.Rows()[A];
        Assert.True(detector.Calls > 0);
        Assert.Equal(("too-large", first.ScannedContentHash), (row.BlockedReason, row.BlockedHash));
        Assert.Empty(summary.Blocked);
        Assert.Empty(summary.Unblocked);
    }

    [Fact]
    public void AReactivatedEntry_StartsClean_ThenIsChecked()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "the token is " + Fakes.Get("github-token"));
        // A stale allowed key equal to the current one: a fresh row must not inherit it.
        GitSyncRows.Write(rig.Connection, new GitSyncRow(
            A, SyncMerge.RowState.Forgotten, null, null, null, null, null, null, null, null, null, null,
            "old", "too-large", KeyOf(rig, A)));
        CountWrites(rig);

        var summary = Scan(rig, T1);

        var row = rig.Rows()[A];
        Assert.True(row.State.IsActive);
        Assert.Equal(1, summary.Reactivated);
        Assert.Equal("secret-detected", row.BlockedReason);
        Assert.Equal(KeyOf(rig, A), row.BlockedHash);
        Assert.Null(row.AllowedHash);
        Assert.Equal([A], summary.Blocked);
        Assert.Equal(1, Writes(rig, A));
    }

    [Fact]
    public void ForgottenAndLeftRowsWithoutAnEntry_AreNotEvaluated()
    {
        using var rig = new ScanRig();
        var forgotten = new GitSyncRow(
            B, SyncMerge.RowState.Forgotten, null, null, null, null, null, null, null, null, null, null,
            "bh", "secret-detected", null);
        var left = forgotten with { EntryId = C, State = SyncMerge.RowState.Left };
        GitSyncRows.Write(rig.Connection, forgotten);
        GitSyncRows.Write(rig.Connection, left);
        var detector = new CountingDetector();

        var summary = Scan(rig, T1, detector);

        Assert.Equal(0, detector.Calls);
        var rows = rig.Rows();
        Assert.Equal(forgotten, rows[B]);
        Assert.Equal(left, rows[C]);
        Assert.Empty(summary.Blocked);
        Assert.Empty(summary.Unblocked);
    }

    [Fact]
    public void OneWritePerId()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        var first = rig.Rows()[A];
        CountWrites(rig);
        Edit(rig, A, new UpdateEntryOpts { Content = "one, with " + Fakes.Get("github-token") });

        var summary = Scan(rig, T4);

        var row = rig.Rows()[A];
        Assert.Equal(1, summary.Content);
        Assert.NotEqual(first.ContentStamp, row.ContentStamp);
        Assert.NotEqual(first.ScannedContentHash, row.ScannedContentHash);
        Assert.Equal("secret-detected", row.BlockedReason);
        Assert.Equal(KeyOf(rig, A), row.BlockedHash);
        Assert.Equal([A], summary.Blocked);
        Assert.Equal(1, Writes(rig, A));
    }

    [Fact]
    public void email_only_text_is_not_blocked()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "Push to git@github.com:strvmarv/total-recall.git; mail noreply@example.com.");

        var summary = Scan(rig, T1);

        var row = rig.Rows()[A];
        Assert.Equal((null, null), (row.BlockedReason, row.BlockedHash));
        Assert.Empty(summary.Blocked);
    }
}
