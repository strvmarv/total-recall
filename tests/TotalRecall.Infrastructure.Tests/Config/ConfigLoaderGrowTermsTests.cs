using System;
using System.IO;
using TotalRecall.Infrastructure.Config;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Config;

/// <summary>
/// eval.grow.sensitive_terms is the caller-supplied internal-term denylist for
/// the grow accept guard. It must default to empty (no internal names baked into
/// the shipped defaults) and read a user-supplied array when present.
/// </summary>
public sealed class ConfigLoaderGrowTermsTests : IDisposable
{
    private readonly string _dir;

    public ConfigLoaderGrowTermsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tr-growterms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void LoadGrowSensitiveTerms_DefaultsToEmpty_WhenSectionAbsent()
    {
        var cfg = Path.Combine(_dir, "config.toml");
        File.WriteAllText(cfg, "[tiers.hot]\nmax_entries = 50\n");
        Assert.Empty(ConfigLoader.LoadGrowSensitiveTerms(cfg));
    }

    [Fact]
    public void LoadGrowSensitiveTerms_ReadsUserSuppliedArray()
    {
        var cfg = Path.Combine(_dir, "config.toml");
        File.WriteAllText(cfg, "[eval.grow]\nsensitive_terms = [\"alpha\", \"beta-gamma\"]\n");
        var terms = ConfigLoader.LoadGrowSensitiveTerms(cfg);
        Assert.Equal(new[] { "alpha", "beta-gamma" }, terms);
    }
}
