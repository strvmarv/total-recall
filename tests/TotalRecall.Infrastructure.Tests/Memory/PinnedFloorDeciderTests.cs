using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Memory;

public sealed class PinnedFloorDeciderTests
{
    private static readonly FloorThresholds Default = new(Enabled: true, EveryNTurns: 6, GrowthTokens: 6000);

    [Fact]
    public void FirstTurn_NotSeeded_SeedsAndSkips()
    {
        var (verdict, next) = PinnedFloorDecider.Decide(
            new FloorState("s", 0, 0, 0, false),
            new FloorSignal(CurrentTranscriptBytes: 2000),
            Default);

        Assert.Equal(FloorVerdict.Skip, verdict);
        Assert.True(next.Seeded);
        Assert.Equal(1, next.TurnCount);
        Assert.Equal(1, next.LastInjectedTurn);
        Assert.Equal(2000, next.LastInjectedBytes);
    }

    [Fact]
    public void Disabled_AlwaysSkips_StillBumpsTurn()
    {
        var disabled = new FloorThresholds(false, 6, 6000);
        var (verdict, next) = PinnedFloorDecider.Decide(
            new FloorState("s", 3, 1, 0, true), new FloorSignal(99999), disabled);

        Assert.Equal(FloorVerdict.Skip, verdict);
        Assert.Equal(4, next.TurnCount);
        Assert.Equal(1, next.LastInjectedTurn);
    }

    [Fact]
    public void TurnThreshold_Trips_Injects()
    {
        var (verdict, next) = PinnedFloorDecider.Decide(
            new FloorState("s", 6, 1, 0, true), new FloorSignal(null), Default);

        Assert.Equal(FloorVerdict.Inject, verdict);
        Assert.Equal(7, next.TurnCount);
        Assert.Equal(7, next.LastInjectedTurn);
    }

    [Fact]
    public void BelowTurnThreshold_NoGrowth_Skips()
    {
        var (verdict, _) = PinnedFloorDecider.Decide(
            new FloorState("s", 2, 1, 0, true), new FloorSignal(null), Default);
        Assert.Equal(FloorVerdict.Skip, verdict);
    }

    [Fact]
    public void GrowthThreshold_Trips_Injects()
    {
        // growth_tokens 6000 * 4 bytes/token = 24000. baseline 1000, current 26000 -> delta 25000 >= 24000.
        var (verdict, next) = PinnedFloorDecider.Decide(
            new FloorState("s", 2, 1, 1000, true), new FloorSignal(26000), Default);

        Assert.Equal(FloorVerdict.Inject, verdict);
        Assert.Equal(26000, next.LastInjectedBytes);
    }

    [Fact]
    public void NullBytes_FallsBackToTurnCountOnly()
    {
        var (verdict, _) = PinnedFloorDecider.Decide(
            new FloorState("s", 3, 1, 1000, true), new FloorSignal(null), Default);
        Assert.Equal(FloorVerdict.Skip, verdict);
    }

    [Fact]
    public void DegenerateEveryN_Zero_ClampsToDefault_DoesNotInjectEveryTurn()
    {
        var bad = new FloorThresholds(true, 0, 6000);
        // seeded, only 1 turn since last injection -> with clamp(6) this must Skip, not Inject.
        var (verdict, _) = PinnedFloorDecider.Decide(
            new FloorState("s", 2, 1, 0, true), new FloorSignal(null), bad);
        Assert.Equal(FloorVerdict.Skip, verdict);
    }

    [Fact]
    public void DegenerateGrowth_Zero_DoesNotTripGrowthArm()
    {
        var bad = new FloorThresholds(true, 6, 0);
        // below turn threshold; growth clamped off -> Skip despite huge byte delta.
        var (verdict, _) = PinnedFloorDecider.Decide(
            new FloorState("s", 2, 1, 0, true), new FloorSignal(1_000_000), bad);
        Assert.Equal(FloorVerdict.Skip, verdict);
    }

    [Fact]
    public void GrowthExactlyAtThreshold_Injects()
    {
        // GrowthTokens 6000 * 4 = 24000; baseline 0, current 24000 -> delta == threshold -> inclusive inject.
        var (verdict, _) = PinnedFloorDecider.Decide(
            new FloorState("s", 2, 1, 0, true), new FloorSignal(24000), Default);
        Assert.Equal(FloorVerdict.Inject, verdict);
    }

    [Fact]
    public void InjectResetsBaseline_AcrossTwoCycles()
    {
        var cfg = new FloorThresholds(true, 3, 6000);
        var st = new FloorState("s", 0, 0, 0, false);
        // turn 1 seeds + skips
        (_, st) = PinnedFloorDecider.Decide(st, new FloorSignal(null), cfg);

        var verdicts = new System.Collections.Generic.List<FloorVerdict>();
        for (int i = 0; i < 6; i++)
        {
            var r = PinnedFloorDecider.Decide(st, new FloorSignal(null), cfg);
            verdicts.Add(r.Verdict);
            st = r.Next;
        }
        // turns 2,3 Skip; 4 Inject; 5,6 Skip; 7 Inject
        Assert.Equal(
            new[] { FloorVerdict.Skip, FloorVerdict.Skip, FloorVerdict.Inject,
                    FloorVerdict.Skip, FloorVerdict.Skip, FloorVerdict.Inject },
            verdicts);
    }

    [Fact]
    public void NullBytesOnInject_KeepsBaseline_ThenGrowthCanFireNextTurn()
    {
        // Inject via turn threshold with null bytes -> baseline stays 0.
        var afterInject = PinnedFloorDecider.Decide(
            new FloorState("s", 6, 1, 0, true), new FloorSignal(null), Default);
        Assert.Equal(FloorVerdict.Inject, afterInject.Verdict);
        Assert.Equal(0, afterInject.Next.LastInjectedBytes); // baseline unchanged (null bytes)

        // Next turn, bytes now present and large -> growth arm fires off the stale 0 baseline.
        var next = PinnedFloorDecider.Decide(
            afterInject.Next, new FloorSignal(30000), Default);
        Assert.Equal(FloorVerdict.Inject, next.Verdict);
    }
}
