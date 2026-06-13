using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands;

public sealed class PinnedFloorCommandTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "tr-floorcmd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private static readonly FloorThresholds Always = new(true, 1, 6000); // inject every turn after seed

    private PinnedFloorCommand MakeCmd(FakeStore store, TextReader stdin, TextWriter stdout,
        Func<string, long?>? sizer = null) =>
        new PinnedFloorCommand(store, _dir, stdin, stdout, Always, sizer ?? (_ => null));

    [Fact]
    public async Task NoOpTurn_EmitsEmptyObject_AndSeeds()
    {
        var store = new FakeStore();
        var outw = new StringWriter();
        var cmd = MakeCmd(store, new StringReader("{\"session_id\":\"s1\",\"transcript_path\":\"/x\"}"),
            outw, _ => 1000);
        var code = await cmd.RunAsync(new[] { "--host", "claude-code" });
        Assert.Equal(0, code);
        Assert.Equal("{}", outw.ToString().Trim());
        Assert.True(File.Exists(Path.Combine(_dir, PinnedFloorState.FileName("s1"))));
    }

    [Fact]
    public async Task InjectTurn_ClaudeCode_EmitsHookSpecificAdditionalContext()
    {
        var store = new FakeStore();
        store.Seed(Tier.Pinned, ContentType.Memory, EntryFactory.Make(id: "p1", content: "never delete prod"));
        PinnedFloorState.Save(_dir, new FloorState("s2", 1, 1, 0, true));
        var outw = new StringWriter();
        var cmd = MakeCmd(store, new StringReader("{\"session_id\":\"s2\",\"transcript_path\":\"/x\"}"), outw);
        var code = await cmd.RunAsync(new[] { "--host", "claude-code" });
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(outw.ToString());
        var ctx = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();
        Assert.Contains("## Pinned directives (always follow)", ctx);
        Assert.Contains("(Reminder)", ctx);
        Assert.Contains("never delete prod", ctx);
    }

    [Fact]
    public async Task InjectTurn_CopilotCli_EmitsTopLevelAdditionalContext()
    {
        var store = new FakeStore();
        store.Seed(Tier.Pinned, ContentType.Memory, EntryFactory.Make(id: "p1", content: "rule"));
        PinnedFloorState.Save(_dir, new FloorState("s3", 1, 1, 0, true));
        var outw = new StringWriter();
        var cmd = MakeCmd(store, new StringReader("{\"session_id\":\"s3\"}"), outw);
        var code = await cmd.RunAsync(new[] { "--host", "copilot-cli" });
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(outw.ToString());
        Assert.True(doc.RootElement.TryGetProperty("additionalContext", out _));
        Assert.False(doc.RootElement.TryGetProperty("hookSpecificOutput", out _));
    }

    [Fact]
    public async Task MalformedStdin_FailsSafe_EmptyObjectExitZero()
    {
        var store = new FakeStore();
        var outw = new StringWriter();
        var cmd = MakeCmd(store, new StringReader("{ not json"), outw);
        var code = await cmd.RunAsync(new[] { "--host", "claude-code" });
        Assert.Equal(0, code);
        Assert.Equal("{}", outw.ToString().Trim());
    }

    [Fact]
    public async Task UnknownHost_FailsSafe_EmptyObjectExitZero()
    {
        var store = new FakeStore();
        store.Seed(Tier.Pinned, ContentType.Memory, EntryFactory.Make(id: "p1", content: "rule"));
        PinnedFloorState.Save(_dir, new FloorState("s4", 1, 1, 0, true));
        var outw = new StringWriter();
        var cmd = MakeCmd(store, new StringReader("{\"session_id\":\"s4\"}"), outw);
        var code = await cmd.RunAsync(new[] { "--host", "bogus" });
        Assert.Equal(0, code);
        Assert.Equal("{}", outw.ToString().Trim()); // cursor/unknown cannot inject -> no-op
    }
}
