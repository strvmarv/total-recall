using System;
using System.Collections.Generic;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Tests.TestSupport;

/// <summary>
/// Minimal <see cref="IStore"/> + <see cref="IMetaStore"/> double for the
/// postgres-path <see cref="TotalRecall.Infrastructure.Embedding.EmbedderMigration.EnsureCompatiblePostgres"/>
/// tests. Only the <c>_meta</c> surface is exercised there (via the internal
/// <c>(IMetaStore)store</c> cast in <c>EmbedderFingerprint.Check</c>/<c>Restamp</c>);
/// every content method throws so any accidental data-path call fails loudly
/// instead of silently no-op'ing.
/// </summary>
public sealed class InMemoryMetaOnlyStore : IStore, IMetaStore
{
    private readonly Dictionary<string, string> _meta = new();

    // --- IMetaStore (the only surface EnsureCompatiblePostgres touches) ---
    public string? GetMeta(string key) => _meta.TryGetValue(key, out var v) ? v : null;
    public void SetMeta(string key, string value) => _meta[key] = value;

    // --- IStore: not reached by the postgres mismatch policy; fail loudly ---
    private static Exception NotUsed() =>
        new NotSupportedException("InMemoryMetaOnlyStore only implements the _meta surface.");

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts) => throw NotUsed();
    public string InsertWithEmbedding(Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding) => throw NotUsed();
    public Entry? Get(Tier tier, ContentType type, string id) => throw NotUsed();
    public long? GetInternalKey(Tier tier, ContentType type, string id) => throw NotUsed();
    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) => throw NotUsed();
    public void Delete(Tier tier, ContentType type, string id) => throw NotUsed();
    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null) => throw NotUsed();
    public int Count(Tier tier, ContentType type) => throw NotUsed();
    public int CountKnowledgeCollections() => throw NotUsed();
    public IReadOnlyList<Entry> ListByMetadata(Tier tier, ContentType type, IReadOnlyDictionary<string, string> metadataFilter, ListEntriesOpts? opts = null) => throw NotUsed();
    public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id) => throw NotUsed();
    public string? FindByContent(Tier tier, ContentType type, string content) => throw NotUsed();
    public void UpdateInjectionCounts(IReadOnlyList<(Tier tier, ContentType type, string id)> entries) => throw NotUsed();
}
