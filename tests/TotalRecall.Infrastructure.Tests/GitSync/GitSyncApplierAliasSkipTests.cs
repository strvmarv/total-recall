using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Alias D1 and D6 (git sync part 5, #165): a pulled record with no row and no local entry (W1) whose origin hash maps in
/// <c>import_log</c> to a different existing local memory is not inserted; one <c>open</c> <c>git_sync_alias</c> row
/// <c>(canonical, other, h)</c> is written per non-canonical member (the only write), planned before the transaction and
/// recomputed inside it (a difference defers), and the apply is a <c>NoOp</c>.
/// </summary>
public sealed class GitSyncApplierAliasSkipTests : IDisposable
{
    // Ordinal: C < Lo < Hi < Top.
    private const string C = "3f9c2a7e-0000-4000-8000-0000000000a0";
    private const string Lo = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string Hi = "3f9c2a7e-0000-4000-8000-0000000000b2";
    private const string Top = "3f9c2a7e-0000-4000-8000-0000000000c3";

    private static readonly string H = AliasTestSupport.Hash("host file");

    private readonly ApplyRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static T Is<T>(EntryResult r) where T : EntryResult =>
        r is EntryResult.Failed f && typeof(T) != typeof(EntryResult.Failed)
            ? throw new Xunit.Sdk.XunitException(f.Detail)
            : Assert.IsType<T>(r);

    private static SyncRecordModule.SyncRecord Rec(string id, bool sticky = false, string? originHash = null) =>
        ApplyRig.Rec(id, "Use bun.", sticky: sticky, originHash: originHash ?? H, writtenBy: ApplyRig.OtherMachine);

    private void Seed(string id, string? hash = null, string sourceTool = "claude-code", ContentType? type = null) =>
        AliasTestSupport.SeedLegacy(_rig.Scan.Store, _rig.Scan.Connection, id, "Use bun.", hash ?? H, sourceTool: sourceTool, type: type);

    private IReadOnlyList<string> Rows() => AliasTestSupport.AliasRows(_rig.Scan.Connection);

    private void SeedAliasRow(string canonical, string other, string state)
    {
        using var cmd = _rig.Scan.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO git_sync_alias (canonical_id, other_id, origin_hash, state) VALUES ($c, $o, $h, $s)";
        cmd.Parameters.AddWithValue("$c", canonical);
        cmd.Parameters.AddWithValue("$o", other);
        cmd.Parameters.AddWithValue("$h", H);
        cmd.Parameters.AddWithValue("$s", state);
        cmd.ExecuteNonQuery();
    }

    private void AssertSkipped(string id)
    {
        Assert.Null(_rig.Entry(id));
        Assert.False(_rig.Scan.Rows().ContainsKey(id));
    }

    private void AssertInserted(EntryResult result, string id)
    {
        Assert.Equal(CarriedKind.Inserted, Is<EntryResult.Carried>(result).Kind);
        Assert.NotNull(_rig.Entry(id));
        Assert.Empty(Rows());
    }

    // ---- the skip ----

    [Fact]
    public void ARecordWhoseOriginADifferentLocalIdImported_IsSkipped_WithOneOpenAliasRow()
    {
        Seed(Lo);
        var before = _rig.Full(); // Full() does not include the alias table

        Is<EntryResult.NoOp>(_rig.Apply(Rec(Hi)));

        AssertSkipped(Hi);
        Assert.Equal([$"{Lo}|{Hi}|{H}|open"], Rows());
        Assert.Equal(before, _rig.Full());
        Assert.Equal("Use bun.", _rig.Entry(Lo)!.Row.Content);
        Assert.Equal(0, _rig.Embedder.Calls);
    }

    [Fact]
    public void TheSamePullAgain_WritesNoRow_AndOpensNoTransaction()
    {
        Seed(Lo);
        Is<EntryResult.NoOp>(_rig.Apply(Rec(Hi)));
        var transactions = _rig.Applier.TransactionCount;
        var before = _rig.Full();

        Is<EntryResult.NoOp>(_rig.Apply(Rec(Hi)));

        Assert.Equal(transactions, _rig.Applier.TransactionCount);
        Assert.Equal([$"{Lo}|{Hi}|{H}|open"], Rows());
        Assert.Equal(before, _rig.Full());
        AssertSkipped(Hi);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheCanonicalId_IsTheLowestOfTheGroup(bool recordLower)
    {
        var (record, local) = recordLower ? (Lo, Hi) : (Hi, Lo);
        Seed(local);

        Is<EntryResult.NoOp>(_rig.Apply(Rec(record)));

        AssertSkipped(record);
        Assert.Equal([$"{Lo}|{Hi}|{H}|open"], Rows()); // canonical = the lower id, whichever side holds it
        Assert.NotNull(_rig.Entry(local));
    }

    [Fact]
    public void TwoLocalIdsForOneHash_SkipTheRecord_WithARowPerNonCanonicalMember()
    {
        Seed(Lo);
        Seed(Hi);

        Is<EntryResult.NoOp>(_rig.Apply(Rec(Top)));

        AssertSkipped(Top);
        Assert.Equal([$"{Lo}|{Hi}|{H}|open", $"{Lo}|{Top}|{H}|open"], Rows());
    }

    [Fact]
    public void AGitSyncLogRow_IsATarget()
    {
        Seed(Lo, sourceTool: "git-sync");

        Is<EntryResult.NoOp>(_rig.Apply(Rec(Hi)));

        AssertSkipped(Hi);
        Assert.Equal([$"{Lo}|{Hi}|{H}|open"], Rows());
    }

    [Fact]
    public void AnUnprovenPull_SkipsTheSame()
    {
        Seed(Lo);

        Is<EntryResult.NoOp>(_rig.Apply(Rec(Hi), proven: false));

        AssertSkipped(Hi);
        Assert.Equal([$"{Lo}|{Hi}|{H}|open"], Rows());
    }

    [Fact]
    public void AStickyRecordsSkip_WritesNoRowAndNoNewPinItem()
    {
        Seed(Lo);

        Is<EntryResult.NoOp>(_rig.Apply(Rec(Hi, sticky: true)));

        AssertSkipped(Hi);
        Assert.Equal(0, _rig.ReviewRows());
        Assert.Empty(_rig.PinItems(Hi));
        Assert.Equal([$"{Lo}|{Hi}|{H}|open"], Rows());
    }

    [Theory]
    [InlineData("open")]
    [InlineData("converged")]
    [InlineData("resolved")]
    [InlineData("abandoned")]
    public void AnExistingAliasRowOfAnyState_IsLeftAlone_AndTheSkipStillApplies(string state)
    {
        Seed(Lo);
        SeedAliasRow(Lo, Hi, state);
        var transactions = _rig.Applier.TransactionCount;

        Is<EntryResult.NoOp>(_rig.Apply(Rec(Hi)));

        AssertSkipped(Hi);
        Assert.Equal(transactions, _rig.Applier.TransactionCount);
        Assert.Equal([$"{Lo}|{Hi}|{H}|{state}"], Rows());
    }

    [Fact]
    public void ALaterLowerArrival_AddsItsRow_AndLeavesTheEarlierRow()
    {
        // Twin B = Hi; record A = Lo (A < B) is skipped, then record C (C < A).
        Seed(Hi);
        Is<EntryResult.NoOp>(_rig.Apply(Rec(Lo)));
        Assert.Equal([$"{Lo}|{Hi}|{H}|open"], Rows());

        Is<EntryResult.NoOp>(_rig.Apply(Rec(C)));

        AssertSkipped(C);
        AssertSkipped(Lo);
        Assert.Equal([$"{C}|{Hi}|{H}|open", $"{Lo}|{Hi}|{H}|open"], Rows());
    }

    // ---- no alias: inserted (regression guards) ----

    [Fact]
    public void AMatchingLogRowWhoseEntryWasDeleted_IsNoAlias_AndTheRecordIsInserted()
    {
        Seed(Lo);
        _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Lo);

        AssertInserted(_rig.Apply(Rec(Hi)), Hi);
    }

    [Fact]
    public void ALogRowForTheRecordsOwnId_IsNoAlias()
    {
        new ImportLog(_rig.Scan.Connection).LogImport(
            "claude-code", "/home/u/.claude/memory/own.md", H, Hi, Tier.Warm, ContentType.Memory);

        AssertInserted(_rig.Apply(Rec(Hi)), Hi);
    }

    [Fact]
    public void AKnowledgeLogRow_IsNoTarget()
    {
        Seed(Lo, type: ContentType.Knowledge);

        AssertInserted(_rig.Apply(Rec(Hi)), Hi);
    }

    [Fact]
    public void AnIneligibleLocalId_IsNoTarget()
    {
        Seed("LEGACY_ID");

        AssertInserted(_rig.Apply(Rec(Hi)), Hi);
    }

    [Fact]
    public void ARecordWithoutAnOrigin_IsInserted()
    {
        var record = ApplyRig.Rec(Hi, "Use bun.", writtenBy: ApplyRig.OtherMachine);
        Seed(Lo, hash: AliasTestSupport.Hash(record.Content));

        AssertInserted(_rig.Apply(record), Hi);
    }

    // ---- a racing importer defers ----

    [Fact]
    public void ARacingImportLogRow_DefersTheEntry_AndWritesNothing()
    {
        Seed(Lo);
        _rig.Applier.BeforeTransaction = id =>
        {
            if (id == Hi)
                Seed(Top);
        };

        var deferred = Is<EntryResult.Deferred>(_rig.Apply(Rec(Hi)));

        Assert.Equal("the entry changed since the decision was made", deferred.Reason);
        Assert.Empty(Rows());
        AssertSkipped(Hi);
    }

    [Fact]
    public void ARacingDeletionOfTheTarget_DefersTheEntry_AndWritesNothing()
    {
        Seed(Lo);
        _rig.Applier.BeforeTransaction = id =>
        {
            if (id == Hi)
                _rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Lo);
        };

        var deferred = Is<EntryResult.Deferred>(_rig.Apply(Rec(Hi)));

        Assert.Equal("the entry changed since the decision was made", deferred.Reason);
        Assert.Empty(Rows());
        AssertSkipped(Hi);
    }

    // ---- the pull ----

    private static (PullRig Rig, GitSyncApplier Applier) NewPull(Func<long> now)
    {
        var rig = new PullRig();
        rig.Join();
        rig.PutManifest();
        rig.Commit("manifest");
        rig.Clone();
        return (rig, new GitSyncApplier(rig.Client, rig.Store, new CountingEmbedder(), new ApplyOptions(0), now));
    }

    private static async Task<ApplySummary> Pull(PullRig rig, GitSyncApplier applier, long now)
    {
        var fetch = await rig.Fetcher().FetchAsync();
        var result = fetch switch
        {
            FetchOutcome.Fetched f => f.Result,
            FetchOutcome.NothingNew n => n.Result,
            _ => throw new Xunit.Sdk.XunitException("the fetch did not succeed: " + fetch),
        };
        var scan = Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(rig.Connection, () => now).Scan()).Summary;
        return Assert.IsType<ApplyOutcome.Done>(await applier.ApplyAsync(result, scan)).Summary;
    }

    private static bool Has(PullRig rig, string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(t => rig.Store.Get(t, ContentType.Memory, id) is not null);

    private static IReadOnlyList<string> GitSyncLogHashes(PullRig rig, string id)
    {
        var hashes = new List<string>();
        using var cmd = rig.Connection.CreateCommand();
        cmd.CommandText = "SELECT content_hash FROM import_log WHERE target_entry_id = $id AND source_tool = 'git-sync'";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            hashes.Add(r.GetString(0));
        return hashes;
    }

    [GitFact]
    public async Task AFreshMachinesFirstPull_WithBothRecordsOfAGroup_InsertsOneSkipsTheOther_AndIsComplete()
    {
        const long now = 1_800_000_000_000L;
        var (rig, applier) = NewPull(() => now);
        using var pullRig = rig;
        rig.PutRecord(Rec(Lo));
        rig.PutRecord(Rec(Hi));
        var head = rig.Commit("records");

        var summary = await Pull(rig, applier, now);

        Assert.True(summary.Complete);
        Assert.Empty(summary.Deferred);
        Assert.Empty(summary.Failed);
        Assert.Equal(head, rig.Last);
        Assert.Equal(1, summary.Inserted);
        Assert.True(Has(rig, Lo));
        Assert.False(Has(rig, Hi));
        Assert.Equal([H], GitSyncLogHashes(rig, Lo));
        Assert.Equal([$"{Lo}|{Hi}|{H}|open"], AliasTestSupport.AliasRows(rig.Connection));
        Assert.False(GitSyncRows.ReadAll(rig.Connection).ContainsKey(Hi));
    }

    [GitFact]
    public async Task ASkippedRecord_DoesNotHoldL_InAPullWithAnotherChange()
    {
        const long now = 1_800_000_000_000L;
        var (rig, applier) = NewPull(() => now);
        using var pullRig = rig;
        AliasTestSupport.SeedLegacy(rig.Store, rig.Connection, Lo, "Use bun.", H);
        rig.PutRecord(Rec(Hi));
        rig.PutRecord(ApplyRig.Rec(Top, "Unrelated.", writtenBy: ApplyRig.OtherMachine));
        var head = rig.Commit("records");

        var summary = await Pull(rig, applier, now);

        Assert.True(summary.Complete);
        Assert.Empty(summary.Deferred);
        Assert.Equal(head, rig.Last);
        Assert.Equal(1, summary.Inserted);
        Assert.Equal(1, summary.Unchanged);
        Assert.True(Has(rig, Top));
        Assert.True(Has(rig, Lo));
        Assert.False(Has(rig, Hi));
        Assert.Equal([$"{Lo}|{Hi}|{H}|open"], AliasTestSupport.AliasRows(rig.Connection));
    }
}
