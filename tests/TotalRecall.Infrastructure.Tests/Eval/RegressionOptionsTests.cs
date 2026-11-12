using Microsoft.FSharp.Core;
using CoreConfig = TotalRecall.Core.Config;
using TotalRecall.Infrastructure.Eval;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

public sealed class RegressionOptionsTests
{
    private static FSharpOption<CoreConfig.RegressionConfig> Cfg(double? delta, double? ratio, int? min)
        => FSharpOption<CoreConfig.RegressionConfig>.Some(new CoreConfig.RegressionConfig(
            delta is double d ? FSharpOption<double>.Some(d) : FSharpOption<double>.None,
            ratio is double r ? FSharpOption<double>.Some(r) : FSharpOption<double>.None,
            min is int m ? FSharpOption<int>.Some(m) : FSharpOption<int>.None));

    [Fact]
    public void FromConfig_NoRegressionSection_UsesTheDefaults()
    {
        var o = RegressionOptions.FromConfig(FSharpOption<CoreConfig.RegressionConfig>.None);
        Assert.Equal(0.1, o.MissRateDelta);
        Assert.Equal(2.0, o.LatencyRatio);
        Assert.Equal(10, o.MinEvents);
    }

    [Fact]
    public void FromConfig_NullOption_UsesTheDefaults()
    {
        var o = RegressionOptions.FromConfig(null);
        Assert.Equal(RegressionOptions.Default, o);
    }

    [Fact]
    public void FromConfig_PartialSection_FillsOnlyTheMissingValues()
    {
        var o = RegressionOptions.FromConfig(Cfg(0.25, null, null));
        Assert.Equal(0.25, o.MissRateDelta);
        Assert.Equal(2.0, o.LatencyRatio);
        Assert.Equal(10, o.MinEvents);
    }

    [Fact]
    public void FromConfig_SetValues_AreUsed()
    {
        var o = RegressionOptions.FromConfig(Cfg(0.3, 1.5, 20));
        Assert.Equal(0.3, o.MissRateDelta);
        Assert.Equal(1.5, o.LatencyRatio);
        Assert.Equal(20, o.MinEvents);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FromConfig_InvalidDelta_FallsBackToTheDefault(double bad)
        => Assert.Equal(0.1, RegressionOptions.FromConfig(Cfg(bad, null, null)).MissRateDelta);

    [Theory]
    [InlineData(0.99)]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FromConfig_InvalidRatio_FallsBackToTheDefault(double bad)
        => Assert.Equal(2.0, RegressionOptions.FromConfig(Cfg(null, bad, null)).LatencyRatio);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void FromConfig_InvalidMinEvents_FallsBackToTheDefault(int bad)
        => Assert.Equal(10, RegressionOptions.FromConfig(Cfg(null, null, bad)).MinEvents);

    [Fact]
    public void FromConfig_RatioOfExactlyOne_IsValid()
        => Assert.Equal(1.0, RegressionOptions.FromConfig(Cfg(null, 1.0, null)).LatencyRatio);
}
