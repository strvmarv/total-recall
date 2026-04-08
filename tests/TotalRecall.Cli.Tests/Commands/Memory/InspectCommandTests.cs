using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands.Memory;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using Xunit;

namespace TotalRecall.Cli.Tests.Commands.Memory;

[Collection("ConsoleCapture")]
public sealed class InspectCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public InspectCommandTests()
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
        var cmd = new InspectCommand(new FakeSqliteStore());
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(2, code);
        Assert.Contains("<id>", _errWriter.ToString());
    }

    [Fact]
    public async Task NotFound_ReturnsExit1()
    {
        var cmd = new InspectCommand(new FakeSqliteStore());
        var code = await cmd.RunAsync(new[] { "nope" });
        Assert.Equal(1, code);
        Assert.Contains("not found", _errWriter.ToString());
    }

    [Fact]
    public async Task HappyPath_DefaultRendering_ReturnsZero()
    {
        // Default rendering goes through Spectre.Console.AnsiConsole which
        // does not honor Console.SetOut — see CliApp.cs comment. So we only
        // assert the exit code here; the --json path below exercises the
        // full field set via a parseable payload.
        var store = new FakeSqliteStore();
        store.Seed(Tier.Warm, ContentType.Memory, EntryFactory.Make(
            id: "abc",
            content: "hello body",
            project: "demo",
            tags: new[] { "alpha", "beta" }));
        var cmd = new InspectCommand(store);

        var code = await cmd.RunAsync(new[] { "abc" });

        Assert.Equal(0, code);
        Assert.Equal("", _errWriter.ToString());
    }

    [Fact]
    public async Task JsonPath_RoundTripsAllFields()
    {
        var store = new FakeSqliteStore();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(
            id: "abc",
            content: "hello body",
            summary: "short summary",
            source: "cli",
            project: "demo",
            tags: new[] { "alpha", "beta" },
            createdAt: 1_700_000_000_000L,
            updatedAt: 1_700_000_001_000L,
            lastAccessedAt: 1_700_000_002_000L,
            accessCount: 7,
            decayScore: 0.5,
            metadataJson: "{\"k\":\"v\"}"));
        var cmd = new InspectCommand(store);

        var code = await cmd.RunAsync(new[] { "abc", "--json" });

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(_outWriter.ToString());
        var root = doc.RootElement;
        Assert.Equal("abc", root.GetProperty("id").GetString());
        Assert.Equal("hot", root.GetProperty("tier").GetString());
        Assert.Equal("memory", root.GetProperty("content_type").GetString());
        Assert.Equal("hello body", root.GetProperty("content").GetString());
        Assert.Equal("short summary", root.GetProperty("summary").GetString());
        Assert.Equal("cli", root.GetProperty("source").GetString());
        Assert.Equal("demo", root.GetProperty("project").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("source_tool").ValueKind);
        var tags = root.GetProperty("tags");
        Assert.Equal(2, tags.GetArrayLength());
        Assert.Equal("alpha", tags[0].GetString());
        Assert.Equal("beta", tags[1].GetString());
        Assert.Equal(1_700_000_000_000L, root.GetProperty("created_at").GetInt64());
        Assert.Equal(1_700_000_001_000L, root.GetProperty("updated_at").GetInt64());
        Assert.Equal(1_700_000_002_000L, root.GetProperty("last_accessed_at").GetInt64());
        Assert.NotNull(root.GetProperty("created_at_iso").GetString());
        Assert.Equal(7, root.GetProperty("access_count").GetInt32());
        Assert.Equal(0.5, root.GetProperty("decay_score").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("parent_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("collection_id").ValueKind);
        Assert.Equal("{\"k\":\"v\"}", root.GetProperty("metadata").GetString());
    }

    [Fact]
    public async Task JsonPath_EscapesSpecialChars()
    {
        var store = new FakeSqliteStore();
        var nasty = "has \"quotes\"\nand \\ backslash\tand tab";
        store.Seed(Tier.Cold, ContentType.Memory, EntryFactory.Make(
            id: "abc",
            content: nasty));
        var cmd = new InspectCommand(store);

        var code = await cmd.RunAsync(new[] { "abc", "--json" });

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(_outWriter.ToString());
        Assert.Equal(nasty, doc.RootElement.GetProperty("content").GetString());
    }
}
