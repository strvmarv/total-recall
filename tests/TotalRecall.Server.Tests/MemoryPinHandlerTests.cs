// tests/TotalRecall.Server.Tests/MemoryPinHandlerTests.cs
//
// Task 4 (pinned-tier plan 2026-06-09) — MemoryPinHandler contract tests.
// Same conventions as MemoryPromoteHandlerTests: FakeStore, FakeVectorSearch,
// RecordingFakeEmbedder from TestSupport.

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

public class MemoryPinHandlerTests
{
    private static (MemoryPinHandler handler, FakeStore store,
            FakeVectorSearch vec, RecordingFakeEmbedder embedder) MakeHandler()
    {
        var store = new FakeStore();
        var vec = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        return (new MemoryPinHandler(store, vec, embedder), store, vec, embedder);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static Entry MakeEntry(string id, string content = "hello", string? project = null) =>
        new(
            id, content,
            FSharpOption<string>.None, FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            project is null ? FSharpOption<string>.None : FSharpOption<string>.Some(project),
            ListModule.OfSeq(Array.Empty<string>()),
            1L, 2L, 3L, 4, 0.5,
            FSharpOption<string>.None, FSharpOption<string>.None, "", EntryType.Preference, "{}", 0);

    [Theory]
    [InlineData(true)]  // hot source
    [InlineData(false)] // warm source
    public async Task Pin_MovesEntryToPinned(bool fromHot)
    {
        var (handler, store, vec, _) = MakeHandler();
        var src = fromHot ? Tier.Hot : Tier.Warm;
        store.Seed(src, ContentType.Memory, MakeEntry("e1", "body"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"e1"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("pinned", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.Single(store.MoveCalls);
        Assert.Equal(Tier.Pinned, store.MoveCalls[0].ToTier);
        Assert.Single(vec.InsertCalls);
        Assert.Equal(Tier.Pinned, vec.InsertCalls[0].Tier);
    }

    [Fact]
    public async Task Pin_AlreadyPinned_IsIdempotentSuccess()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Pinned, ContentType.Memory, MakeEntry("p1"));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"p1"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Empty(store.MoveCalls); // no move, no re-embed
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("pinned", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Pin_ScopeGlobal_ClearsProject()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("e1", project: "proj-a"));

        await handler.ExecuteAsync(
            ParseArgs("""{"id":"e1","scope":"global"}"""), CancellationToken.None);

        Assert.Single(store.UpdateCalls);
        Assert.True(store.UpdateCalls[0].Opts.ClearProject);
    }

    [Fact]
    public async Task Pin_ScopeProject_SetsProvidedProject()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("e1"));

        await handler.ExecuteAsync(
            ParseArgs("""{"id":"e1","scope":"project","project":"proj-b"}"""), CancellationToken.None);

        Assert.Single(store.UpdateCalls);
        Assert.Equal("proj-b", store.UpdateCalls[0].Opts.Project);
    }

    [Fact]
    public async Task Pin_ScopeProject_NoProjectAnywhere_Throws()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("e1")); // entry has no project

        await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"e1","scope":"project"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Pin_MissingEntry_Throws()
    {
        var (handler, _, _, _) = MakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"nope"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task Pin_ContentAtLimit_Succeeds_OneOverRejected()
    {
        var (handler, store, _, _) = MakeHandler(); // default limit 500
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("ok", new string('a', 500)));
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("big", new string('a', 501)));

        var ok = await handler.ExecuteAsync(ParseArgs("""{"id":"ok"}"""), CancellationToken.None);
        Assert.NotEqual(true, ok.IsError);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => handler.ExecuteAsync(
            ParseArgs("""{"id":"big"}"""), CancellationToken.None));
        Assert.Contains("500", ex.Message);
        Assert.Single(store.MoveCalls); // only the ok entry moved
    }

    [Fact]
    public async Task Pin_ConfigOverride_RaisesLimit()
    {
        var store = new FakeStore();
        var handler = new MemoryPinHandler(store, new FakeVectorSearch(),
            new RecordingFakeEmbedder(), maxContentChars: 1000);
        store.Seed(Tier.Warm, ContentType.Memory, MakeEntry("big", new string('a', 800)));

        var result = await handler.ExecuteAsync(ParseArgs("""{"id":"big"}"""), CancellationToken.None);
        Assert.NotEqual(true, result.IsError);
    }

    // T1: already-pinned no-scope path must not call Update (I1 regression lock)
    [Fact]
    public async Task Pin_AlreadyPinned_NoScope_DoesNotCallUpdate()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Pinned, ContentType.Memory, MakeEntry("p1"));

        await handler.ExecuteAsync(ParseArgs("""{"id":"p1"}"""), CancellationToken.None);

        Assert.Empty(store.MoveCalls);
        Assert.Empty(store.UpdateCalls);
    }

    // T2: already-pinned with existing project, no scope arg → no write
    [Fact]
    public async Task Pin_AlreadyPinned_WithProject_NoScope_DoesNotCallUpdate()
    {
        var (handler, store, _, _) = MakeHandler();
        store.Seed(Tier.Pinned, ContentType.Memory, MakeEntry("p2", project: "existing-proj"));

        await handler.ExecuteAsync(ParseArgs("""{"id":"p2"}"""), CancellationToken.None);

        Assert.Empty(store.MoveCalls);
        Assert.Empty(store.UpdateCalls); // existing project preserved; no write needed
    }

    // T3: pin a hot knowledge entry with type:"knowledge" explicitly →
    // result DTO reflects knowledge content type, move targets knowledge slot.
    // (The handler supports cross-content-type pinning the same way promote does:
    // the caller passes type=<target> and the handler uses it as targetType.)
    [Fact]
    public async Task Pin_KnowledgeEntry_TypeArgKnowledge_ResultIsKnowledge()
    {
        var (handler, store, vec, _) = MakeHandler();
        store.Seed(Tier.Hot, ContentType.Knowledge, MakeEntry("k1", "some kb content"));

        var result = await handler.ExecuteAsync(
            ParseArgs("""{"id":"k1","type":"knowledge"}"""), CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("knowledge", doc.RootElement.GetProperty("to_content_type").GetString());
        Assert.Equal("pinned", doc.RootElement.GetProperty("to_tier").GetString());
        Assert.Single(store.MoveCalls);
        Assert.Equal(ContentType.Knowledge, store.MoveCalls[0].ToType);
        Assert.Equal(Tier.Pinned, store.MoveCalls[0].ToTier);
    }
}
