using System;
using System.IO;
using TotalRecall.Infrastructure.Embedding;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

public sealed class ModelRegistryTests : IDisposable
{
    private readonly string _tempDir;

    public ModelRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private string WriteRegistry(string json)
    {
        var path = Path.Combine(_tempDir, "registry.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void LoadFromFile_RealRepoRegistry_ReturnsRegistry()
    {
        // Walk up to find the repo root (registry lives at <repo>/models/registry.json).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? registryPath = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "models", "registry.json");
            if (File.Exists(candidate))
            {
                registryPath = candidate;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(registryPath);

        var registry = ModelRegistry.LoadFromFile(registryPath!);
        Assert.Equal(1, registry.Version);

        var spec = registry.GetSpec("all-MiniLM-L6-v2");
        Assert.Equal("all-MiniLM-L6-v2", spec.Name);
        Assert.Equal(384, spec.Dimensions);
        Assert.Equal(90405214, spec.SizeBytes);
        Assert.Equal("main", spec.Revision);
        Assert.Contains("model.onnx", spec.Files.Keys);
        Assert.Contains("tokenizer.json", spec.Files.Keys);
        Assert.Contains("tokenizer_config.json", spec.Files.Keys);
    }

    [Fact]
    public void LoadFromFile_UnsupportedVersion_Throws()
    {
        var path = WriteRegistry("""{ "version": 2, "models": {} }""");
        var ex = Assert.Throws<InvalidDataException>(() => ModelRegistry.LoadFromFile(path));
        Assert.Contains("Unsupported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromFile_MalformedJson_Throws()
    {
        var path = WriteRegistry("{ this is not json");
        Assert.Throws<InvalidDataException>(() => ModelRegistry.LoadFromFile(path));
    }

    [Fact]
    public void LoadFromFile_FileNotFound_Throws()
    {
        var path = Path.Combine(_tempDir, "does-not-exist.json");
        Assert.Throws<FileNotFoundException>(() => ModelRegistry.LoadFromFile(path));
    }

    [Fact]
    public void GetSpec_ExistingModel_ReturnsSpec()
    {
        var path = WriteRegistry("""
            {
              "version": 1,
              "models": {
                "fake-model": {
                  "dimensions": 8,
                  "sha256": "abc",
                  "sizeBytes": 16,
                  "revision": "main",
                  "files": { "model.onnx": "https://example/model.onnx" }
                }
              }
            }
            """);
        var registry = ModelRegistry.LoadFromFile(path);
        var spec = registry.GetSpec("fake-model");
        Assert.Equal(8, spec.Dimensions);
        Assert.Equal("abc", spec.Sha256);
        Assert.Equal(16, spec.SizeBytes);
    }

    [Fact]
    public void GetSpec_UnknownModel_ThrowsWithAvailableList()
    {
        var path = WriteRegistry("""
            {
              "version": 1,
              "models": {
                "fake-model": {
                  "dimensions": 8,
                  "sha256": "abc",
                  "sizeBytes": 16,
                  "revision": "main",
                  "files": { "model.onnx": "https://example/model.onnx" }
                }
              }
            }
            """);
        var registry = ModelRegistry.LoadFromFile(path);
        var ex = Assert.Throws<KeyNotFoundException>(() => registry.GetSpec("nope"));
        Assert.Contains("fake-model", ex.Message);
        Assert.Contains("nope", ex.Message);
    }
}
