using System;
using System.IO;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Config;

/// <summary>
/// A [regression] value that is not a number must not break config loading:
/// it is ignored and the shipped default is used, like any other invalid
/// threshold (RegressionOptions.FromConfig).
/// </summary>
public sealed class ConfigLoaderRegressionTests : IDisposable
{
    private readonly string _dir;

    public ConfigLoaderRegressionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tr-regression-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private RegressionOptions Load(string toml)
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, toml);
        return RegressionOptions.FromConfig(new ConfigLoader().LoadEffectiveConfig(path).Regression);
    }

    [Fact]
    public void NumericValues_AreUsed()
        => Assert.Equal(new RegressionOptions(0.25, 3.0, 20),
            Load("[regression]\nmiss_rate_delta = 0.25\nlatency_ratio = 3.0\nmin_events = 20\n"));

    [Fact]
    public void IntegerForADouble_IsAccepted()
        => Assert.Equal(3.0, Load("[regression]\nlatency_ratio = 3\n").LatencyRatio);

    [Fact]
    public void StringValues_AreIgnored_AndTheDefaultsApply()
        => Assert.Equal(RegressionOptions.Default,
            Load("[regression]\nmiss_rate_delta = \"high\"\nlatency_ratio = \"fast\"\nmin_events = \"ten\"\n"));

    [Fact]
    public void BooleanValues_AreIgnored_AndTheDefaultsApply()
        => Assert.Equal(RegressionOptions.Default,
            Load("[regression]\nmiss_rate_delta = true\nlatency_ratio = false\nmin_events = true\n"));

    [Fact]
    public void AnIntegerTooLargeForMinEvents_IsIgnored()
        => Assert.Equal(RegressionOptions.DefaultMinEvents, Load("[regression]\nmin_events = 3000000000\n").MinEvents);

    [Fact]
    public void AFloatForMinEvents_IsIgnored()
        => Assert.Equal(RegressionOptions.DefaultMinEvents, Load("[regression]\nmin_events = 2.5\n").MinEvents);
}
