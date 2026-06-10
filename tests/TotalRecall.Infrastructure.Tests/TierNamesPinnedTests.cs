using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

public class TierNamesPinnedTests
{
    [Fact]
    public void TierName_Pinned_IsPinnedNotCold() =>
        Assert.Equal("pinned", TierNames.TierName(Tier.Pinned));

    [Fact]
    public void ParseTier_Pinned_RoundTrips() =>
        Assert.Equal(Tier.Pinned, TierNames.ParseTier("pinned"));

    [Fact]
    public void WarmthRank_Pinned_IsAboveHot() =>
        Assert.True(TierNames.WarmthRank(Tier.Pinned) > TierNames.WarmthRank(Tier.Hot));

    [Fact]
    public void AllTablePairs_IncludesPinnedPairs()
    {
        Assert.Contains((Tier.Pinned, ContentType.Memory), TierNames.AllTablePairs);
        Assert.Contains((Tier.Pinned, ContentType.Knowledge), TierNames.AllTablePairs);
    }
}
