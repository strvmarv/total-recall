// tests/TotalRecall.Server.Tests/SessionContextHandlerTests.cs
//
// Plan 4 Task 4.10 — unit tests for SessionContextHandler. Uses
// FakeStore's ListSlots seeding (added alongside Task 4.10) to
// drop pre-built F# Entry rows into the hot tier slots. Line-format
// assertions mirror src-ts/tools/session-tools.ts:376-380.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class SessionContextHandlerTests
{
    private static Entry MakeEntry(
        string id,
        string content,
        IEnumerable<string>? tags = null,
        string? project = null)
    {
        return new Entry(
            id,
            content,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            project is null ? FSharpOption<string>.None : FSharpOption<string>.Some(project),
            ListModule.OfSeq(tags ?? Array.Empty<string>()),
            0, 0, 0, 0, 0.0,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            "",
            EntryType.Preference,
            "{}");
    }

    private static async Task<JsonElement> RunAsync(FakeStore store)
    {
        var handler = new SessionContextHandler(store);
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        return JsonDocument.Parse(result.Content[0].Text).RootElement.Clone();
    }

    [Fact]
    public async Task NoHotEntries_ReturnsEmptyContextSentinel()
    {
        var store = new FakeStore();

        var root = await RunAsync(store);

        Assert.Equal(0, root.GetProperty("entryCount").GetInt32());
        Assert.Equal("(no hot tier entries)", root.GetProperty("context").GetString());
    }

    [Fact]
    public async Task HotMemoriesOnly_FormatsLines()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory,
            MakeEntry("m1", "first memory"),
            MakeEntry("m2", "second memory"));

        var root = await RunAsync(store);

        Assert.Equal(2, root.GetProperty("entryCount").GetInt32());
        Assert.Equal(
            "- first memory\n- second memory",
            root.GetProperty("context").GetString());
    }

    [Fact]
    public async Task HotMemoriesWithTags_IncludesTagSuffix()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory,
            MakeEntry("m1", "tagged", tags: new[] { "a", "b" }));

        var root = await RunAsync(store);

        Assert.Equal("- tagged [a, b]", root.GetProperty("context").GetString());
    }

    [Fact]
    public async Task HotMemoriesWithProject_IncludesProjectSuffix()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory,
            MakeEntry("m1", "in project", project: "foo"));

        var root = await RunAsync(store);

        Assert.Equal("- in project (project: foo)", root.GetProperty("context").GetString());
    }

    [Fact]
    public async Task HotMemoriesWithTagsAndProject_BothSuffixes()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory,
            MakeEntry("m1", "full", tags: new[] { "x", "y" }, project: "bar"));

        var root = await RunAsync(store);

        Assert.Equal(
            "- full [x, y] (project: bar)",
            root.GetProperty("context").GetString());
    }

    [Fact]
    public async Task HotMemoriesAndKnowledge_BothIncluded()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory, MakeEntry("m1", "mem"));
        store.SeedList(Tier.Hot, ContentType.Knowledge, MakeEntry("k1", "kb"));

        var root = await RunAsync(store);

        Assert.Equal(2, root.GetProperty("entryCount").GetInt32());
        Assert.Equal("- mem\n- kb", root.GetProperty("context").GetString());
    }

    [Fact]
    public async Task EntryCount_MatchesTotalAcrossTables()
    {
        var store = new FakeStore();
        store.SeedList(Tier.Hot, ContentType.Memory,
            MakeEntry("m1", "a"), MakeEntry("m2", "b"), MakeEntry("m3", "c"));
        store.SeedList(Tier.Hot, ContentType.Knowledge,
            MakeEntry("k1", "d"), MakeEntry("k2", "e"));

        var root = await RunAsync(store);

        Assert.Equal(5, root.GetProperty("entryCount").GetInt32());
    }

    [Fact]
    public async Task NullArguments_DoesNotThrow()
    {
        var store = new FakeStore();
        var handler = new SessionContextHandler(store);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public void Metadata_NameAndDescription()
    {
        var handler = new SessionContextHandler(new FakeStore());

        Assert.Equal("session_context", handler.Name);
        Assert.Contains("hot tier", handler.Description);
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
    }
}
