using System;
using System.IO;
using Tomlyn;
using Tomlyn.Model;
using TotalRecall.Infrastructure.Config;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Config;

/// <summary>
/// Unit tests for <see cref="ConfigWriter"/>. Exercises <c>SetNestedKey</c>
/// mutation semantics and <c>SaveUserOverride</c> file I/O against a temp
/// directory per test. Mirrors the TS semantics in
/// <c>src-ts/config.ts</c> (setNestedKey, saveUserConfig).
/// </summary>
public sealed class ConfigWriterTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-cfgwr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // --- SetNestedKey -----------------------------------------------------

    [Fact]
    public void SetNestedKey_SetsTopLevel()
    {
        var t = new TomlTable();
        ConfigWriter.SetNestedKey(t, "foo", "bar");
        Assert.Equal("bar", t["foo"]);
    }

    [Fact]
    public void SetNestedKey_SetsNested()
    {
        var t = new TomlTable();
        ConfigWriter.SetNestedKey(t, "a.b.c", 42L);
        var a = Assert.IsType<TomlTable>(t["a"]);
        var b = Assert.IsType<TomlTable>(a["b"]);
        Assert.Equal(42L, b["c"]);
    }

    [Fact]
    public void SetNestedKey_PreservesSiblings()
    {
        var t = new TomlTable();
        var a = new TomlTable { ["b"] = "x" };
        t["a"] = a;
        ConfigWriter.SetNestedKey(t, "a.c", "y");
        var updated = Assert.IsType<TomlTable>(t["a"]);
        Assert.Equal("x", updated["b"]);
        Assert.Equal("y", updated["c"]);
    }

    [Theory]
    [InlineData("__proto__")]
    [InlineData("constructor")]
    [InlineData("prototype")]
    public void SetNestedKey_RejectsUnsafeFinalKey(string bad)
    {
        var t = new TomlTable();
        Assert.Throws<ArgumentException>(() =>
            ConfigWriter.SetNestedKey(t, bad, 1L));
    }

    [Theory]
    [InlineData("__proto__.x")]
    [InlineData("a.constructor.y")]
    [InlineData("a.prototype")]
    public void SetNestedKey_RejectsUnsafeIntermediate(string bad)
    {
        var t = new TomlTable();
        Assert.Throws<ArgumentException>(() =>
            ConfigWriter.SetNestedKey(t, bad, 1L));
    }

    [Fact]
    public void SetNestedKey_OverwritesScalarWithTable()
    {
        var t = new TomlTable { ["a"] = "was-scalar" };
        ConfigWriter.SetNestedKey(t, "a.b", 1L);
        var sub = Assert.IsType<TomlTable>(t["a"]);
        Assert.Equal(1L, sub["b"]);
    }

    // --- SaveUserOverride -------------------------------------------------

    [Fact]
    public void SaveUserOverride_CreatesNewFile()
    {
        var path = Path.Combine(_tempDir, "nested", "config.toml");
        ConfigWriter.SaveUserOverride(path, "tiers.warm.similarity_threshold", 0.99);

        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        var doc = Toml.Parse(text);
        Assert.False(doc.HasErrors);
        var model = doc.ToModel();
        var tiers = Assert.IsType<TomlTable>(model["tiers"]);
        var warm = Assert.IsType<TomlTable>(tiers["warm"]);
        Assert.Equal(0.99, Convert.ToDouble(warm["similarity_threshold"]));
    }

    [Fact]
    public void SaveUserOverride_MergesOverExisting()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(path, """
            [tiers.hot]
            max_entries = 123
            """);

        ConfigWriter.SaveUserOverride(path, "tiers.warm.similarity_threshold", 0.5);

        var model = Toml.Parse(File.ReadAllText(path)).ToModel();
        var tiers = Assert.IsType<TomlTable>(model["tiers"]);
        var hot = Assert.IsType<TomlTable>(tiers["hot"]);
        var warm = Assert.IsType<TomlTable>(tiers["warm"]);
        Assert.Equal(123L, Convert.ToInt64(hot["max_entries"]));
        Assert.Equal(0.5, Convert.ToDouble(warm["similarity_threshold"]));
    }

    [Fact]
    public void SaveUserOverride_CreatesParentDirectory()
    {
        var parent = Path.Combine(_tempDir, "does", "not", "exist");
        var path = Path.Combine(parent, "config.toml");

        ConfigWriter.SaveUserOverride(path, "foo.bar", "baz");

        Assert.True(Directory.Exists(parent));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SaveUserOverride_RoundTripsBool()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        ConfigWriter.SaveUserOverride(path, "feature.enabled", true);
        var model = Toml.Parse(File.ReadAllText(path)).ToModel();
        var feature = Assert.IsType<TomlTable>(model["feature"]);
        Assert.Equal(true, feature["enabled"]);
    }

    [Fact]
    public void SaveUserOverride_RoundTripsLong()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        ConfigWriter.SaveUserOverride(path, "x.y", 42L);
        var model = Toml.Parse(File.ReadAllText(path)).ToModel();
        var x = Assert.IsType<TomlTable>(model["x"]);
        Assert.Equal(42L, Convert.ToInt64(x["y"]));
    }

    [Fact]
    public void SaveUserOverride_RoundTripsInteger_DoubleForcesDecimalPoint()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        ConfigWriter.SaveUserOverride(path, "x.y", 3.0);
        var text = File.ReadAllText(path);
        // The writer must force ".0" so the value parses back as a float.
        Assert.Contains("3.0", text);
        var model = Toml.Parse(text).ToModel();
        var x = Assert.IsType<TomlTable>(model["x"]);
        Assert.Equal(3.0, Convert.ToDouble(x["y"]));
    }

    // --- GetNestedValue ---------------------------------------------------

    [Fact]
    public void GetNestedValue_WalksNestedTable()
    {
        var t = new TomlTable();
        ConfigWriter.SetNestedKey(t, "a.b.c", "leaf");
        Assert.Equal("leaf", ConfigWriter.GetNestedValue(t, "a.b.c"));
    }

    [Fact]
    public void GetNestedValue_MissingKey_ReturnsNull()
    {
        var t = new TomlTable();
        ConfigWriter.SetNestedKey(t, "a.b", "leaf");
        Assert.Null(ConfigWriter.GetNestedValue(t, "a.c"));
        Assert.Null(ConfigWriter.GetNestedValue(t, "nope"));
    }
}
