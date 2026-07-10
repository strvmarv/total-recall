using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Tier model v2 (Task 9): the <c>Tier.Pinned</c> DU case is retired. "pinned"
/// is no longer a tier — it is the sticky flag on hot. These tests assert the
/// parse/format helpers no longer round-trip "pinned" and that the sweep table
/// no longer advertises pinned pairs.
/// </summary>
public class TierNamesPinnedTests
{
    [Fact]
    public void ParseTier_PinnedIsUnknown()
        => Assert.Null(TierNames.ParseTier("pinned"));

    [Fact]
    public void AllTablePairs_ExcludesPinnedPairs()
    {
        // Only the 6 live (tier, type) pairs remain — no pinned pairs.
        Assert.Equal(6, TierNames.AllTablePairs.Length);
        Assert.All(TierNames.AllTablePairs, p =>
            Assert.True(p.Tier.IsHot || p.Tier.IsWarm || p.Tier.IsCold));
    }

    [Fact]
    public void WarmthRank_HotIsTop()
    {
        Assert.Equal(2, TierNames.WarmthRank(Tier.Hot));
        Assert.Equal(1, TierNames.WarmthRank(Tier.Warm));
        Assert.Equal(0, TierNames.WarmthRank(Tier.Cold));
    }
}
