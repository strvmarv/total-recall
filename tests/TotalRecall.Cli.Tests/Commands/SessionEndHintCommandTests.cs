using System;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

public sealed class SessionEndHintCommandTests
{
    [Fact]
    public async Task ClaudeCode_AtThreshold_EmitsSystemMessage()
    {
        var store = new FakeStore();
        for (var i = 0; i < 5; i++)
            store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: $"h{i}"));
        var outw = new StringWriter();

        var cmd = new SessionEndHintCommand(store, outw, threshold: 5);
        var code = await cmd.RunAsync(new[] { "--host", "claude-code" });

        Assert.Equal(0, code);
        Assert.Contains("systemMessage", outw.ToString());
        Assert.DoesNotContain("hookSpecificOutput", outw.ToString());
        Assert.DoesNotContain("additionalContext", outw.ToString());
    }

    [Fact]
    public async Task BelowThreshold_EmitsEmptyObject()
    {
        var store = new FakeStore();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "h0"));
        var outw = new StringWriter();

        var cmd = new SessionEndHintCommand(store, outw, threshold: 5);
        var code = await cmd.RunAsync(new[] { "--host", "claude-code" });

        Assert.Equal(0, code);
        Assert.Equal("{}", outw.ToString().Trim());
    }

    [Fact]
    public async Task CursorHost_AtThreshold_EmitsEmptyObject()
    {
        var store = new FakeStore();
        for (var i = 0; i < 5; i++)
            store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: $"h{i}"));
        var outw = new StringWriter();

        var cmd = new SessionEndHintCommand(store, outw, threshold: 5);
        var code = await cmd.RunAsync(new[] { "--host", "cursor" });

        Assert.Equal(0, code);
        Assert.Equal("{}", outw.ToString().Trim());
        Assert.DoesNotContain("systemMessage", outw.ToString());
    }

    [Fact]
    public async Task ThresholdZero_EmitsEmptyObject()
    {
        var store = new FakeStore();
        for (var i = 0; i < 5; i++)
            store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: $"h{i}"));
        var outw = new StringWriter();

        var cmd = new SessionEndHintCommand(store, outw, threshold: 0);
        var code = await cmd.RunAsync(new[] { "--host", "claude-code" });

        Assert.Equal(0, code);
        Assert.Equal("{}", outw.ToString().Trim());
        Assert.DoesNotContain("systemMessage", outw.ToString());
    }
}
