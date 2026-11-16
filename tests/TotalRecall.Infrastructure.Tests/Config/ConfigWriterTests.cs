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
/// directory per test.
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

    // --- TOML array serialization -----------------------------------------

    [Fact]
    public void SerializeTomlTable_StringArray_RoundTrips()
    {
        var t = new TomlTable();
        var skills = new TomlTable();
        var dirs = new TomlArray { "a/b", "c\\d" };
        skills["extra_dirs"] = dirs;
        t["skills"] = skills;

        var serialized = ConfigWriter.SerializeTomlTable(t);

        var model = Toml.Parse(serialized).ToModel();
        var skillsBack = Assert.IsType<TomlTable>(model["skills"]);
        var arr = Assert.IsType<TomlArray>(skillsBack["extra_dirs"]);
        Assert.Equal(2, arr.Count);
        Assert.Equal("a/b", arr[0]);
        Assert.Equal("c\\d", arr[1]);
    }

    [Fact]
    public void SaveUserOverride_SetsScalar_PreservesExistingArray()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        // Mirror a real user config: a [skills] section that already holds an array.
        File.WriteAllText(path, """
            [skills]
            extra_dirs = [ "C:\\Users\\me\\.total-recall\\skills" ]
            """);

        // This used to throw NotSupportedException because re-serializing the whole
        // config hit the TomlArray during the write.
        ConfigWriter.SaveUserOverride(path, "tiers.warm.similarity_threshold", 0.42);

        var text = File.ReadAllText(path);
        var model = Toml.Parse(text).ToModel();

        // New scalar landed.
        var tiers = Assert.IsType<TomlTable>(model["tiers"]);
        var warm = Assert.IsType<TomlTable>(tiers["warm"]);
        Assert.Equal(0.42, Convert.ToDouble(warm["similarity_threshold"]));

        // Pre-existing array preserved.
        var skills = Assert.IsType<TomlTable>(model["skills"]);
        var arr = Assert.IsType<TomlArray>(skills["extra_dirs"]);
        var only = Assert.Single(arr);
        Assert.Equal("C:\\Users\\me\\.total-recall\\skills", only);
    }

    [Fact]
    public void SerializeTomlTable_EmptyArray_EmitsBrackets()
    {
        var t = new TomlTable { ["xs"] = new TomlArray() };
        var serialized = ConfigWriter.SerializeTomlTable(t);
        Assert.Contains("xs = []", serialized);

        var model = Toml.Parse(serialized).ToModel();
        var arr = Assert.IsType<TomlArray>(model["xs"]);
        Assert.Empty(arr);
    }

    [Fact]
    public void SerializeTomlTable_IntAndDoubleArrays_RoundTrip()
    {
        var t = new TomlTable
        {
            ["ints"] = new TomlArray { 1L, 2L, 3L },
            ["doubles"] = new TomlArray { 1.5, 2.0 },
        };

        var serialized = ConfigWriter.SerializeTomlTable(t);
        var model = Toml.Parse(serialized).ToModel();

        var ints = Assert.IsType<TomlArray>(model["ints"]);
        Assert.Equal(new long[] { 1L, 2L, 3L }, new[] { Convert.ToInt64(ints[0]), Convert.ToInt64(ints[1]), Convert.ToInt64(ints[2]) });

        var doubles = Assert.IsType<TomlArray>(model["doubles"]);
        Assert.Equal(1.5, Convert.ToDouble(doubles[0]));
        Assert.Equal(2.0, Convert.ToDouble(doubles[1]));
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

    // --- RenderUserOverrides ----------------------------------------------

    private static readonly (string, object)[] ThreeKeys =
    [
        ("storage.mode", "git"),
        ("git_sync.remote", "git@host:o/r.git"),
        ("git_sync.branch", "main"),
    ];

    [Fact]
    public void RenderUserOverrides_OnAMissingFile_EqualsRepeatedSaveUserOverride_AndCreatesNothing()
    {
        var dir = Path.Combine(_tempDir, "not-yet");
        var path = Path.Combine(dir, "config.toml");

        var rendered = ConfigWriter.RenderUserOverrides(path, ThreeKeys);

        Assert.False(Directory.Exists(dir));
        Assert.False(File.Exists(path));
        var saved = Path.Combine(_tempDir, "saved", "config.toml");
        foreach (var (key, value) in ThreeKeys)
            ConfigWriter.SaveUserOverride(saved, key, value);
        Assert.Equal(File.ReadAllText(saved), rendered);
    }

    [Fact]
    public void RenderUserOverrides_KeepsUnrelatedSections_AndReplacesAKeyInAnExistingTable()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(path, "[tiers.hot]\nmax_entries = 7\n\n[storage]\nmode = \"local\"\nconnection_string = \"x\"\n");
        var before = File.ReadAllText(path);

        var rendered = ConfigWriter.RenderUserOverrides(path, ThreeKeys);

        Assert.Equal(before, File.ReadAllText(path));
        var model = Toml.ToModel(rendered);
        Assert.Equal(7L, ((TomlTable)((TomlTable)model["tiers"])["hot"])["max_entries"]);
        var storage = (TomlTable)model["storage"];
        Assert.Equal("git", storage["mode"]);
        Assert.Equal("x", storage["connection_string"]);
        var sync = (TomlTable)model["git_sync"];
        Assert.Equal("git@host:o/r.git", sync["remote"]);
        Assert.Equal("main", sync["branch"]);
    }

    [Fact]
    public void RenderUserOverrides_MalformedToml_Throws()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(path, "[storage\nmode = ");
        Assert.Throws<InvalidDataException>(() => ConfigWriter.RenderUserOverrides(path, ThreeKeys));
    }

    [Fact]
    public void SaveUserOverride_StillWritesTheSameBytes()
    {
        // golden strings of the writer: a refactor that extracts the rendering must not change a byte
        var cases = new (string Existing, string Key, object Value, string Expected)[]
        {
            ("", "a.b", "x", "[a]\nb = \"x\"\n"),
            ("", "top", 5L, "top = 5\n"),
            ("", "flag", true, "flag = true\n"),
            ("[a]\nb = \"old\"\nc = 1\n", "a.b", "new", "[a]\nb = \"new\"\nc = 1\n"),
        };
        foreach (var (existing, key, value, expected) in cases)
        {
            var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"), "config.toml");
            if (existing.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, existing);
            }
            ConfigWriter.SaveUserOverride(path, key, value);
            Assert.Equal(expected, File.ReadAllText(path));
        }
    }
}
