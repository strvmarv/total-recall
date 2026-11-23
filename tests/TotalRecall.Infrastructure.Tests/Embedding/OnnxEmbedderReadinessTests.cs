using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Embedding;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class OnnxEmbedderReadinessTests : IDisposable
{
    private const string ModelName = "fake-model";
    private const int FakeOnnxSize = 64;

    private static readonly byte[] s_bytes = Enumerable.Range(0, FakeOnnxSize).Select(i => (byte)i).ToArray();
    private static readonly string s_sha = Convert.ToHexString(SHA256.HashData(s_bytes)).ToLowerInvariant();

    private readonly string _tempDir;
    private readonly string _bundled;
    private readonly string _user;
    private readonly ModelRegistry _registry;

    public OnnxEmbedderReadinessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-rdy-" + Guid.NewGuid().ToString("N"));
        _bundled = Path.Combine(_tempDir, "bundled");
        _user = Path.Combine(_tempDir, "user");
        Directory.CreateDirectory(_bundled);
        Directory.CreateDirectory(_user);
        var json = "{\"version\":1,\"models\":{\"" + ModelName + "\":{\"dimensions\":8,\"sha256\":\"" + s_sha +
            "\",\"sizeBytes\":" + FakeOnnxSize + ",\"revision\":\"main\",\"files\":{" +
            "\"model.onnx\":\"https://example/m\",\"tokenizer.json\":\"https://example/t\"}}}}";
        var p = Path.Combine(_tempDir, "registry.json");
        File.WriteAllText(p, json);
        _registry = ModelRegistry.LoadFromFile(p);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private OnnxEmbedder NewEmbedder(string name = ModelName) =>
        new(new ModelManager(_registry, _bundled, _user), name);

    private void WriteModel(string baseDir, byte[]? onnx = null, bool tokenizer = true)
    {
        var dir = Path.Combine(baseDir, ModelName);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), onnx ?? s_bytes);
        if (tokenizer) File.WriteAllText(Path.Combine(dir, "tokenizer.json"), "{}");
    }

    [Fact]
    public void NoModel_NotReady_ReasonNamesTheModel()
    {
        using var e = NewEmbedder();
        var r = e.CheckReady();
        Assert.False(r.Ready);
        Assert.Contains(ModelName, r.Reason);
        Assert.False(e.IsLoaded);
    }

    [Fact]
    public void ValidFiles_Ready_NothingLoaded()
    {
        WriteModel(_bundled);
        using var e = NewEmbedder();
        var r = e.CheckReady();
        Assert.True(r.Ready);
        Assert.Null(r.Reason);
        Assert.False(e.IsLoaded);
        _ = e.Descriptor;
        Assert.False(e.IsLoaded);
    }

    [Fact]
    public void ValidInUserDir_Ready()
    {
        WriteModel(_user);
        using var e = NewEmbedder();
        Assert.True(e.CheckReady().Ready);
    }

    [Fact]
    public void WrongSizedModel_ReasonNamesTheFile()
    {
        WriteModel(_bundled, onnx: new byte[10]);
        using var e = NewEmbedder();
        var r = e.CheckReady();
        Assert.False(r.Ready);
        Assert.Contains("model.onnx", r.Reason);
        Assert.Contains("10", r.Reason);
    }

    [Fact]
    public void MissingTokenizer_ReasonNamesTheFile()
    {
        WriteModel(_bundled, tokenizer: false);
        using var e = NewEmbedder();
        var r = e.CheckReady();
        Assert.False(r.Ready);
        Assert.Contains("tokenizer.json", r.Reason);
    }

    [Fact]
    public void UnknownModel_NotReady()
    {
        using var e = NewEmbedder("nope");
        var r = e.CheckReady();
        Assert.False(r.Ready);
        Assert.Contains("nope", r.Reason);
    }

    [Fact]
    public void RealModel_Ready_WhenPresent()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? models = null;
        while (dir is not null)
        {
            var c = Path.Combine(dir.FullName, "models", "registry.json");
            if (File.Exists(c)) { models = Path.Combine(dir.FullName, "models"); break; }
            dir = dir.Parent;
        }
        Assert.NotNull(models);
        var reg = ModelRegistry.LoadFromFile(Path.Combine(models!, "registry.json"));
        using var e = new OnnxEmbedder(new ModelManager(reg, models!, _user), "bge-small-en-v1.5");
        Assert.True(e.CheckReady().Ready, e.CheckReady().Reason);
        Assert.False(e.IsLoaded);
    }

    private static Core.Config.EmbeddingConfig LocalCfg() => new(
        "bge-small-en-v1.5", 384, FSharpOption<string>.Some("local"),
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None,
        FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None, FSharpOption<string>.None);

    [Fact]
    public void Factory_IntraOpThreads_Passed()
    {
        var e = Assert.IsType<OnnxEmbedder>(EmbedderFactory.CreateFromConfig(LocalCfg(), intraOpThreads: 2));
        using (e) Assert.Equal(2, e.IntraOpThreads);
    }

    [Fact]
    public void Factory_DefaultThreads_Null()
    {
        var e = Assert.IsType<OnnxEmbedder>(EmbedderFactory.CreateFromConfig(LocalCfg()));
        using (e) Assert.Null(e.IntraOpThreads);
    }
}
