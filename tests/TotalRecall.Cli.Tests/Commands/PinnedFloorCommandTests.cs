using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Cli.Commands;
using TotalRecall.Cli.Tests.TestSupport;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
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

    [Fact]
    public async Task InjectPath_RenderThrows_FailsSafe_AndDoesNotAdvanceState()
    {
        // A store whose List(Pinned, Memory) throws simulates a DB error mid-render.
        // After the reorder fix, Save runs only AFTER a successful render, so a
        // render failure must leave state untouched (retried next turn).
        var store = new ThrowingOnPinnedListStore();
        PinnedFloorState.Save(_dir, new FloorState("s9", 1, 1, 0, true));
        var outw = new StringWriter();
        var cmd = new PinnedFloorCommand(store, _dir, new StringReader("{\"session_id\":\"s9\"}"), outw, Always, _ => null);

        var code = await cmd.RunAsync(new[] { "--host", "claude-code" });

        Assert.Equal(0, code);
        Assert.Equal("{}", outw.ToString().Trim());
        // State NOT advanced past the pre-seeded turn — no Save ran on render failure.
        var after = PinnedFloorState.Load(_dir, "s9");
        Assert.Equal(1, after.TurnCount);
        Assert.Equal(1, after.LastInjectedTurn);
    }
}

/// <summary>
/// Delegates all IStore calls to a FakeStore, but throws on
/// List(Tier.Pinned, ContentType.Memory) to simulate a DB error during render.
/// </summary>
internal sealed class ThrowingOnPinnedListStore : IStore
{
    private readonly FakeStore _inner = new();

    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
    {
        if (tier == Tier.Pinned && type == ContentType.Memory)
            throw new InvalidOperationException("simulated DB error during pinned-memory list");
        return _inner.List(tier, type, opts);
    }

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts) => _inner.Insert(tier, type, opts);
    public string InsertWithEmbedding(Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding) => _inner.InsertWithEmbedding(tier, type, opts, embedding);
    public Entry? Get(Tier tier, ContentType type, string id) => _inner.Get(tier, type, id);
    public long? GetInternalKey(Tier tier, ContentType type, string id) => _inner.GetInternalKey(tier, type, id);
    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) => _inner.Update(tier, type, id, opts);
    public void Delete(Tier tier, ContentType type, string id) => _inner.Delete(tier, type, id);
    public int Count(Tier tier, ContentType type) => _inner.Count(tier, type);
    public int CountKnowledgeCollections() => _inner.CountKnowledgeCollections();
    public IReadOnlyList<Entry> ListByMetadata(Tier tier, ContentType type, IReadOnlyDictionary<string, string> metadataFilter, ListEntriesOpts? opts = null) => _inner.ListByMetadata(tier, type, metadataFilter, opts);
    public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id) => _inner.Move(fromTier, fromType, toTier, toType, id);
    public string? FindByContent(Tier tier, ContentType type, string content) => _inner.FindByContent(tier, type, content);
    public void UpdateInjectionCounts(IReadOnlyList<(Tier tier, ContentType type, string id)> entries) => _inner.UpdateInjectionCounts(entries);
}
