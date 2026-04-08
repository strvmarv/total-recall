using System;
using System.IO;
using System.Linq;
using TotalRecall.Infrastructure.Embedding;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="OnnxEmbedder"/>. These tests load the
/// real bundled <c>models/all-MiniLM-L6-v2/</c> model and run a full ONNX
/// inference, so they are slow (first call ~500ms, subsequent calls ~10-50ms).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OnnxEmbedderIntegrationTests : IDisposable
{
    private const string ModelName = "all-MiniLM-L6-v2";
    private const int ExpectedDimensions = 384;

    private readonly string _tempUserDir;
    private readonly string _bundledModelsDir;
    private readonly ModelRegistry _registry;

    public OnnxEmbedderIntegrationTests()
    {
        var repoRoot = FindRepoRoot();
        _bundledModelsDir = Path.Combine(repoRoot, "models");
        var registryPath = Path.Combine(_bundledModelsDir, "registry.json");
        _registry = ModelRegistry.LoadFromFile(registryPath);

        _tempUserDir = Path.Combine(Path.GetTempPath(), "tr-embed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempUserDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempUserDir))
            {
                Directory.Delete(_tempUserDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "models", "registry.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repository root (models/registry.json) by walking up from "
            + AppContext.BaseDirectory);
    }

    private OnnxEmbedder NewEmbedder()
    {
        var manager = new ModelManager(_registry, _bundledModelsDir, _tempUserDir);
        return new OnnxEmbedder(manager, ModelName);
    }

    [Fact]
    public void Embed_HelloWorld_Returns384DimVector()
    {
        using var embedder = NewEmbedder();
        var vec = embedder.Embed("hello world");
        Assert.Equal(ExpectedDimensions, vec.Length);
    }

    [Fact]
    public void Embed_ProducesL2NormalizedVector()
    {
        using var embedder = NewEmbedder();
        var vec = embedder.Embed("the quick brown fox jumps over the lazy dog");
        double sumSq = 0.0;
        foreach (var v in vec)
        {
            sumSq += v * v;
        }
        Assert.InRange(Math.Sqrt(sumSq), 1.0 - 1e-4, 1.0 + 1e-4);
    }

    [Fact]
    public void Embed_SameInputTwice_ProducesIdenticalOutput()
    {
        using var embedder = NewEmbedder();
        var a = embedder.Embed("determinism check");
        var b = embedder.Embed("determinism check");
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i], b[i]);
        }
    }

    [Fact]
    public void Embed_DifferentInputs_ProduceDifferentOutputs()
    {
        using var embedder = NewEmbedder();
        var a = embedder.Embed("cats are soft");
        var b = embedder.Embed("quantum chromodynamics");
        Assert.Equal(a.Length, b.Length);
        // Vectors should not be element-wise identical.
        bool allEqual = true;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                allEqual = false;
                break;
            }
        }
        Assert.False(allEqual, "Different inputs produced identical embeddings.");
    }

    [Fact]
    public void Embed_EmptyString_DoesNotThrow()
    {
        using var embedder = NewEmbedder();
        var vec = embedder.Embed(string.Empty);
        Assert.Equal(ExpectedDimensions, vec.Length);
    }

    [Fact]
    public void Embed_LazyInitialization_FirstCallLoads()
    {
        using var embedder = NewEmbedder();
        Assert.False(embedder.IsLoaded);
        embedder.Embed("warm up");
        Assert.True(embedder.IsLoaded);
    }

    [Fact]
    public void Embed_ReusesSessionOnSecondCall()
    {
        using var embedder = NewEmbedder();
        embedder.Embed("first");
        Assert.True(embedder.IsLoaded);
        // Second call should not change the loaded state and should succeed.
        var second = embedder.Embed("second");
        Assert.True(embedder.IsLoaded);
        Assert.Equal(ExpectedDimensions, second.Length);
    }

    [Fact]
    public void Dispose_ReleasesSession()
    {
        var embedder = NewEmbedder();
        embedder.Embed("load me");
        Assert.True(embedder.IsLoaded);
        embedder.Dispose();
        Assert.False(embedder.IsLoaded);
    }
}
