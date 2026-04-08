using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Kb;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Kb;

[Collection("ConsoleCapture")]
public sealed class RemoveCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public RemoveCommandTests()
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
    public async Task MissingId_ReturnsExit2()
    {
        var cmd = new RemoveCommand(new FakeSqliteStore(), new FakeVectorSearch(), new StringWriter());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
        Assert.Contains("<id>", _errWriter.ToString());
    }

    [Fact]
    public async Task NotFound_ReturnsExit1()
    {
        var cmd = new RemoveCommand(new FakeSqliteStore(), new FakeVectorSearch(), new StringWriter(),
            confirmDelegate: _ => true);
        var code = await cmd.RunAsync(new[] { "nope", "--yes" });
        Assert.Equal(1, code);
        Assert.Contains("not found", _errWriter.ToString());
    }

    [Fact]
    public async Task HappyPath_NoCascade_DeletesRootOnly()
    {
        var store = new FakeSqliteStore();
        var vec = new FakeVectorSearch();
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\"}"));
        // seed an unrelated child
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "other-1", collectionId: "coll-a"));
        var injected = new StringWriter();
        var cmd = new RemoveCommand(store, vec, injected);

        var code = await cmd.RunAsync(new[] { "coll-a", "--yes" });

        Assert.Equal(0, code);
        Assert.Single(vec.Deletes);
        Assert.Equal(("coll-a"), vec.Deletes[0].Id);
        Assert.Single(store.DeleteCalls);
        Assert.Equal("coll-a", store.DeleteCalls[0].Id);
        Assert.Contains("removed coll-a", injected.ToString());
    }

    [Fact]
    public async Task HappyPath_Cascade_DeletesChildrenAndRoot()
    {
        var store = new FakeSqliteStore();
        var vec = new FakeVectorSearch();
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\"}"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "doc-1", collectionId: "coll-a"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "chunk-1", parentId: "doc-1", collectionId: "coll-a"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "chunk-2", parentId: "doc-1", collectionId: "coll-a"));
        var injected = new StringWriter();
        var cmd = new RemoveCommand(store, vec, injected);

        var code = await cmd.RunAsync(new[] { "coll-a", "--cascade", "--yes" });

        Assert.Equal(0, code);
        // 3 children + 1 root = 4
        Assert.Equal(4, vec.Deletes.Count);
        Assert.Equal(4, store.DeleteCalls.Count);
        Assert.Contains("removed coll-a + 3 children", injected.ToString());
    }

    [Fact]
    public async Task ConfirmDelegate_Declines_PrintsAbortedExitZero()
    {
        var store = new FakeSqliteStore();
        var vec = new FakeVectorSearch();
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\"}"));
        var injected = new StringWriter();
        var cmd = new RemoveCommand(store, vec, injected, confirmDelegate: _ => false);

        var code = await cmd.RunAsync(new[] { "coll-a" });

        Assert.Equal(0, code);
        Assert.Empty(vec.Deletes);
        Assert.Empty(store.DeleteCalls);
        Assert.Contains("aborted", injected.ToString());
    }

    [Fact]
    public async Task ConfirmDelegate_Accepts_Deletes()
    {
        var store = new FakeSqliteStore();
        var vec = new FakeVectorSearch();
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\"}"));
        var injected = new StringWriter();
        var cmd = new RemoveCommand(store, vec, injected, confirmDelegate: _ => true);

        var code = await cmd.RunAsync(new[] { "coll-a" });

        Assert.Equal(0, code);
        Assert.Single(vec.Deletes);
        Assert.Single(store.DeleteCalls);
    }

    [Fact]
    public async Task Yes_BypassesPrompt()
    {
        var store = new FakeSqliteStore();
        var vec = new FakeVectorSearch();
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(
            id: "coll-a",
            metadataJson: "{\"type\":\"collection\",\"name\":\"Alpha\"}"));
        var injected = new StringWriter();
        // confirmDelegate would reject, but --yes bypasses it.
        var cmd = new RemoveCommand(store, vec, injected, confirmDelegate: _ => false);

        var code = await cmd.RunAsync(new[] { "coll-a", "--yes" });

        Assert.Equal(0, code);
        Assert.Single(store.DeleteCalls);
    }
}
