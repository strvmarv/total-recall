// tests/TotalRecall.Server.Tests/SessionEndHandlerTests.cs
//
// Tests for SessionEndHandler. Two groups:
//
//   Stub shape (no store injected) — original Plan 4 tests preserved.
//   These verify backward compatibility: handler returns zeroed counts
//   when constructed without a store.
//
//   Real compaction (store injected) — verify heuristic decay-based
//   promotion of hot → warm entries below warm_threshold.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
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

public sealed class SessionEndHandlerTests
{
    // ---- helpers ----

    private static Entry MakeEntry(
        string id,
        string content,
        double decayScore = 1.0,
        int accessCount = 0,
        long lastAccessedAt = 0,
        EntryType? entryType = null)
    {
        return new Entry(
            id, content,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            createdAt: 0,
            updatedAt: 0,
            lastAccessedAt: lastAccessedAt,
            accessCount: accessCount,
            decayScore: decayScore,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            scope: "",
            entryType: entryType ?? EntryType.Preference,
            metadataJson: "{}");
    }

    private sealed class FakeStore : IStore
    {
        public Dictionary<(Tier, ContentType), List<Entry>> Entries { get; } = new();
        public List<(string id, UpdateEntryOpts opts)> Updates { get; } = new();

        private List<Entry> Slot(Tier t, ContentType ct)
        {
            if (!Entries.TryGetValue((t, ct), out var list))
                Entries[(t, ct)] = list = new List<Entry>();
            return list;
        }

        public string Insert(Tier t, ContentType ct, InsertEntryOpts o) => throw new NotSupportedException();
        public string InsertWithEmbedding(Tier t, ContentType ct, InsertEntryOpts o, ReadOnlyMemory<float> e) => throw new NotSupportedException();
        public Entry? Get(Tier t, ContentType ct, string id) => Slot(t, ct).FirstOrDefault(e => e.Id == id);
        public long? GetInternalKey(Tier t, ContentType ct, string id) => null;

        public void Update(Tier t, ContentType ct, string id, UpdateEntryOpts opts)
        {
            Updates.Add((id, opts));
            var slot = Slot(t, ct);
            var idx = slot.FindIndex(e => e.Id == id);
            if (idx < 0) throw new InvalidOperationException($"entry {id} not found");
            var e = slot[idx];
            slot[idx] = new Entry(e.Id, e.Content, e.Summary, e.Source, e.SourceTool, e.Project,
                e.Tags, e.CreatedAt, e.UpdatedAt, e.LastAccessedAt, e.AccessCount,
                opts.DecayScore ?? e.DecayScore,
                e.ParentId, e.CollectionId, e.Scope, e.EntryType, e.MetadataJson);
        }

        public void Delete(Tier t, ContentType ct, string id) => Slot(t, ct).RemoveAll(e => e.Id == id);

        public IReadOnlyList<Entry> List(Tier t, ContentType ct, ListEntriesOpts? opts = null)
            => Slot(t, ct).ToList(); // snapshot — matches SQL store behaviour

        public int Count(Tier t, ContentType ct) => Slot(t, ct).Count;
        public int CountKnowledgeCollections() => 0;

        public IReadOnlyList<Entry> ListByMetadata(Tier t, ContentType ct,
            IReadOnlyDictionary<string, string> filter, ListEntriesOpts? opts = null)
            => Array.Empty<Entry>();

        public void Move(Tier fromT, ContentType fromCt, Tier toT, ContentType toCt, string id)
        {
            var src = Slot(fromT, fromCt);
            var entry = src.FirstOrDefault(e => e.Id == id)
                ?? throw new InvalidOperationException($"entry {id} not found in source tier");
            src.Remove(entry);
            Slot(toT, toCt).Add(entry);
        }

        public string? FindByContent(Tier tier, ContentType type, string content)
            => Slot(tier, type).FirstOrDefault(e => e.Content == content)?.Id;
    }

    // ---- stub shape (no store) ----

    [Fact]
    public async Task HappyPath_ReturnsStubResponse()
    {
        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "sess-end-1" };
        var handler = new SessionEndHandler(lifecycle);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.False(result.IsError ?? false);
        Assert.Single(result.Content);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal("sess-end-1", root.GetProperty("sessionId").GetString());
        Assert.Equal(0, root.GetProperty("carryForward").GetInt32());
        Assert.Equal(0, root.GetProperty("promoted").GetInt32());
        Assert.Equal(0, root.GetProperty("discarded").GetInt32());
    }

    [Fact]
    public async Task NullArguments_DoesNotThrow()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionEndHandler(lifecycle);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task EmptyObjectArguments_DoesNotThrow()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionEndHandler(lifecycle);

        using var doc = JsonDocument.Parse("{}");
        var result = await handler.ExecuteAsync(doc.RootElement, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task SessionId_FromLifecycle()
    {
        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "unique-id-xyz" };
        var handler = new SessionEndHandler(lifecycle);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("unique-id-xyz", doc.RootElement.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task DoesNotCall_EnsureInitializedAsync()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionEndHandler(lifecycle);

        await handler.ExecuteAsync(null, CancellationToken.None);

        Assert.Equal(0, lifecycle.EnsureInitializedCallCount);
    }

    [Fact]
    public void Metadata_NameAndDescription()
    {
        var lifecycle = new FakeSessionLifecycle();
        var handler = new SessionEndHandler(lifecycle);

        Assert.Equal("session_end", handler.Name);
        Assert.Contains("End a session", handler.Description);
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
    }

    // ---- real compaction ----

    [Fact]
    public async Task Compaction_PromotesEntriesBelowThreshold()
    {
        var store = new FakeStore();
        // Entry with a very old last-accessed timestamp → low decay score.
        // Use lastAccessedAt = 0 (epoch) so hours-since-access is enormous → score ≈ 0.
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("stale",  "stale",  lastAccessedAt: 0),
            MakeEntry("fresh",  "fresh",  lastAccessedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };

        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "s1" };
        // warmThreshold=0.5, decayConstantHours=168. The stale entry will have
        // a score near 0; the fresh entry ≥ 1.0 (freqFactor ≥ 1, timeFactor ≈ 1).
        var handler = new SessionEndHandler(lifecycle, store, warmThreshold: 0.5, decayConstantHours: 168);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("promoted").GetInt32());
        Assert.Equal(0, root.GetProperty("discarded").GetInt32());
        Assert.Equal(1, store.Count(Tier.Hot, ContentType.Memory));
        Assert.Equal(1, store.Count(Tier.Warm, ContentType.Memory));
        Assert.Equal("stale", store.Entries[(Tier.Warm, ContentType.Memory)][0].Id);
    }

    [Fact]
    public async Task Compaction_CarryForwardCount_ReflectsRemainingHot()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("a", "a", lastAccessedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            MakeEntry("b", "b", lastAccessedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        };

        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "s2" };
        var handler = new SessionEndHandler(lifecycle, store, warmThreshold: 0.01, decayConstantHours: 168);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(2, doc.RootElement.GetProperty("carryForward").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("promoted").GetInt32());
    }

    [Fact]
    public async Task Compaction_UpdatesStoredDecayScore()
    {
        var store = new FakeStore();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("x", "x", lastAccessedAt: nowMs),
        };

        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "s3" };
        var handler = new SessionEndHandler(lifecycle, store, warmThreshold: 0.01, decayConstantHours: 168);

        await handler.ExecuteAsync(null, CancellationToken.None);

        // Update should have been called once, with a fresh decay score.
        Assert.Single(store.Updates);
        Assert.Equal("x", store.Updates[0].id);
        Assert.NotNull(store.Updates[0].opts.DecayScore);
        Assert.True(store.Updates[0].opts.DecayScore > 0);
    }

    [Fact]
    public async Task Compaction_EmptyHotTier_ReturnsZeros()
    {
        var store = new FakeStore();
        var lifecycle = new FakeSessionLifecycle { SessionIdValue = "s4" };
        var handler = new SessionEndHandler(lifecycle, store, warmThreshold: 0.3);

        var result = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(0, doc.RootElement.GetProperty("promoted").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("carryForward").GetInt32());
    }
}
