// tests/TotalRecall.Server.Tests/Handlers/MemoryExtractHandlerTests.cs
//
// Phase 3 idea 2e — contract tests for MemoryExtractHandler.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

namespace TotalRecall.Server.Tests.Handlers;

public sealed class MemoryExtractHandlerTests
{
    private static (MemoryExtractHandler handler, FakeStore store, RecordingFakeEmbedder embedder)
        MakeHandler(string? scopeDefault = null)
    {
        var store = new FakeStore();
        var embedder = new RecordingFakeEmbedder();
        return (new MemoryExtractHandler(store, embedder, scopeDefault), store, embedder);
    }

    private static JsonElement ParseArgs(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // -------------------------------------------------------------------------
    // Test 1: happy path — three facts with different types
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Extract_StoresEachFactWithMappedEntryType()
    {
        var (handler, store, embedder) = MakeHandler();

        var result = await handler.ExecuteAsync(ParseArgs("""
            {
              "source": "conversation_compact",
              "facts": [
                {"type": "decision", "content": "Use next-auth v5 for OAuth2"},
                {"type": "fact",     "content": "Auth endpoint is https://auth.example.com"},
                {"type": "action_item", "content": "TODO: implement token refresh", "tags": ["auth"]}
              ]
            }
            """), CancellationToken.None);

        // Not an error
        Assert.False(result.IsError);

        // Parse response DTO
        var dto = JsonSerializer.Deserialize(result.Content[0].Text,
            JsonContext.Default.MemoryExtractResultDto);
        Assert.NotNull(dto);
        Assert.Equal(3, dto!.Stored);
        Assert.Equal(0, dto.DuplicatesSkipped);
        Assert.Equal(3, dto.Entries.Length);

        // First entry is "decision"
        Assert.Equal("decision", dto.Entries[0].Type);
        Assert.All(dto.Entries, e => Assert.NotEmpty(e.Id));

        // Three InsertWithEmbedding calls, all Hot/Memory
        Assert.Equal(3, store.InsertWithEmbeddingCalls.Count);
        Assert.All(store.InsertWithEmbeddingCalls, c =>
        {
            Assert.Equal(Tier.Hot, c.Tier);
            Assert.Equal(ContentType.Memory, c.Type);
        });

        // First call: EntryType Decision, Source "conversation_compact"
        var first = store.InsertWithEmbeddingCalls[0];
        Assert.Equal(EntryType.Decision, first.Opts.EntryType);
        Assert.Equal("conversation_compact", first.Opts.Source);
        Assert.Contains("\"entry_type\":\"decision\"", first.Opts.MetadataJson ?? "");

        // Third call: action_item → Surfaced, tags ["auth"], metadata contains "surfaced"
        var third = store.InsertWithEmbeddingCalls[2];
        Assert.Equal(EntryType.Surfaced, third.Opts.EntryType);
        Assert.Contains("auth", third.Opts.Tags ?? Array.Empty<string>());
        Assert.Contains("\"entry_type\":\"surfaced\"", third.Opts.MetadataJson ?? "");

        // Second call: fact → Surfaced, metadata contains "surfaced"
        var second = store.InsertWithEmbeddingCalls[1];
        Assert.Equal(EntryType.Surfaced, second.Opts.EntryType);
        Assert.Contains("\"entry_type\":\"surfaced\"", second.Opts.MetadataJson ?? "");

        // Embedder called once per fact
        Assert.Equal(3, embedder.Calls.Count);
    }

    // -------------------------------------------------------------------------
    // Test 2: duplicate content is skipped and counted
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Extract_DuplicateContent_SkipsAndCounts()
    {
        var (handler, store, _) = MakeHandler();

        // Seed an existing entry so FindByContent returns "existing-id"
        var existing = EntryBuilder.Build("existing-id", "already stored");
        store.Seed(Tier.Hot, ContentType.Memory, existing);

        var result = await handler.ExecuteAsync(ParseArgs("""
            {
              "source": "test",
              "facts": [{"type": "fact", "content": "already stored"}]
            }
            """), CancellationToken.None);

        Assert.False(result.IsError);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text,
            JsonContext.Default.MemoryExtractResultDto);
        Assert.NotNull(dto);
        Assert.Equal(0, dto!.Stored);
        Assert.Equal(1, dto.DuplicatesSkipped);
        Assert.Empty(dto.Entries);

        // No insert should have been recorded
        Assert.Empty(store.InsertWithEmbeddingCalls);
    }

    // -------------------------------------------------------------------------
    // Test 3: unknown fact type throws ArgumentException
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Extract_InvalidFactType_Throws()
    {
        var (handler, store, _) = MakeHandler();

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""
                {"source":"s","facts":[{"type":"opinion","content":"something"}]}
                """), CancellationToken.None));

        Assert.Empty(store.InsertWithEmbeddingCalls);
    }

    // -------------------------------------------------------------------------
    // Test 4: missing source throws ArgumentException
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Extract_MissingSource_Throws()
    {
        var (handler, _, _) = MakeHandler();

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""{"facts":[]}"""), CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // Test 5: empty facts array stores zero
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Extract_EmptyFactsArray_StoresZero()
    {
        var (handler, store, embedder) = MakeHandler();

        var result = await handler.ExecuteAsync(ParseArgs("""
            {"source":"s","facts":[]}
            """), CancellationToken.None);

        Assert.False(result.IsError);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text,
            JsonContext.Default.MemoryExtractResultDto);
        Assert.NotNull(dto);
        Assert.Equal(0, dto!.Stored);
        Assert.Equal(0, dto.DuplicatesSkipped);
        Assert.Empty(dto.Entries);
        Assert.Empty(store.InsertWithEmbeddingCalls);
        Assert.Empty(embedder.Calls);
    }

    // -------------------------------------------------------------------------
    // Test 6: invalid fact mid-array — nothing stored (validate-all-before-write)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Extract_InvalidFactMidArray_NothingStored()
    {
        var (handler, store, _) = MakeHandler();

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(ParseArgs("""
                {
                  "source": "s",
                  "facts": [
                    {"type": "decision", "content": "valid first fact"},
                    {"type": "opinion",  "content": "bad second fact"}
                  ]
                }
                """), CancellationToken.None));

        // Validate-all-before-write: zero inserts even though the first fact was valid
        Assert.Empty(store.InsertWithEmbeddingCalls);
    }
}

/// <summary>
/// Minimal Entry builder for tests that only need an id+content.
/// </summary>
internal static class EntryBuilder
{
    public static Entry Build(string id, string content)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Entry(
            id, content,
            Microsoft.FSharp.Core.FSharpOption<string>.None,
            Microsoft.FSharp.Core.FSharpOption<string>.None,
            Microsoft.FSharp.Core.FSharpOption<SourceTool>.None,
            Microsoft.FSharp.Core.FSharpOption<string>.None,
            Microsoft.FSharp.Collections.ListModule.OfSeq(Array.Empty<string>()),
            now, now, now, 0, 1.0,
            Microsoft.FSharp.Core.FSharpOption<string>.None,
            Microsoft.FSharp.Core.FSharpOption<string>.None,
            "local", EntryType.Surfaced, "{}", 0);
    }
}
