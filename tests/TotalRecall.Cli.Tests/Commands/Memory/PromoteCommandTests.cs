using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Memory;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Memory;

[Collection("ConsoleCapture")]
public sealed class PromoteCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public PromoteCommandTests()
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

    private static (PromoteCommand cmd, FakeSqliteStore store, FakeVectorSearch vec, RecordingEmbedder emb) Build()
    {
        var store = new FakeSqliteStore();
        var vec = new FakeVectorSearch();
        var emb = new RecordingEmbedder();
        return (new PromoteCommand(store, vec, emb), store, vec, emb);
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
    public async Task HappyPath_ColdMemoryToWarm_MovesAndReEmbeds()
    {
        var (cmd, store, vec, emb) = Build();
        store.Seed(Tier.Cold, ContentType.Memory, EntryFactory.Make("abc", "body"));

        var code = await cmd.RunAsync(new[] { "abc", "--tier", "warm" });

        Assert.Equal(0, code);
        Assert.Single(vec.Deletes);
        Assert.Equal((Tier.Cold, ContentType.Memory, "abc"), vec.Deletes[0]);
        Assert.Single(store.MoveCalls);
        Assert.Equal((Tier.Cold, ContentType.Memory, Tier.Warm, ContentType.Memory, "abc"), store.MoveCalls[0]);
        Assert.Single(emb.Calls);
        Assert.Equal("body", emb.Calls[0]);
        Assert.Single(vec.Inserts);
        Assert.Equal(Tier.Warm, vec.Inserts[0].Tier);
        Assert.Equal(ContentType.Memory, vec.Inserts[0].Type);
        Assert.Equal("abc", vec.Inserts[0].Id);
        Assert.Contains("promoted abc from cold/memory to warm/memory", _outWriter.ToString());
    }

    [Fact]
    public async Task HappyPath_DefaultsToHot()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Warm, ContentType.Knowledge, EntryFactory.Make("k1"));
        var code = await cmd.RunAsync(new[] { "k1" });
        Assert.Equal(0, code);
        Assert.Equal((Tier.Warm, ContentType.Knowledge, Tier.Hot, ContentType.Knowledge, "k1"), store.MoveCalls[0]);
    }

    [Fact]
    public async Task InvalidDirection_HotToCold_ReturnsExit2()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make("abc"));
        var code = await cmd.RunAsync(new[] { "abc", "--tier", "hot" });
        // hot -> hot is not strictly warmer
        Assert.Equal(2, code);
        Assert.Contains("target must be warmer", _errWriter.ToString());
    }

    [Fact]
    public async Task PromoteToCold_ReturnsExit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "abc", "--tier", "cold" });
        Assert.Equal(2, code);
        Assert.Contains("cannot promote to cold", _errWriter.ToString());
    }

    [Fact]
    public async Task BadTier_ReturnsExit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "abc", "--tier", "nope" });
        Assert.Equal(2, code);
        Assert.Contains("invalid --tier", _errWriter.ToString());
    }
}
