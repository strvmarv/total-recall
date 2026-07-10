using System;
using System.Collections.Generic;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>
/// <see cref="IStore"/> + <see cref="IMetaStore"/> double that, unlike
/// <see cref="InMemoryMetaOnlyStore"/>, models a POPULATED index so the
/// postgres-path <c>EnsureCompatiblePostgres</c> tests can exercise the
/// "unstamped but populated" branch (where <c>IndexIsEmpty</c> must return
/// false and route into the mismatch dispatch).
///
/// <c>Count</c> — what <c>IndexIsEmpty</c> actually calls — reports
/// <paramref name="rowCount"/> for EVERY (tier, type) pair, so the index reads
/// as non-empty regardless of which pair is queried first (no ordering coupling
/// to <c>TierNames.AllTablePairs</c>). <c>List</c> is kept consistent with that
/// count. The remaining content methods throw so an accidental data-path call
/// fails loudly.
/// </summary>
public sealed class InMemoryMetaContentStore : IStore, IMetaStore
{
    private readonly Dictionary<string, string> _meta = new();
    private readonly int _rowCount;

    public InMemoryMetaContentStore(int rowCount = 1) => _rowCount = rowCount;

    // --- IMetaStore ---
    public string? GetMeta(string key) => _meta.TryGetValue(key, out var v) ? v : null;
    public void SetMeta(string key, string value) => _meta[key] = value;
    public void DeleteMeta(string key) => _meta.Remove(key);

    // --- IStore surface IndexIsEmpty reaches: Count (primary) + List (parity) ---
    public int Count(Tier tier, ContentType type) => _rowCount;

    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
    {
        var rows = new List<Entry>(_rowCount);
        for (var i = 0; i < _rowCount; i++)
        {
            rows.Add(MakeEntry($"row-{i}", $"content-{i}"));
        }
        return rows;
    }

    private static Entry MakeEntry(string id, string content)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Entry(
            id, content,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.OfSeq(Array.Empty<string>()),
            now, now, now, 0, 1.0,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            "local", EntryType.Surfaced, "{}", 0);
    }

    // --- IStore: not reached by the postgres mismatch policy; fail loudly ---
    private static Exception NotUsed() =>
        new NotSupportedException("InMemoryMetaContentStore only implements _meta + Count/List.");

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts) => throw NotUsed();
    public string InsertWithEmbedding(Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding) => throw NotUsed();
    public Entry? Get(Tier tier, ContentType type, string id) => throw NotUsed();
    public long? GetInternalKey(Tier tier, ContentType type, string id) => throw NotUsed();
    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) => throw NotUsed();
    public void Delete(Tier tier, ContentType type, string id) => throw NotUsed();
    public int CountKnowledgeCollections() => throw NotUsed();
    public IReadOnlyList<Entry> ListByMetadata(Tier tier, ContentType type, IReadOnlyDictionary<string, string> metadataFilter, ListEntriesOpts? opts = null) => throw NotUsed();
    public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id) => throw NotUsed();
    public string? FindByContent(Tier tier, ContentType type, string content) => throw NotUsed();
    public void UpdateInjectionCounts(IReadOnlyList<(Tier tier, ContentType type, string id)> entries) => throw NotUsed();
    public void SetSticky(ContentType type, string id, bool sticky) => throw NotUsed();
    public bool IsSticky(ContentType type, string id) => false;
}
