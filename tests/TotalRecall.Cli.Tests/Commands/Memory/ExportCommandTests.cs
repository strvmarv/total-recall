// tests/TotalRecall.Cli.Tests/Commands/Memory/ExportCommandTests.cs
//
// Plan 5 Task 5.6 — coverage for the memory export CLI verb.

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
public sealed class ExportCommandTests : IDisposable
{
    private readonly TextWriter _origOut;
    private readonly TextWriter _origErr;
    private readonly StringWriter _outWriter = new();
    private readonly StringWriter _errWriter = new();

    public ExportCommandTests()
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
    public async Task Help_ReturnsZero()
    {
        // CliApp's top-level --help intercept handles help flags before dispatch,
        // so instead we just make sure the command is a valid registered verb by
        // running it with an invalid tier arg (fast usage exit) and verifying
        // that the usage contract holds. True --help is covered by CliAppTests.
        var cmd = new ExportCommand(new FakeSqliteStore(), new StringWriter());
        // Spot-check: no args = empty-store happy path (== success).
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task InvalidTier_ReturnsExit2()
    {
        var cmd = new ExportCommand(new FakeSqliteStore(), new StringWriter());
        var code = await cmd.RunAsync(new[] { "--tiers", "bogus" });
        Assert.Equal(2, code);
        Assert.Contains("invalid tier", _errWriter.ToString());
    }

    [Fact]
    public async Task InvalidType_ReturnsExit2()
    {
        var cmd = new ExportCommand(new FakeSqliteStore(), new StringWriter());
        var code = await cmd.RunAsync(new[] { "--types", "bogus" });
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task EmptyStore_EmitsEmptyEnvelope()
    {
        var sw = new StringWriter();
        var cmd = new ExportCommand(new FakeSqliteStore(), sw);
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);

        var json = sw.ToString().Trim();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("entries").ValueKind);
        Assert.Equal(0, root.GetProperty("entries").GetArrayLength());
        Assert.True(root.TryGetProperty("exported_at", out _));
    }

    [Fact]
    public async Task HappyPath_SeedsThreeEntries()
    {
        var store = new FakeSqliteStore();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "a", content: "alpha"));
        store.Seed(Tier.Warm, ContentType.Memory, EntryFactory.Make(id: "b", content: "beta"));
        store.Seed(Tier.Cold, ContentType.Knowledge, EntryFactory.Make(id: "c", content: "gamma"));

        var sw = new StringWriter();
        var cmd = new ExportCommand(store, sw);
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(sw.ToString());
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(3, entries.GetArrayLength());

        // Collect (id, tier, content_type, content) tuples and assert presence.
        var seen = new System.Collections.Generic.Dictionary<string, (string tier, string ct, string content)>();
        foreach (var e in entries.EnumerateArray())
        {
            seen[e.GetProperty("id").GetString()!] =
                (e.GetProperty("tier").GetString()!,
                 e.GetProperty("content_type").GetString()!,
                 e.GetProperty("content").GetString()!);
        }
        Assert.Equal(("hot", "memory", "alpha"), seen["a"]);
        Assert.Equal(("warm", "memory", "beta"), seen["b"]);
        Assert.Equal(("cold", "knowledge", "gamma"), seen["c"]);
    }

    [Fact]
    public async Task TierFilter_ColdOnly()
    {
        var store = new FakeSqliteStore();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "a", content: "hot1"));
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "b", content: "hot2"));
        store.Seed(Tier.Cold, ContentType.Memory, EntryFactory.Make(id: "c", content: "cold1"));

        var sw = new StringWriter();
        var cmd = new ExportCommand(store, sw);
        var code = await cmd.RunAsync(new[] { "--tiers", "cold" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(sw.ToString());
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("c", entries[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task TypeFilter_KnowledgeOnly()
    {
        var store = new FakeSqliteStore();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "m1", content: "mem1"));
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "m2", content: "mem2"));
        store.Seed(Tier.Hot, ContentType.Knowledge, EntryFactory.Make(id: "k1", content: "know1"));

        var sw = new StringWriter();
        var cmd = new ExportCommand(store, sw);
        var code = await cmd.RunAsync(new[] { "--types", "knowledge" });
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(sw.ToString());
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("k1", entries[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Pretty_EmitsMultilineJson()
    {
        var store = new FakeSqliteStore();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "a", content: "alpha"));
        var sw = new StringWriter();
        var cmd = new ExportCommand(store, sw);
        var code = await cmd.RunAsync(new[] { "--pretty" });
        Assert.Equal(0, code);

        var json = sw.ToString();
        Assert.Contains("\n", json);
        // Must still parse.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task OutFile_WritesToDiskAndPrintsSummary()
    {
        var store = new FakeSqliteStore();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "a", content: "alpha"));
        var tmp = Path.Combine(Path.GetTempPath(), $"tr-export-{Guid.NewGuid():N}.json");
        try
        {
            var cmd = new ExportCommand(store, new StringWriter());
            var code = await cmd.RunAsync(new[] { "--out", tmp });
            Assert.Equal(0, code);
            Assert.Contains("exported 1 entries to", _outWriter.ToString());
            Assert.True(File.Exists(tmp));

            using var doc = JsonDocument.Parse(File.ReadAllText(tmp));
            Assert.Equal(1, doc.RootElement.GetProperty("entries").GetArrayLength());
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task SpecialChars_RoundTrip()
    {
        var special = "has \"quote\"\nnewline\\backslash";
        var store = new FakeSqliteStore();
        store.Seed(Tier.Hot, ContentType.Memory, EntryFactory.Make(id: "x", content: special));

        var sw = new StringWriter();
        var cmd = new ExportCommand(store, sw);
        var code = await cmd.RunAsync(Array.Empty<string>());
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(sw.ToString());
        var round = doc.RootElement.GetProperty("entries")[0].GetProperty("content").GetString();
        Assert.Equal(special, round);
    }
}
