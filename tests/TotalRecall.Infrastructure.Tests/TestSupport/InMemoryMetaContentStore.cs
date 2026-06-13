using System;
using System.Collections.Generic;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>
/// <see cref="IStore"/> + <see cref="IMetaStore"/> double that, unlike
/// <see cref="InMemoryMetaOnlyStore"/>, returns content rows from
/// <see cref="List"/> so the postgres-path <c>EnsureCompatiblePostgres</c>
/// tests can exercise the "unstamped but POPULATED" branch (where
/// <c>IndexIsEmpty</c> must return false and route into the mismatch dispatch).
///
/// <paramref name="rowCount"/> rows are returned for the FIRST queried
/// (tier, type) pair; every other pair returns empty. That is enough for
/// <c>IndexIsEmpty</c>'s short-circuit (<c>.Any()</c> on the first non-empty
/// pair). All other content methods throw so an accidental data-path call
/// fails loudly.
/// </summary>
public sealed class InMemoryMetaContentStore : IStore, IMetaStore
{
    private readonly Dictionary<string, string> _meta = new();
    private readonly int _rowCount;
    private bool _served;

    public InMemoryMetaContentStore(int rowCount = 1) => _rowCount = rowCount;

    // --- IMetaStore ---
    public string? GetMeta(string key) => _meta.TryGetValue(key, out var v) ? v : null;
    public void SetMeta(string key, string value) => _meta[key] = value;

    // --- IStore.List: only method EnsureCompatiblePostgres/IndexIsEmpty reach ---
    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
    {
        // Serve the configured rows once (the first pair IndexIsEmpty queries),
        // empty thereafter — IndexIsEmpty short-circuits on the first non-empty.
        if (_served)
        {
            return Array.Empty<Entry>();
        }
        _served = true;

        var rows = new List<Entry>();
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
        new NotSupportedException("InMemoryMetaContentStore only implements _meta + List.");

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts) => throw NotUsed();
    public string InsertWithEmbedding(Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding) => throw NotUsed();
    public Entry? Get(Tier tier, ContentType type, string id) => throw NotUsed();
    public long? GetInternalKey(Tier tier, ContentType type, string id) => throw NotUsed();
    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) => throw NotUsed();
    public void Delete(Tier tier, ContentType type, string id) => throw NotUsed();
    public int Count(Tier tier, ContentType type) => throw NotUsed();
    public int CountKnowledgeCollections() => throw NotUsed();
    public IReadOnlyList<Entry> ListByMetadata(Tier tier, ContentType type, IReadOnlyDictionary<string, string> metadataFilter, ListEntriesOpts? opts = null) => throw NotUsed();
    public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id) => throw NotUsed();
    public string? FindByContent(Tier tier, ContentType type, string content) => throw NotUsed();
    public void UpdateInjectionCounts(IReadOnlyList<(Tier tier, ContentType type, string id)> entries) => throw NotUsed();
}
