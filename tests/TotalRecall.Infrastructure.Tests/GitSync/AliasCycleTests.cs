using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-iii part 5 (alias detection and skip-insert, #165): the whole path over machines on one local bare
/// repository (<see cref="TwoMachineRig"/> with its third machine T). The legacy entries (a host file imported before git
/// sync, with a random id on each machine) are seeded directly with their host <c>import_log</c> row
/// (<see cref="AliasTestSupport.SeedLegacy"/>): a current import derives its id from the hash, so the importer cannot
/// produce the case. Two legacy copies leave neither machine with a duplicate and one alias row on each; a third machine
/// that holds B's record as an entry converges onto A's canonical record once B's record is removed, carrying its tier,
/// tags and pin, with the differing local text in a <c>conflict</c> item (a held <c>pin-edit</c> under a local pin).
/// </summary>
public sealed class AliasCycleTests : IDisposable
{
    private const string IdA = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string IdB = "3f9c2a7e-0000-4000-8000-0000000000b2";

    private static readonly string H = AliasTestSupport.Hash("~/.claude/memory/use-bun.md");

    private readonly TwoMachineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ---- helpers ----

    /// <summary>The cycle finished: the pull is complete with nothing deferred and the push was not skipped.</summary>
    private static CycleSummary Done(CycleOutcome outcome)
    {
        var summary = Assert.IsType<CycleOutcome.Done>(outcome).Summary;
        Assert.True(summary.Apply!.Complete);
        Assert.Empty(summary.Apply.Deferred);
        Assert.Empty(summary.Apply.Held);
        Assert.Null(summary.PushSkipped);
        return summary;
    }

    private static void SeedLegacy(CycleRig machine, string id, string text) =>
        AliasTestSupport.SeedLegacy(machine.Pull.Store, machine.Pull.Connection, id, text, H, Tier.Warm);

    /// <summary>Every memory of the machine as <c>id|tier</c>, ordinal by id.</summary>
    private static List<string> Memories(CycleRig machine)
    {
        var all = new List<string>();
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            foreach (var entry in machine.Pull.Store.List(tier, ContentType.Memory))
                all.Add($"{entry.Id}|{TierName(tier)}");
        all.Sort(StringComparer.Ordinal);
        return all;
    }

    private static string TierName(Tier tier) => tier.IsHot ? "hot" : tier.IsWarm ? "warm" : "cold";

    private static (Tier Tier, Entry Entry) Found(CycleRig machine, string id)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (machine.Pull.Store.Get(tier, ContentType.Memory, id) is { } entry)
                return (tier, entry);
        throw new Xunit.Sdk.XunitException("the memory " + id + " is not stored");
    }

    private static IReadOnlyList<string> Aliases(CycleRig machine) => AliasTestSupport.AliasRows(machine.Pull.Connection);

    private static IReadOnlyList<string> Rows(CycleRig machine) =>
        [.. GitSyncRows.ReadAll(machine.Pull.Connection).Keys.Order(StringComparer.Ordinal)];

    // ---- two legacy copies ----

    [GitFact]
    public async Task TwoLegacyCopies_NeitherMachineGetsADuplicate_AndEachRecordsOneAlias()
    {
        const string text = "Use bun, not npm.";
        _rig.Seed();
        _rig.JoinB();
        SeedLegacy(_rig.A, IdA, text);
        SeedLegacy(_rig.B, IdB, text);

        var a1 = Done(await _rig.RunA());
        Assert.Equal([IdA], Assert.IsType<PushOutcome.Pushed>(a1.Push).Summary.Written);
        var b = Done(await _rig.RunB());
        Assert.Equal([IdB], Assert.IsType<PushOutcome.Pushed>(b.Push).Summary.Written);
        Done(await _rig.RunA());

        Assert.Equal([$"{IdA}|warm"], Memories(_rig.A));
        Assert.Equal([$"{IdB}|warm"], Memories(_rig.B));
        Assert.Equal([IdA], Rows(_rig.A));
        Assert.Equal([IdB], Rows(_rig.B));
        var alias = $"{IdA}|{IdB}|{H}|open";
        Assert.Equal([alias], Aliases(_rig.A));
        Assert.Equal([alias], Aliases(_rig.B));
        // the published records carry the host origin through the pusher's first non-sync import row
        foreach (var id in new[] { IdA, IdB })
        {
            var record = RecordReader.Parse(
                System.Text.Encoding.UTF8.GetBytes(_rig.A.Push.RemoteFile(SyncValidation.recordPath(id))),
                SyncValidation.recordPath(id)).Parsed!.Record;
            Assert.Equal(H, record.Origin!.Value.Hash);
        }
    }

    // ---- a third machine converges ----

    [GitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AThirdMachine_ConvergesOntoTheCanonicalEntry_WhenTheOtherRecordIsRemoved(bool pinned)
    {
        const string textA = "Use bun.";
        const string textB = "Use bun, not npm.";
        _rig.Seed();
        _rig.JoinB();
        SeedLegacy(_rig.A, IdA, textA);
        SeedLegacy(_rig.B, IdB, textB);

        // B publishes IdB; T joins and takes it as an ordinary entry
        Assert.Equal([IdB], Assert.IsType<PushOutcome.Pushed>(Done(await _rig.RunB()).Push).Summary.Written);
        _rig.JoinT();
        Done(await _rig.RunT());
        Assert.Equal([$"{IdB}|warm"], Memories(_rig.T));

        // T's own changes to IdB: a tag, the hot tier, and (pinned) a pin
        _rig.T.Push.Now += 60_000;
        _rig.T.Push.Edit(Tier.Warm, IdB, new UpdateEntryOpts { Tags = ["t-local"] });
        _rig.T.Pull.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, IdB);
        if (pinned)
            _rig.T.Pull.Store.SetSticky(ContentType.Memory, IdB, true);

        // A publishes the canonical IdA and skips IdB
        _rig.A.Push.Now += 120_000;
        Done(await _rig.RunA());
        Assert.Equal([$"{IdA}|warm"], Memories(_rig.A));
        Assert.Equal([$"{IdA}|{IdB}|{H}|open"], Aliases(_rig.A));

        // T meets IdA: skipped as an alias of its IdB, still one entry
        _rig.T.Push.Now += 60_000;
        Done(await _rig.RunT());
        Assert.Equal([$"{IdB}|hot"], Memories(_rig.T));
        Assert.Equal([$"{IdA}|{IdB}|{H}|open"], Aliases(_rig.T));

        // another machine removes IdB's record; T converges onto IdA
        _rig.RemoveOnRemote(IdB);
        _rig.T.Push.Now += 60_000;
        var t = Done(await _rig.RunT());

        Assert.Empty(t.Apply!.Deleted);
        Assert.Empty(t.Apply.GuardTripped);
        Assert.Equal([$"{IdA}|hot"], Memories(_rig.T));
        Assert.Equal([IdA], Rows(_rig.T));
        Assert.Equal([$"{IdA}|{IdB}|{H}|resolved"], Aliases(_rig.T));
        Assert.Null(GitSyncReviews.ReadHeldItem(_rig.T.Pull.Connection, IdA));
        Assert.Null(GitSyncReviews.ReadHeldItem(_rig.T.Pull.Connection, IdB));
        var (_, entry) = Found(_rig.T, IdA);
        Assert.Contains("t-local", entry.Tags);
        Assert.Equal(pinned, _rig.T.Pull.Store.IsSticky(ContentType.Memory, IdA));
        Assert.Null(GitSyncReviews.ReadConflictItem(_rig.T.Pull.Connection, IdB));
        Assert.Null(GitSyncReviews.ReadPinItem(_rig.T.Pull.Connection, IdB, "pin-edit"));
        if (!pinned)
        {
            Assert.Equal(textA, entry.Content);
            var item = GitSyncReviews.ReadConflictItem(_rig.T.Pull.Connection, IdA)!;
            var loser = Assert.Single(GitSyncReviews.ReadConflictLosers(item.PayloadJson));
            Assert.Equal(("local", textB, TwoMachineRig.MachineT), (SyncConflict.sideName(loser.Side), loser.Text, loser.Machine));
            Assert.Null(GitSyncReviews.ReadPinItem(_rig.T.Pull.Connection, IdA, "pin-edit"));
        }
        else
        {
            Assert.Equal(textB, entry.Content);
            Assert.Equal("open", GitSyncReviews.ReadPinItem(_rig.T.Pull.Connection, IdA, "pin-edit")!.State);
            Assert.Null(GitSyncReviews.ReadConflictItem(_rig.T.Pull.Connection, IdA));
        }
    }
}
