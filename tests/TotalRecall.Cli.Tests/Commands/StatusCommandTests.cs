using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Tomlyn.Model;
using TotalRecall.Cli.Commands;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

[Collection("ConsoleCapture")]
public sealed class StatusCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public StatusCommandTests()
    {
        _origOut = Console.Out;
        _origErr = Console.Error;
        Console.SetOut(_outWriter);
        Console.SetError(_errWriter);
    }

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
    }

    private sealed class FakeConfigLoader : IConfigLoader
    {
        private readonly Core.Config.TotalRecallConfig _cfg;
        public FakeConfigLoader(string model, int dims)
        {
            _cfg = new Core.Config.TotalRecallConfig(
                new Core.Config.TiersConfig(
                    new Core.Config.HotTierConfig(20, 4000, 0.5),
                    new Core.Config.WarmTierConfig(1000, 50, 0.3, 90),
                    new Core.Config.ColdTierConfig(500, 50, 1000)),
                new Core.Config.CompactionConfig(168.0, 0.3, 0.7, 30),
                new Core.Config.EmbeddingConfig(model, dims),
                FSharpOption<Core.Config.RegressionConfig>.None,
                FSharpOption<Core.Config.SearchConfig>.None);
        }

        public Core.Config.TotalRecallConfig LoadDefaults() => _cfg;
        public Core.Config.TotalRecallConfig LoadEffectiveConfig(string? userConfigPath = null) => _cfg;
        public TomlTable LoadEffectiveTable(string? userConfigPath = null) => new TomlTable();
    }

    [Fact]
    public async Task EmptyStore_ReturnsZero()
    {
        var store = new FakeSqliteStore();
        var loader = new FakeConfigLoader("all-MiniLM-L6-v2", 384);
        var cmd = new StatusCommand(store, loader, "/tmp/nonexistent-tr.db", new StringWriter());

        var code = await cmd.RunAsync(Array.Empty<string>());

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task UnknownArg_ReturnsExit2()
    {
        var store = new FakeSqliteStore();
        var loader = new FakeConfigLoader("all-MiniLM-L6-v2", 384);
        var cmd = new StatusCommand(store, loader, "/tmp/x.db", new StringWriter());

        var code = await cmd.RunAsync(new[] { "--bogus" });

        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Json_EmptyStore_HasExpectedShape()
    {
        var store = new FakeSqliteStore();
        var loader = new FakeConfigLoader("test-model", 128);
        var injected = new StringWriter();
        var cmd = new StatusCommand(store, loader, "/tmp/nonexistent-tr.db", injected);

        var code = await cmd.RunAsync(new[] { "--json" });

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(injected.ToString());
        var root = doc.RootElement;

        var tiers = root.GetProperty("tierSizes");
        Assert.Equal(0, tiers.GetProperty("hot").GetProperty("memory").GetInt32());
        Assert.Equal(0, tiers.GetProperty("cold").GetProperty("knowledge").GetInt32());

        var kb = root.GetProperty("knowledgeBase");
        Assert.Equal(0, kb.GetProperty("collections").GetArrayLength());
        Assert.Equal(0, kb.GetProperty("totalChunks").GetInt32());

        var db = root.GetProperty("db");
        Assert.Equal("/tmp/nonexistent-tr.db", db.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, db.GetProperty("sizeBytes").ValueKind);

        var emb = root.GetProperty("embedding");
        Assert.Equal("test-model", emb.GetProperty("model").GetString());
        Assert.Equal(128, emb.GetProperty("dimensions").GetInt32());
    }

    [Fact]
    public async Task Json_Seeded_ReflectsCountsAndCollections()
    {
        var store = new FakeSqliteStore();
        // 2 hot memories, 1 warm memory, 1 cold knowledge chunk + 2 collections.
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "hm1"));
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "hm2"));
        store.Seed(Tier.Warm, ContentType.Memory, EntryFactory.Make(id: "wm1"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\"}"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-b",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Beta\"}"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "chunk1", collectionId: "coll-a"));

        var loader = new FakeConfigLoader("mini", 384);
        var injected = new StringWriter();

        // Use a real temp file so sizeBytes is non-null.
        var dbPath = Path.Combine(Path.GetTempPath(),
            "tr-status-test-" + Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllText(dbPath, "stub");
        try
        {
            var cmd = new StatusCommand(store, loader, dbPath, injected);
            var code = await cmd.RunAsync(new[] { "--json" });

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(injected.ToString());
            var root = doc.RootElement;

            var tiers = root.GetProperty("tierSizes");
            Assert.Equal(2, tiers.GetProperty("hot").GetProperty("memory").GetInt32());
            Assert.Equal(1, tiers.GetProperty("warm").GetProperty("memory").GetInt32());
            Assert.Equal(3, tiers.GetProperty("cold").GetProperty("knowledge").GetInt32());

            var kb = root.GetProperty("knowledgeBase");
            Assert.Equal(2, kb.GetProperty("collections").GetArrayLength());
            // totalChunks = coldKnow (3) - collections (2) = 1.
            Assert.Equal(1, kb.GetProperty("totalChunks").GetInt32());

            var db = root.GetProperty("db");
            Assert.Equal(dbPath, db.GetProperty("path").GetString());
            Assert.True(db.GetProperty("sizeBytes").GetInt64() > 0);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Json_MissingDbFile_SizeNull()
    {
        var store = new FakeSqliteStore();
        var loader = new FakeConfigLoader("mini", 384);
        var injected = new StringWriter();
        var cmd = new StatusCommand(
            store, loader,
            "/tmp/definitely-not-a-real-file-" + Guid.NewGuid().ToString("N") + ".db",
            injected);

        var code = await cmd.RunAsync(new[] { "--json" });

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(injected.ToString());
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("db").GetProperty("sizeBytes").ValueKind);
    }
}
