// memory_recent handler contract tests.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests;

public class MemoryRecentHandlerTests
{
    private static MemoryRecentHandler MakeHandler(FakeStore store) =>
        new(store, scopeDefault: "user:local");

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // created/updated/lastAccessed = ts, ts+1, ts+2; entry_type configurable.
    private static Entry MakeEntry(string id, long ts, EntryType type) =>
        new(
            id, "content for " + id + "  with\nwhitespace",
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            ts, ts + 1, ts + 2, 0, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "user:local", type, "{}");

    private static Entry MakeEntryTs(string id, long created, long updated, long lastAccessed, EntryType type) =>
        new(
            id, "content for " + id,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None, FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            created, updated, lastAccessed, 0, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "user:local", type, "{}");

    [Fact]
    public void Name_IsWireContract()
    {
        Assert.Equal("memory_recent", MakeHandler(new FakeStore()).Name);
    }

    [Fact]
    public async Task MergesTiers_SortsByCreatedDesc_Default()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("h", 100, EntryType.Preference));
        store.SeedList(Tier.Warm, ContentType.Memory, MakeEntry("w", 300, EntryType.Preference));
        store.SeedList(Tier.Cold, ContentType.Memory, MakeEntry("c", 200, EntryType.Preference));

        var result = await MakeHandler(store).ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(3, entries.GetArrayLength());
        Assert.Equal("w", entries[0].GetProperty("id").GetString()); // 300
        Assert.Equal("c", entries[1].GetProperty("id").GetString()); // 200
        Assert.Equal("h", entries[2].GetProperty("id").GetString()); // 100
        Assert.Equal("warm", entries[0].GetProperty("tier").GetString());
        Assert.Equal("created", doc.RootElement.GetProperty("order").GetString());
    }

    [Fact]
    public async Task Limit_CapsResults()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory,
            MakeEntry("a", 100, EntryType.Preference),
            MakeEntry("b", 200, EntryType.Preference),
            MakeEntry("c", 300, EntryType.Preference));

        var result = await MakeHandler(store).ExecuteAsync(
            ParseArgs("""{"limit":2}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("c", doc.RootElement.GetProperty("entries")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task TierFilter_RestrictsToOneTier()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("h", 100, EntryType.Preference));
        store.SeedList(Tier.Warm, ContentType.Memory, MakeEntry("w", 200, EntryType.Preference));

        var result = await MakeHandler(store).ExecuteAsync(
            ParseArgs("""{"tier":"warm"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("w", doc.RootElement.GetProperty("entries")[0].GetProperty("id").GetString());
        Assert.Equal("warm", doc.RootElement.GetProperty("tier").GetString());
    }

    [Fact]
    public async Task TypeFilter_FiltersByEntryType()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory,
            MakeEntry("corr", 100, EntryType.Correction),
            MakeEntry("pref", 200, EntryType.Preference));

        var result = await MakeHandler(store).ExecuteAsync(
            ParseArgs("""{"type":"correction"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("corr", doc.RootElement.GetProperty("entries")[0].GetProperty("id").GetString());
        Assert.Equal("Correction", doc.RootElement.GetProperty("entries")[0].GetProperty("entry_type").GetString());
        Assert.Equal("Correction", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task OrderAccessed_SortsByLastAccessed()
    {
        var store = new FakeStore();
        // a: newest by created (300) but OLDEST by accessed (102).
        // b: oldest by created (100) but NEWEST by accessed (202).
        store.SeedList(Tier.Hot, ContentType.Memory,
            MakeEntryTs("a", created: 300, updated: 301, lastAccessed: 102, EntryType.Preference),
            MakeEntryTs("b", created: 100, updated: 101, lastAccessed: 202, EntryType.Preference));

        var result = await MakeHandler(store).ExecuteAsync(
            ParseArgs("""{"order":"accessed"}"""), CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("accessed", doc.RootElement.GetProperty("order").GetString());
        // last_accessed_at DESC → b (202) before a (102): proves NOT created-order.
        Assert.Equal("b", doc.RootElement.GetProperty("entries")[0].GetProperty("id").GetString());
        Assert.Equal("a", doc.RootElement.GetProperty("entries")[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Preview_CollapsesWhitespace()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("a", 100, EntryType.Preference));
        var result = await MakeHandler(store).ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var preview = doc.RootElement.GetProperty("entries")[0].GetProperty("preview").GetString();
        Assert.DoesNotContain("\n", preview);
        Assert.Equal("content for a with whitespace", preview);
    }

    [Fact]
    public async Task Empty_IsSuccessNotError()
    {
        var result = await MakeHandler(new FakeStore()).ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.False(result.IsError);
    }

    [Theory]
    [InlineData("""{"limit":0}""")]
    [InlineData("""{"limit":201}""")]
    [InlineData("""{"tier":"bogus"}""")]
    [InlineData("""{"type":"bogus"}""")]
    [InlineData("""{"order":"bogus"}""")]
    public async Task InvalidArgs_Throw(string json)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => MakeHandler(new FakeStore()).ExecuteAsync(ParseArgs(json), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("a", 100, EntryType.Preference));
        var result = await MakeHandler(store).ExecuteAsync(null, CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.MemoryRecentResultDto);
        Assert.NotNull(dto);
        Assert.Single(dto!.Entries);
        Assert.Equal("a", dto.Entries[0].Id);
        Assert.Equal("hot", dto.Entries[0].Tier);
    }
}
