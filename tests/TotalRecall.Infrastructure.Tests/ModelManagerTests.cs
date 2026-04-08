using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TotalRecall.Infrastructure.Embedding;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

public sealed class ModelManagerTests : IDisposable
{
    private const string ModelName = "fake-model";
    private const int FakeOnnxSize = 64;

    private static readonly byte[] s_fakeOnnxBytes =
        Enumerable.Range(0, FakeOnnxSize).Select(i => (byte)i).ToArray();

    private static readonly string s_fakeOnnxSha256 =
        Convert.ToHexString(SHA256.HashData(s_fakeOnnxBytes)).ToLowerInvariant();

    private readonly string _tempDir;
    private readonly string _bundledBase;
    private readonly string _userBase;
    private readonly ModelRegistry _registry;

    public ModelManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-mm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _bundledBase = Path.Combine(_tempDir, "bundled");
        _userBase = Path.Combine(_tempDir, "user");
        Directory.CreateDirectory(_bundledBase);
        Directory.CreateDirectory(_userBase);

        var registryJson = $$"""
            {
              "version": 1,
              "models": {
                "{{ModelName}}": {
                  "dimensions": 8,
                  "sha256": "{{s_fakeOnnxSha256}}",
                  "sizeBytes": {{FakeOnnxSize}},
                  "revision": "main",
                  "files": {
                    "model.onnx": "https://example/model.onnx",
                    "extra.txt": "https://example/extra.txt"
                  }
                }
              }
            }
            """;
        var registryPath = Path.Combine(_tempDir, "registry.json");
        File.WriteAllText(registryPath, registryJson);
        _registry = ModelRegistry.LoadFromFile(registryPath);
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
            // best-effort
        }
    }

    private ModelManager NewManager() => new ModelManager(_registry, _bundledBase, _userBase);

    private string MakeValidBundled()
    {
        var dir = Path.Combine(_bundledBase, ModelName);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), s_fakeOnnxBytes);
        File.WriteAllText(Path.Combine(dir, "extra.txt"), "hello");
        return dir;
    }

    // ---------- GetModelPath ----------

    [Fact]
    public void GetModelPath_BundledStructurallyValid_ReturnsBundled()
    {
        var expected = MakeValidBundled();
        var mm = NewManager();
        Assert.Equal(expected, mm.GetModelPath(ModelName));
    }

    [Fact]
    public void GetModelPath_BundledMissing_FallsBackToUserPath()
    {
        var mm = NewManager();
        var path = mm.GetModelPath(ModelName);
        Assert.Equal(Path.Combine(_userBase, ModelName), path);
    }

    [Fact]
    public void GetModelPath_BundledWrongSize_FallsBackToUserPath()
    {
        var dir = Path.Combine(_bundledBase, ModelName);
        Directory.CreateDirectory(dir);
        // Simulate an LFS pointer - tiny file, wrong size.
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(dir, "extra.txt"), "hi");

        var mm = NewManager();
        Assert.Equal(Path.Combine(_userBase, ModelName), mm.GetModelPath(ModelName));
    }

    [Fact]
    public void GetModelPath_UnknownModel_FallsBackToUserPath()
    {
        var mm = NewManager();
        var path = mm.GetModelPath("does-not-exist");
        Assert.Equal(Path.Combine(_userBase, "does-not-exist"), path);
    }

    // ---------- IsStructurallyValid ----------

    [Fact]
    public void IsStructurallyValid_AllFilesPresentWithCorrectSize_True()
    {
        var dir = MakeValidBundled();
        var mm = NewManager();
        Assert.True(mm.IsStructurallyValid(dir, _registry.GetSpec(ModelName)));
    }

    [Fact]
    public void IsStructurallyValid_ModelOnnxMissing_False()
    {
        var dir = Path.Combine(_bundledBase, ModelName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "extra.txt"), "hi");
        var mm = NewManager();
        Assert.False(mm.IsStructurallyValid(dir, _registry.GetSpec(ModelName)));
    }

    [Fact]
    public void IsStructurallyValid_AuxiliaryFileMissing_False()
    {
        var dir = Path.Combine(_bundledBase, ModelName);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), s_fakeOnnxBytes);
        // Omit extra.txt.
        var mm = NewManager();
        Assert.False(mm.IsStructurallyValid(dir, _registry.GetSpec(ModelName)));
    }

    [Fact]
    public void IsStructurallyValid_ModelOnnxWrongSize_False()
    {
        var dir = Path.Combine(_bundledBase, ModelName);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), new byte[] { 0, 1, 2, 3 });
        File.WriteAllText(Path.Combine(dir, "extra.txt"), "hi");
        var mm = NewManager();
        Assert.False(mm.IsStructurallyValid(dir, _registry.GetSpec(ModelName)));
    }

    [Fact]
    public void IsStructurallyValid_DirMissing_False()
    {
        var mm = NewManager();
        var dir = Path.Combine(_bundledBase, "never-created");
        Assert.False(mm.IsStructurallyValid(dir, _registry.GetSpec(ModelName)));
    }

    // ---------- IsChecksumValid ----------

    [Fact]
    public void IsChecksumValid_NoSidecar_HashesAndWritesSidecar()
    {
        var dir = MakeValidBundled();
        var sidecar = Path.Combine(dir, ".verified");
        Assert.False(File.Exists(sidecar));

        var mm = NewManager();
        Assert.True(mm.IsChecksumValid(dir, _registry.GetSpec(ModelName)));

        Assert.True(File.Exists(sidecar));
        Assert.Equal(s_fakeOnnxSha256, File.ReadAllText(sidecar).Trim(), ignoreCase: true);
    }

    [Fact]
    public void IsChecksumValid_SidecarMatches_TrueWithoutReHashing()
    {
        var dir = MakeValidBundled();
        var sidecar = Path.Combine(dir, ".verified");
        File.WriteAllText(sidecar, s_fakeOnnxSha256);

        // Corrupt the onnx file AFTER writing the sidecar. If the sidecar
        // cache path is taken, we should still return true.
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), new byte[FakeOnnxSize]);

        var mm = NewManager();
        Assert.True(mm.IsChecksumValid(dir, _registry.GetSpec(ModelName)));
    }

    [Fact]
    public void IsChecksumValid_HashMismatch_False()
    {
        var dir = Path.Combine(_bundledBase, ModelName);
        Directory.CreateDirectory(dir);
        // Size matches but bytes differ -> hash mismatch.
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), new byte[FakeOnnxSize]);
        File.WriteAllText(Path.Combine(dir, "extra.txt"), "hi");

        var mm = NewManager();
        Assert.False(mm.IsChecksumValid(dir, _registry.GetSpec(ModelName)));
        Assert.False(File.Exists(Path.Combine(dir, ".verified")));
    }

    // ---------- EnsureModelAvailable ----------

    [Fact]
    public void EnsureModelAvailable_ValidBundled_ReturnsPath()
    {
        var expected = MakeValidBundled();
        var mm = NewManager();
        Assert.Equal(expected, mm.EnsureModelAvailable(ModelName));
    }

    [Fact]
    public void EnsureModelAvailable_Missing_Throws()
    {
        var mm = NewManager();
        var ex = Assert.Throws<InvalidOperationException>(
            () => mm.EnsureModelAvailable(ModelName));
        Assert.Contains(ModelName, ex.Message);
        Assert.Contains("model.onnx", ex.Message);
        Assert.Contains("extra.txt", ex.Message);
    }

    [Fact]
    public void EnsureModelAvailable_UnknownModel_ThrowsFromRegistry()
    {
        var mm = NewManager();
        Assert.Throws<KeyNotFoundException>(() => mm.EnsureModelAvailable("who?"));
    }
}
