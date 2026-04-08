using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Tomlyn;
using Tomlyn.Model;
using TotalRecall.Cli.Commands.Config;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Config;

[Collection("ConsoleCapture")]
public sealed class SetCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();
    private readonly string _tempDir;

    public SetCommandTests()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
        _tempDir = Path.Combine(Path.GetTempPath(), "tr-set-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeLoader : IConfigLoader
    {
        public Core.Config.TotalRecallConfig LoadDefaults()
            => new ConfigLoader().LoadDefaults();
        public Core.Config.TotalRecallConfig LoadEffectiveConfig(string? userConfigPath = null)
            => new ConfigLoader().LoadDefaults();
        public TomlTable LoadEffectiveTable(string? userConfigPath = null)
            => new ConfigLoader().LoadEffectiveTable(userConfigPath);
    }

    private sealed class FakeSnapshotStore : IConfigSnapshotStore
    {
        public List<(string ConfigJson, string? Name)> Created { get; } = new();

        public string CreateSnapshot(string configJson, string? name = null)
        {
            Created.Add((configJson, name));
            return "fake-" + Created.Count;
        }
        public ConfigSnapshotRow? GetLatest() => null;
        public ConfigSnapshotRow? GetById(string id) => null;
        public ConfigSnapshotRow? GetByName(string name) => null;
        public IReadOnlyList<ConfigSnapshotRow> ListRecent(int limit) => Array.Empty<ConfigSnapshotRow>();
        public string? ResolveRef(string nameOrId) => null;
    }

    private string TempConfigPath() => Path.Combine(_tempDir, "config.toml");

    [Fact]
    public async Task MissingKey_Exit2()
    {
        var cmd = new SetCommand(new FakeLoader(), TempConfigPath(), null, new StringWriter());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task MissingValue_Exit2()
    {
        var cmd = new SetCommand(new FakeLoader(), TempConfigPath(), null, new StringWriter());
        var code = await cmd.RunAsync(new[] { "tiers.hot.max_entries", "--no-snapshot" });
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task HappyPath_NoSnapshot_WritesFile()
    {
        var path = TempConfigPath();
        var sink = new StringWriter();
        var cmd = new SetCommand(new FakeLoader(), path, null, sink);

        var code = await cmd.RunAsync(new[] { "tiers.warm.similarity_threshold", "0.99", "--no-snapshot" });

        Assert.Equal(0, code);
        Assert.True(File.Exists(path));
        var model = Toml.Parse(File.ReadAllText(path)).ToModel();
        var tiers = Assert.IsType<TomlTable>(model["tiers"]);
        var warm = Assert.IsType<TomlTable>(tiers["warm"]);
        Assert.Equal(0.99, Convert.ToDouble(warm["similarity_threshold"]));
        Assert.Contains("set tiers.warm.similarity_threshold", sink.ToString());
    }

    [Fact]
    public async Task Coerce_Bool_True()
    {
        Assert.Equal(true, SetCommand.Coerce("true"));
    }

    [Fact]
    public async Task Coerce_Bool_False()
    {
        Assert.Equal(false, SetCommand.Coerce("false"));
    }

    [Fact]
    public async Task Coerce_Long()
    {
        Assert.Equal(42L, SetCommand.Coerce("42"));
        Assert.Equal(-7L, SetCommand.Coerce("-7"));
    }

    [Fact]
    public async Task Coerce_Double()
    {
        Assert.Equal(3.14, SetCommand.Coerce("3.14"));
    }

    [Fact]
    public async Task Coerce_StringFallback()
    {
        Assert.Equal("hello", SetCommand.Coerce("hello"));
    }

    [Fact]
    public async Task HappyPath_Bool_RoundTrips()
    {
        var path = TempConfigPath();
        var cmd = new SetCommand(new FakeLoader(), path, null, new StringWriter());
        var code = await cmd.RunAsync(new[] { "feature.enabled", "true", "--no-snapshot" });
        Assert.Equal(0, code);
        var text = File.ReadAllText(path);
        Assert.Contains("enabled = true", text);
    }

    [Fact]
    public async Task HappyPath_Int_RoundTrips()
    {
        var path = TempConfigPath();
        var cmd = new SetCommand(new FakeLoader(), path, null, new StringWriter());
        var code = await cmd.RunAsync(new[] { "tiers.hot.max_entries", "999", "--no-snapshot" });
        Assert.Equal(0, code);
        var text = File.ReadAllText(path);
        Assert.Contains("max_entries = 999", text);
    }

    [Fact]
    public async Task HappyPath_Double_RoundTrips()
    {
        var path = TempConfigPath();
        var cmd = new SetCommand(new FakeLoader(), path, null, new StringWriter());
        var code = await cmd.RunAsync(new[] { "tiers.warm.similarity_threshold", "0.42", "--no-snapshot" });
        Assert.Equal(0, code);
        var model = Toml.Parse(File.ReadAllText(path)).ToModel();
        var tiers = Assert.IsType<TomlTable>(model["tiers"]);
        var warm = Assert.IsType<TomlTable>(tiers["warm"]);
        Assert.Equal(0.42, Convert.ToDouble(warm["similarity_threshold"]));
    }

    [Fact]
    public async Task HappyPath_String_RoundTrips()
    {
        var path = TempConfigPath();
        var cmd = new SetCommand(new FakeLoader(), path, null, new StringWriter());
        var code = await cmd.RunAsync(new[] { "embedding.model", "other-model", "--no-snapshot" });
        Assert.Equal(0, code);
        var text = File.ReadAllText(path);
        Assert.Contains("model = \"other-model\"", text);
    }

    [Fact]
    public async Task WithSnapshot_InvokesStoreBeforeWrite()
    {
        var path = TempConfigPath();
        var store = new FakeSnapshotStore();
        var cmd = new SetCommand(new FakeLoader(), path, store, new StringWriter());

        var code = await cmd.RunAsync(new[] { "tiers.hot.max_entries", "77" });

        Assert.Equal(0, code);
        Assert.Single(store.Created);
        Assert.Equal("pre-change:tiers.hot.max_entries", store.Created[0].Name);
        Assert.Contains("tiers", store.Created[0].ConfigJson);
        // File was still written.
        Assert.True(File.Exists(path));
    }
}
