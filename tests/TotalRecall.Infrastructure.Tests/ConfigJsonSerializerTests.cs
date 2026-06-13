using System;
using System.IO;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="ConfigJsonSerializer"/>, focused on the optional
/// embedding keys. The serializer feeds ConfigSnapshotStore's byte-equal dedup,
/// so an optional key must be emitted ONLY when present — a stray or mis-keyed
/// field would silently break snapshot dedup. Mirrors the temp-TOML harness in
/// <see cref="ConfigLoaderTests"/>.
/// </summary>
public sealed class ConfigJsonSerializerTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigJsonSerializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-cfgjson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private Core.Config.TotalRecallConfig Load(string toml)
    {
        var path = Path.Combine(_tempDir, "config.toml");
        File.WriteAllText(path, toml);
        return new ConfigLoader().LoadEffectiveConfig(path);
    }

    [Fact]
    public void Serialize_WithOnModelChange_EmitsKey()
    {
        var cfg = Load("[embedding]\non_model_change = \"warn\"\n");

        var json = ConfigJsonSerializer.Serialize(cfg);

        Assert.Contains("\"on_model_change\":\"warn\"", json);
    }

    [Fact]
    public void Serialize_WithoutOnModelChange_OmitsKey()
    {
        var cfg = Load("[embedding]\nprovider = \"local\"\n");

        var json = ConfigJsonSerializer.Serialize(cfg);

        Assert.DoesNotContain("on_model_change", json);
    }

    [Fact]
    public void Serialize_DefaultEmbedding_HasNoOptionalKeys()
    {
        // A default config carries neither query_prefix nor on_model_change, so the
        // embedding block must serialize as exactly model + dimensions — byte-identical
        // with the pre-field era for ConfigSnapshotStore dedup.
        var cfg = new ConfigLoader().LoadDefaults();

        var json = ConfigJsonSerializer.Serialize(cfg);

        Assert.Contains("\"embedding\":{\"model\":\"bge-small-en-v1.5\",\"dimensions\":384}", json);
        Assert.DoesNotContain("on_model_change", json);
    }
}
