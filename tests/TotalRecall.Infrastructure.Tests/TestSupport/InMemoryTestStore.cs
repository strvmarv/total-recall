// tests/TotalRecall.Infrastructure.Tests/TestSupport/InMemoryTestStore.cs
//
// Minimal mutating in-memory IStore for HotTierCompactorTests. Unlike the
// read-only stubs in this folder, this one actually moves entries between
// tiers so compaction tests can assert post-Move counts.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

public sealed class InMemoryTestStore : IStore
{
    private readonly Dictionary<(Tier, ContentType), List<Entry>> _store = new();

    private List<Entry> Slot(Tier tier, ContentType type)
    {
        if (!_store.TryGetValue((tier, type), out var list))
            _store[(tier, type)] = list = new List<Entry>();
        return list;
    }

    /// <summary>Add an entry to the given tier/type slot.</summary>
    public void Seed(Tier tier, ContentType type, Entry entry) =>
        Slot(tier, type).Add(entry);

    // --- IStore members used by HotTierCompactor ---

    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null) =>
        Slot(tier, type).ToList(); // snapshot

    public int Count(Tier tier, ContentType type) =>
        Slot(tier, type).Count;

    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts)
    {
        var slot = Slot(tier, type);
        var idx = slot.FindIndex(e => e.Id == id);
        if (idx < 0)
            throw new InvalidOperationException($"entry '{id}' not found in {tier}/{type}");
        var e = slot[idx];
        slot[idx] = new Entry(
            e.Id, e.Content, e.Summary, e.Source, e.SourceTool, e.Project,
            e.Tags, e.CreatedAt, e.UpdatedAt, e.LastAccessedAt, e.AccessCount,
            opts.DecayScore ?? e.DecayScore,
            e.ParentId, e.CollectionId, e.Scope, e.EntryType, e.MetadataJson, e.TimesInjected);
    }

    public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id)
    {
        var src = Slot(fromTier, fromType);
        var entry = src.FirstOrDefault(e => e.Id == id)
            ?? throw new InvalidOperationException($"entry '{id}' not found in {fromTier}/{fromType}");
        src.Remove(entry);
        Slot(toTier, toType).Add(entry);
    }

    // --- Stubs for the rest of IStore ---

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts) =>
        throw new NotSupportedException();

    public string InsertWithEmbedding(Tier tier, ContentType type, InsertEntryOpts opts,
        ReadOnlyMemory<float> embedding) =>
        throw new NotSupportedException();

    public Entry? Get(Tier tier, ContentType type, string id) =>
        Slot(tier, type).FirstOrDefault(e => e.Id == id);

    public long? GetInternalKey(Tier tier, ContentType type, string id) =>
        throw new NotSupportedException();

    public void Delete(Tier tier, ContentType type, string id) =>
        Slot(tier, type).RemoveAll(e => e.Id == id);

    public int CountKnowledgeCollections() =>
        throw new NotSupportedException();

    public IReadOnlyList<Entry> ListByMetadata(Tier tier, ContentType type,
        IReadOnlyDictionary<string, string> metadataFilter, ListEntriesOpts? opts = null) =>
        throw new NotSupportedException();

    public string? FindByContent(Tier tier, ContentType type, string content) =>
        throw new NotSupportedException();

    public void UpdateInjectionCounts(
        IReadOnlyList<(Tier tier, ContentType type, string id)> entries) =>
        throw new NotSupportedException();
}
