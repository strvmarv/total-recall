// Lightweight ISqliteStore fake for Plan 4 handler tests. Records calls to
// Insert (the only method the MemoryStoreHandler exercises) and throws on
// every other surface so an accidental dependency on unrelated state shows
// up loudly. Promoted to TestSupport/ because Tasks 4.7/4.8 will reuse it.

using System;
using System.Collections.Generic;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Tests.TestSupport;

public sealed class FakeSqliteStore : ISqliteStore
{
    public sealed record InsertCall(Tier Tier, ContentType Type, InsertEntryOpts Opts);

    public List<InsertCall> InsertCalls { get; } = new();
    public string NextInsertId { get; set; } = "entry-123";

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts)
    {
        InsertCalls.Add(new InsertCall(tier, type, opts));
        return NextInsertId;
    }

    public Entry? Get(Tier tier, ContentType type, string id) =>
        throw new NotImplementedException();

    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) =>
        throw new NotImplementedException();

    public void Delete(Tier tier, ContentType type, string id) =>
        throw new NotImplementedException();

    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null) =>
        throw new NotImplementedException();

    public int Count(Tier tier, ContentType type) =>
        throw new NotImplementedException();

    public int CountKnowledgeCollections() =>
        throw new NotImplementedException();

    public IReadOnlyList<Entry> ListByMetadata(
        Tier tier,
        ContentType type,
        IReadOnlyDictionary<string, string> metadataFilter,
        ListEntriesOpts? opts = null) =>
        throw new NotImplementedException();

    public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id) =>
        throw new NotImplementedException();
}
