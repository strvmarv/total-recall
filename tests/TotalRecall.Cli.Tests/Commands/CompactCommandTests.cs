using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

public sealed class CompactCommandTests
{
    [Fact]
    public async Task Run_PromotesStaleHotEntries_AndReportsCount()
    {
        var store = new FakeStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "stale", lastAccessedAt: 0));
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "fresh", lastAccessedAt: now));
        var outw = new StringWriter();

        var cmd = new CompactCommand(store, outw, warmThreshold: 0.5, decayConstantHours: 168, nowMs: now);
        var code = await cmd.RunAsync(new[] { "--run" });

        Assert.Equal(0, code);
        Assert.Contains("promoted=1", outw.ToString());
    }

    [Fact]
    public async Task NoArgs_PrintsExplainerAndReturnsZero()
    {
        var outw = new StringWriter();
        var cmd = new CompactCommand(outw);
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);
        Assert.Contains("Compaction is driven by", outw.ToString());
    }

    [Fact]
    public async Task UnknownArg_ReturnsExitTwo()
    {
        var outw = new StringWriter();
        var cmd = new CompactCommand(outw);
        var code = await cmd.RunAsync(new[] { "--bogus" });
        Assert.Equal(2, code);
    }
}
