using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Memory;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Memory;

[Collection("ConsoleCapture")]
public sealed class DemoteCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public DemoteCommandTests()
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

    private static (DemoteCommand cmd, FakeSqliteStore store, FakeVectorSearch vec, RecordingEmbedder emb) Build()
    {
        var store = new FakeSqliteStore();
        var vec = new FakeVectorSearch();
        var emb = new RecordingEmbedder();
        return (new DemoteCommand(store, vec, emb), store, vec, emb);
    }

    [Fact]
    public async Task MissingId_ReturnsExit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
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
    public async Task HappyPath_HotMemoryToCold_MovesAndReEmbeds()
    {
        var (cmd, store, vec, emb) = Build();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make("abc", "body"));

        var code = await cmd.RunAsync(new[] { "abc", "--tier", "cold" });

        Assert.Equal(0, code);
        Assert.Single(vec.Deletes);
        // abc is seeded first → synthetic rowid 1 in FakeMemoryInfra.FakeSqliteStore.
        Assert.Equal((Tier.Hot, ContentType.Memory, 1L), vec.Deletes[0]);
        Assert.Equal((Tier.Hot, ContentType.Memory, Tier.Cold, ContentType.Memory, "abc"), store.MoveCalls[0]);
        Assert.Single(emb.Calls);
        Assert.Equal(Tier.Cold, vec.Inserts[0].Tier);
        Assert.Contains("demoted abc from hot/memory to cold/memory", _outWriter.ToString());
    }

    [Fact]
    public async Task DefaultsToWarm()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Hot, ContentType.Knowledge, EntryFactory.Make("k1"));
        var code = await cmd.RunAsync(new[] { "k1" });
        Assert.Equal(0, code);
        Assert.Equal((Tier.Hot, ContentType.Knowledge, Tier.Warm, ContentType.Knowledge, "k1"), store.MoveCalls[0]);
    }

    [Fact]
    public async Task InvalidDirection_WarmToHot_ReturnsExit2()
    {
        var (cmd, _, _, _) = Build();
        var code = await cmd.RunAsync(new[] { "abc", "--tier", "hot" });
        Assert.Equal(2, code);
        Assert.Contains("cannot demote to hot", _errWriter.ToString());
    }

    [Fact]
    public async Task InvalidDirection_ColdToWarm_ReturnsExit2()
    {
        var (cmd, store, _, _) = Build();
        store.Seed(Tier.Cold, ContentType.Memory, EntryFactory.Make("abc"));
        var code = await cmd.RunAsync(new[] { "abc", "--tier", "warm" });
        Assert.Equal(2, code);
        Assert.Contains("target must be colder", _errWriter.ToString());
    }
}
