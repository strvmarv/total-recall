using System;
using System.IO;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Config;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Config;

/// <summary>
/// Tests for cortex/mode parsing in <see cref="ConfigLoader"/>.
/// Each test writes a minimal TOML file then loads via
/// <see cref="IConfigLoader.LoadEffectiveConfig"/> to verify the
/// projected F# record.
/// </summary>
public sealed class ConfigLoaderCortexTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigLoaderCortexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-cortex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.toml");
    }

    public void Dispose()
    {
        // Restore env vars
        Environment.SetEnvironmentVariable("TOTAL_RECALL_CORTEX_URL", null);
        Environment.SetEnvironmentVariable("TOTAL_RECALL_CORTEX_PAT", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void DefaultMode_IsLocal_WhenNoModeSpecified()
    {
        // No user config — defaults only. Storage section is commented out in
        // defaults.toml, so Storage should be None (no mode field).
        var loader = new ConfigLoader();
        var cfg = loader.LoadDefaults();

        // defaults.toml has [storage] commented out, so Storage is None.
        // When Storage is None, the effective mode is implicitly "local".
        if (FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage))
        {
            // If storage is present, mode should be None (defaulting to local)
            Assert.True(
                FSharpOption<string>.get_IsNone(cfg.Storage.Value.Mode),
                "Default mode should be None (implying local)");
        }
        else
        {
            // Storage being None also implies local mode
            Assert.True(true);
        }
    }

    [Fact]
    public void ModeCortex_WithCortexSection_ParsesCorrectly()
    {
        var toml = """
            [storage]
            mode = "cortex"

            [cortex]
            url = "https://cortex.example.com"
            pat = "tr_test123"
            """;
        File.WriteAllText(_configPath, toml);

        var loader = new ConfigLoader();
        var cfg = loader.LoadEffectiveConfig(_configPath);

        // Storage mode
        Assert.True(FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage));
        var storage = cfg.Storage.Value;
        Assert.True(FSharpOption<string>.get_IsSome(storage.Mode));
        Assert.Equal("cortex", storage.Mode.Value);

        // Cortex config
        Assert.True(FSharpOption<Core.Config.CortexConfig>.get_IsSome(cfg.Cortex));
        var cortex = cfg.Cortex.Value;
        Assert.Equal("https://cortex.example.com", cortex.Url);
        Assert.Equal("tr_test123", cortex.Pat);
    }

    [Fact]
    public void ModePostgres_ParsesCorrectly()
    {
        var toml = """
            [storage]
            mode = "postgres"
            connection_string = "Host=localhost;Database=total_recall"
            """;
        File.WriteAllText(_configPath, toml);

        var loader = new ConfigLoader();
        var cfg = loader.LoadEffectiveConfig(_configPath);

        Assert.True(FSharpOption<Core.Config.StorageConfig>.get_IsSome(cfg.Storage));
        var storage = cfg.Storage.Value;
        Assert.True(FSharpOption<string>.get_IsSome(storage.Mode));
        Assert.Equal("postgres", storage.Mode.Value);
        Assert.True(FSharpOption<string>.get_IsSome(storage.ConnectionString));
        Assert.Equal("Host=localhost;Database=total_recall", storage.ConnectionString.Value);

        // No cortex section → Cortex should be None
        Assert.True(FSharpOption<Core.Config.CortexConfig>.get_IsNone(cfg.Cortex));
    }

    [Fact]
    public void CortexEnvVars_OverrideToml()
    {
        var toml = """
            [cortex]
            url = "https://toml.example.com"
            pat = "tr_toml"
            """;
        File.WriteAllText(_configPath, toml);

        Environment.SetEnvironmentVariable("TOTAL_RECALL_CORTEX_URL", "https://env.example.com");
        Environment.SetEnvironmentVariable("TOTAL_RECALL_CORTEX_PAT", "tr_env");

        var loader = new ConfigLoader();
        var cfg = loader.LoadEffectiveConfig(_configPath);

        Assert.True(FSharpOption<Core.Config.CortexConfig>.get_IsSome(cfg.Cortex));
        var cortex = cfg.Cortex.Value;
        Assert.Equal("https://env.example.com", cortex.Url);
        Assert.Equal("tr_env", cortex.Pat);
    }
}
