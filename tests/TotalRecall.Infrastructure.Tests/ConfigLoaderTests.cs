using System;
using System.IO;
using Microsoft.FSharp.Core;
using Tomlyn.Model;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="ConfigLoader"/>. Verifies defaults.toml round-trips
/// into a typed <see cref="Config.TotalRecallConfig"/> and that user overrides
/// deep-merge correctly. Tests write TOML fixtures to a per-test temp dir rather
/// than relying on checked-in fixture files.
/// </summary>
public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalHome;

    public ConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalHome = Environment.GetEnvironmentVariable("TOTAL_RECALL_HOME");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", _originalHome);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", _originalDbPath);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private readonly string? _originalDbPath = Environment.GetEnvironmentVariable("TOTAL_RECALL_DB_PATH");

    // --- LoadDefaults -----------------------------------------------------

    [Fact]
    public void LoadDefaults_ReturnsExpectedValues()
    {
        var loader = new ConfigLoader();

        var cfg = loader.LoadDefaults();

        Assert.Equal(50, cfg.Tiers.Hot.MaxEntries);
        Assert.Equal(4000, cfg.Tiers.Hot.TokenBudget);
        Assert.Equal(0.7, cfg.Tiers.Hot.CarryForwardThreshold, 6);

        Assert.Equal(10000, cfg.Tiers.Warm.MaxEntries);
        Assert.Equal(5, cfg.Tiers.Warm.RetrievalTopK);
        Assert.Equal(0.65, cfg.Tiers.Warm.SimilarityThreshold, 6);
        Assert.Equal(30, cfg.Tiers.Warm.ColdDecayDays);

        Assert.Equal(512, cfg.Tiers.Cold.ChunkMaxTokens);
        Assert.Equal(50, cfg.Tiers.Cold.ChunkOverlapTokens);
        Assert.Equal(5, cfg.Tiers.Cold.LazySummaryThreshold);

        Assert.Equal(168.0, cfg.Compaction.DecayHalfLifeHours, 6);
        Assert.Equal(0.3, cfg.Compaction.WarmThreshold, 6);
        Assert.Equal(0.7, cfg.Compaction.PromoteThreshold, 6);
        Assert.Equal(7, cfg.Compaction.WarmSweepIntervalDays);

        Assert.Equal("all-MiniLM-L6-v2", cfg.Embedding.Model);
        Assert.Equal(384, cfg.Embedding.Dimensions);

        Assert.True(FSharpOption<Core.Config.RegressionConfig>.get_IsSome(cfg.Regression));
        var reg = cfg.Regression.Value;
        Assert.True(FSharpOption<double>.get_IsSome(reg.MissRateDelta));
        Assert.Equal(0.1, reg.MissRateDelta.Value, 6);
        Assert.True(FSharpOption<double>.get_IsSome(reg.LatencyRatio));
        Assert.Equal(2.0, reg.LatencyRatio.Value, 6);
        Assert.True(FSharpOption<int>.get_IsSome(reg.MinEvents));
        Assert.Equal(10, reg.MinEvents.Value);

        Assert.True(FSharpOption<Core.Config.SearchConfig>.get_IsSome(cfg.Search));
        var search = cfg.Search.Value;
        Assert.True(FSharpOption<double>.get_IsSome(search.FtsWeight));
        Assert.Equal(0.3, search.FtsWeight.Value, 6);
    }

    // --- LoadEffectiveConfig ----------------------------------------------

    [Fact]
    public void LoadEffectiveConfig_NoUserPath_NoHomeConfig_ReturnsDefaults()
    {
        // Point TOTAL_RECALL_HOME at an empty temp dir so the default fallback
        // path (~/.total-recall/config.toml) cannot resolve to an existing file.
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", _tempDir);
        var loader = new ConfigLoader();

        var cfg = loader.LoadEffectiveConfig(userConfigPath: null);

        Assert.Equal(50, cfg.Tiers.Hot.MaxEntries);
        Assert.Equal("all-MiniLM-L6-v2", cfg.Embedding.Model);
    }

    [Fact]
    public void LoadEffectiveConfig_UserPathDoesntExist_ReturnsDefaults()
    {
        var loader = new ConfigLoader();
        var missingPath = Path.Combine(_tempDir, "does-not-exist.toml");

        var cfg = loader.LoadEffectiveConfig(missingPath);

        Assert.Equal(50, cfg.Tiers.Hot.MaxEntries);
        Assert.Equal(4000, cfg.Tiers.Hot.TokenBudget);
    }

    [Fact]
    public void LoadEffectiveConfig_UserOverridesScalar_ScalarReplaced()
    {
        var userPath = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(userPath, """
            [tiers.hot]
            max_entries = 999
            """);
        var loader = new ConfigLoader();

        var cfg = loader.LoadEffectiveConfig(userPath);

        Assert.Equal(999, cfg.Tiers.Hot.MaxEntries);
        // Siblings preserved from defaults.
        Assert.Equal(4000, cfg.Tiers.Hot.TokenBudget);
        Assert.Equal(0.7, cfg.Tiers.Hot.CarryForwardThreshold, 6);
        // Other sections untouched.
        Assert.Equal(10000, cfg.Tiers.Warm.MaxEntries);
        Assert.Equal("all-MiniLM-L6-v2", cfg.Embedding.Model);
    }

    [Fact]
    public void LoadEffectiveConfig_UserOverridesNestedField_PreservesSiblings()
    {
        var userPath = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(userPath, """
            [tiers.warm]
            similarity_threshold = 0.99
            """);
        var loader = new ConfigLoader();

        var cfg = loader.LoadEffectiveConfig(userPath);

        Assert.Equal(0.99, cfg.Tiers.Warm.SimilarityThreshold, 6);
        // Sibling Warm fields preserved.
        Assert.Equal(10000, cfg.Tiers.Warm.MaxEntries);
        Assert.Equal(5, cfg.Tiers.Warm.RetrievalTopK);
        Assert.Equal(30, cfg.Tiers.Warm.ColdDecayDays);
        // Hot untouched.
        Assert.Equal(50, cfg.Tiers.Hot.MaxEntries);
    }

    [Fact]
    public void LoadEffectiveConfig_MalformedToml_ThrowsInvalidData()
    {
        var userPath = Path.Combine(_tempDir, "bad.toml");
        File.WriteAllText(userPath, "not = a [valid\ntoml = =");
        var loader = new ConfigLoader();

        var ex = Assert.Throws<InvalidDataException>(() => loader.LoadEffectiveConfig(userPath));
        Assert.Contains("bad.toml", ex.Message);
    }

    [Fact]
    public void LoadEffectiveConfig_UnsafeKey_Skipped()
    {
        // Unsafe top-level keys from the user config must be dropped by the
        // MergeTables safe-key filter (mirrors src-ts/config.ts isSafeKey).
        var userPath = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(userPath, """
            __proto__ = "evil"
            [tiers.hot]
            max_entries = 123
            """);
        var loader = new ConfigLoader();

        // Must not throw, and the safe portion of the merge must still apply.
        var cfg = loader.LoadEffectiveConfig(userPath);
        Assert.Equal(123, cfg.Tiers.Hot.MaxEntries);
    }

    // --- LoadEffectiveTable -----------------------------------------------

    [Fact]
    public void LoadEffectiveTable_ReturnsMergedTable()
    {
        var userPath = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(userPath, """
            [tiers.hot]
            max_entries = 777

            [custom]
            unknown_key = "hello"
            """);
        var loader = new ConfigLoader();

        var table = loader.LoadEffectiveTable(userPath);

        // Defaults merged.
        var tiers = Assert.IsType<TomlTable>(table["tiers"]);
        var hot = Assert.IsType<TomlTable>(tiers["hot"]);
        Assert.Equal(777L, Convert.ToInt64(hot["max_entries"]));
        // Sibling from defaults preserved.
        Assert.Equal(4000L, Convert.ToInt64(hot["token_budget"]));
        var embedding = Assert.IsType<TomlTable>(table["embedding"]);
        Assert.Equal("all-MiniLM-L6-v2", embedding["model"]);
        // Custom (non-schema) keys survive — this is the main reason
        // LoadEffectiveTable exists alongside LoadEffectiveConfig.
        var custom = Assert.IsType<TomlTable>(table["custom"]);
        Assert.Equal("hello", custom["unknown_key"]);
    }

    // --- GetDataDir -------------------------------------------------------

    [Fact]
    public void GetDataDir_TotalRecallHomeSet_ReturnsIt()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", _tempDir);

        var result = ConfigLoader.GetDataDir();

        Assert.Equal(_tempDir, result);
    }

    [Fact]
    public void GetDataDir_TotalRecallHomeUnset_ReturnsHomeDotTotalRecall()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", null);
        // Mirror the fallback chain in the impl: HOME, then USERPROFILE
        // (Windows), then literal "~" as a last resort. Without this the
        // test was Linux-only — Windows always falls through to USERPROFILE.
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? "~";

        var result = ConfigLoader.GetDataDir();

        Assert.Equal(Path.Combine(home, ".total-recall"), result);
    }

    // --- GetDbPath --------------------------------------------------------

    [Fact]
    public void GetDbPath_Unset_DefaultsToDataDirTotalRecallDb()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", _tempDir);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", null);

        var result = ConfigLoader.GetDbPath();

        Assert.Equal(Path.Combine(_tempDir, "total-recall.db"), result);
    }

    [Fact]
    public void GetDbPath_Empty_DefaultsToDataDirTotalRecallDb()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_HOME", _tempDir);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", "   ");

        var result = ConfigLoader.GetDbPath();

        Assert.Equal(Path.Combine(_tempDir, "total-recall.db"), result);
    }

    [Fact]
    public void GetDbPath_AbsolutePath_ReturnsVerbatim()
    {
        var customPath = Path.Combine(_tempDir, "custom", "memories.db");
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", customPath);

        var result = ConfigLoader.GetDbPath();

        Assert.Equal(customPath, result);
    }

    [Fact]
    public void GetDbPath_TildeSlashPrefix_Expanded()
    {
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE")
            ?? string.Empty;
        if (string.IsNullOrEmpty(home))
        {
            return; // no HOME → skip; behavior unspecified
        }

        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", "~/tr-test/memories.db");

        var result = ConfigLoader.GetDbPath();

        Assert.Equal(Path.Combine(home, "tr-test", "memories.db"), result);
    }

    [Fact]
    public void GetDbPath_BareTilde_Throws()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", "~");

        var ex = Assert.Throws<SqliteDbPathException>(() => ConfigLoader.GetDbPath());
        Assert.Contains("\"~\"", ex.Message);
    }

    [Fact]
    public void GetDbPath_TrailingForwardSlash_Throws()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", "/some/dir/");

        var ex = Assert.Throws<SqliteDbPathException>(() => ConfigLoader.GetDbPath());
        Assert.Contains("file path, not a directory", ex.Message);
    }

    [Fact]
    public void GetDbPath_TrailingBackslash_Throws()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", "C:\\tmp\\");

        var ex = Assert.Throws<SqliteDbPathException>(() => ConfigLoader.GetDbPath());
        Assert.Contains("file path, not a directory", ex.Message);
    }

    [Fact]
    public void GetDbPath_RelativePath_Throws()
    {
        Environment.SetEnvironmentVariable("TOTAL_RECALL_DB_PATH", "relative/path.db");

        var ex = Assert.Throws<SqliteDbPathException>(() => ConfigLoader.GetDbPath());
        Assert.Contains("absolute or start with ~/", ex.Message);
    }

    // --- New projection tests -----------------------------------------------

    private const string MinimalValidToml = """
        [tiers.hot]
        max_entries = 50
        token_budget = 4000
        carry_forward_threshold = 0.7

        [tiers.warm]
        max_entries = 10000
        retrieval_top_k = 5
        similarity_threshold = 0.65
        cold_decay_days = 30

        [tiers.cold]
        chunk_max_tokens = 512
        chunk_overlap_tokens = 50
        lazy_summary_threshold = 5

        [compaction]
        decay_half_life_hours = 168
        warm_threshold = 0.3
        promote_threshold = 0.7
        warm_sweep_interval_days = 7

        [embedding]
        model = "all-MiniLM-L6-v2"
        dimensions = 384
        """;

    [Fact]
    public void LoadEffectiveConfig_WithStorageSection_ParsesConnectionString()
    {
        var userPath = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(userPath, """
            [storage]
            connection_string = "Server=localhost;Database=tr"
            """);
        var loader = new ConfigLoader();

        var cfg = loader.LoadEffectiveConfig(userPath);

        Assert.True(FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage));
        var storage = cfg.Storage.Value;
        Assert.True(FSharpOption<string>.get_IsSome(storage.ConnectionString));
        Assert.Equal("Server=localhost;Database=tr", storage.ConnectionString.Value);
    }

    [Fact]
    public void LoadEffectiveConfig_WithEmbeddingProvider_ParsesRemoteFields()
    {
        var userPath = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(userPath, """
            [embedding]
            provider = "bedrock"
            bedrock_region = "us-east-1"
            bedrock_model = "amazon.titan-embed-text-v2:0"
            """);
        var loader = new ConfigLoader();

        var cfg = loader.LoadEffectiveConfig(userPath);

        Assert.True(FSharpOption<string>.get_IsSome(cfg.Embedding.Provider));
        Assert.Equal("bedrock", cfg.Embedding.Provider.Value);
        Assert.True(FSharpOption<string>.get_IsSome(cfg.Embedding.BedrockRegion));
        Assert.Equal("us-east-1", cfg.Embedding.BedrockRegion.Value);
        Assert.True(FSharpOption<string>.get_IsSome(cfg.Embedding.BedrockModel));
        Assert.Equal("amazon.titan-embed-text-v2:0", cfg.Embedding.BedrockModel.Value);
    }

    [Fact]
    public void LoadDefaults_StorageAndUserAreNone()
    {
        var loader = new ConfigLoader();

        var cfg = loader.LoadDefaults();

        Assert.True(FSharpOption<Core.Config.StorageConfig>.get_IsNone(cfg.Storage));
        Assert.True(FSharpOption<Core.Config.UserConfig>.get_IsNone(cfg.User));
        Assert.True(FSharpOption<string>.get_IsNone(cfg.Embedding.Provider));
    }
}
