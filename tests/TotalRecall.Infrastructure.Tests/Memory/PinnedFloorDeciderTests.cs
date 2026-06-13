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
}
