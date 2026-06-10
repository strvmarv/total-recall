using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Memory;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Memory;

[Collection("ConsoleCapture")]
public sealed class UnpinCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public UnpinCommandTests()
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

    private static (UnpinCommand cmd, FakeStore store, FakeVectorSearch vec, RecordingEmbedder emb) Build()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var emb = new RecordingEmbedder();
        return (new UnpinCommand(store, vec, emb), store, vec, emb);
    }

    [Fact]
    public async Task MissingId_ReturnsExit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
        Assert.Contains("<id>", _errWriter.ToString());
    }

    [Fact]
    public async Task EntryNotFound_ReturnsExit1()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "nope" });
        Assert.Equal(1, code);
        Assert.Contains("not found", _errWriter.ToString());
    }

    [Fact]
    public async Task Unpin_MovesToWarm_AndPrints()
    {
        var (cmd, store, vec, emb) = Build();
        store.Seed(Tier.Pinned, ContentType.Memory, EntryFactory.Make("p1", "body"));

        var code = await cmd.RunAsync(new[] { "p1" });

        Assert.Equal(0, code);
        Assert.Single(store.MoveCalls);
        Assert.Equal((Tier.Pinned, ContentType.Memory, Tier.Warm, ContentType.Memory, "p1"), store.MoveCalls[0]);
        Assert.Single(vec.Deletes);
        // p1 is seeded first → synthetic rowid 1 in FakeMemoryInfra.FakeStore.
        Assert.Equal((Tier.Pinned, ContentType.Memory, 1L), vec.Deletes[0]);
        Assert.Single(emb.Calls);
        Assert.Single(vec.Inserts);
        Assert.Equal(Tier.Warm, vec.Inserts[0].Tier);
        Assert.Contains("unpinned p1 -> warm/memory", _outWriter.ToString());
    }

    [Fact]
    public async Task Unpin_NotPinned_Exit2()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make("h1"));

        var code = await cmd.RunAsync(new[] { "h1" });

        Assert.Equal(2, code);
        Assert.Empty(store.MoveCalls);
        Assert.Contains("not pinned", _errWriter.ToString());
        Assert.Contains("hot", _errWriter.ToString());
    }

    [Fact]
    public async Task Unpin_TypeOverride_MovesToKnowledge()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Pinned, ContentType.Memory, EntryFactory.Make("p1"));

        var code = await cmd.RunAsync(new[] { "p1", "--type", "knowledge" });

        Assert.Equal(0, code);
        Assert.Equal((Tier.Pinned, ContentType.Memory, Tier.Warm, ContentType.Knowledge, "p1"), store.MoveCalls[0]);
        Assert.Contains("unpinned p1 -> warm/knowledge", _outWriter.ToString());
    }

    [Fact]
    public async Task Unpin_BadType_Exit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "p1", "--type", "nope" });
        Assert.Equal(2, code);
        Assert.Contains("invalid --type", _errWriter.ToString());
    }

    [Fact]
    public async Task UnknownArg_Exit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "p1", "--scope", "global" });
        Assert.Equal(2, code);
        Assert.Contains("unknown argument", _errWriter.ToString());
    }
}
