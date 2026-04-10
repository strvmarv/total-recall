using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Kb;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Kb;

[Collection("ConsoleCapture")]
public sealed class ListCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public ListCommandTests()
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

    [Fact]
    public async Task Empty_PrintsSentinel_ReturnsZero()
    {
        var store = new FakeStore();
        var injected = new StringWriter();
        var cmd = new ListCommand(store, injected);

        var code = await cmd.RunAsync(Array.Empty<string>());

        Assert.Equal(0, code);
        Assert.Contains("(no collections)", injected.ToString());
    }

    [Fact]
    public async Task UnknownArg_ReturnsExit2()
    {
        var store = new FakeStore();
        var cmd = new ListCommand(store, new StringWriter());

        var code = await cmd.RunAsync(new[] { "--bogus" });

        Assert.Equal(2, code);
    }

    [Fact]
    public async Task HappyPath_Json_RoundTripsCounts()
    {
        var store = new FakeStore();
        // Two collections.
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\",\"source_path\":\"/tmp/a\"}"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-b",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Beta\",\"source_path\":\"/tmp/b\"}"));
        // coll-a: 2 docs + 3 chunks.
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "doc-a1", collectionId: "coll-a"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "doc-a2", collectionId: "coll-a"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "chunk-a1", parentId: "doc-a1", collectionId: "coll-a"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "chunk-a2", parentId: "doc-a1", collectionId: "coll-a"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "chunk-a3", parentId: "doc-a2", collectionId: "coll-a"));
        // coll-b: 1 doc + 1 chunk.
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "doc-b1", collectionId: "coll-b"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "chunk-b1", parentId: "doc-b1", collectionId: "coll-b"));

        var injected = new StringWriter();
        var cmd = new ListCommand(store, injected);

        var code = await cmd.RunAsync(new[] { "--json" });

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(injected.ToString());
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(2, arr.GetArrayLength());

        var byId = new System.Collections.Generic.Dictionary<string, JsonElement>();
        foreach (var el in arr.EnumerateArray())
        {
            byId[el.GetProperty("id").GetString()!] = el;
        }
        Assert.Equal("Alpha", byId["coll-a"].GetProperty("name").GetString());
        Assert.Equal(2, byId["coll-a"].GetProperty("documents").GetInt32());
        Assert.Equal(3, byId["coll-a"].GetProperty("chunks").GetInt32());
        Assert.Equal("/tmp/a", byId["coll-a"].GetProperty("source_path").GetString());
        Assert.Equal("Beta", byId["coll-b"].GetProperty("name").GetString());
        Assert.Equal(1, byId["coll-b"].GetProperty("documents").GetInt32());
        Assert.Equal(1, byId["coll-b"].GetProperty("chunks").GetInt32());
    }

    [Fact]
    public async Task HappyPath_Default_ReturnsZero()
    {
        // Default rendering goes through Spectre.Console which doesn't honor
        // Console.SetOut — we only assert exit code here.
        var store = new FakeStore();
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\",\"source_path\":\"/tmp/a\"}"));
        var cmd = new ListCommand(store, new StringWriter());

        var code = await cmd.RunAsync(Array.Empty<string>());

        Assert.Equal(0, code);
    }
}
