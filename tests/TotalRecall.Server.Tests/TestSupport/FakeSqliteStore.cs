// Lightweight ISqliteStore fake for Plan 4 handler tests. Records calls to
// Insert/Update/Delete and supports a pre-seeded Get lookup table keyed by
// (tier, type, id). Methods that individual handler tests do not exercise
// throw NotImplementedException so accidental dependencies show up loudly.
//
// Tasks 4.6/4.7 used only Insert; Task 4.8 (get/update/delete handlers)
// needs a functioning Get, Update, and Delete surface.

using System;
using System.Collections.Generic;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Tests.TestSupport;

public sealed class FakeSqliteStore : ISqliteStore
{
    public sealed record InsertCall(Tier Tier, ContentType Type, InsertEntryOpts Opts);
    public sealed record UpdateCall(Tier Tier, ContentType Type, string Id, UpdateEntryOpts Opts);
    public sealed record DeleteCall(Tier Tier, ContentType Type, string Id);
    public sealed record GetCall(Tier Tier, ContentType Type, string Id);

    public List<InsertCall> InsertCalls { get; } = new();
    public List<UpdateCall> UpdateCalls { get; } = new();
    public List<DeleteCall> DeleteCalls { get; } = new();
    public List<GetCall> GetCalls { get; } = new();

    public string NextInsertId { get; set; } = "entry-123";

    /// <summary>
    /// Pre-seeded entries keyed by (tier, type, id). Tests call Seed(...)
    /// to place rows before invoking a handler.
    /// </summary>
    public Dictionary<(Tier, ContentType, string), Entry> Entries { get; } = new();

    public void Seed(Tier tier, ContentType type, Entry entry)
    {
        Entries[(tier, type, entry.Id)] = entry;
    }

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts)
    {
        InsertCalls.Add(new InsertCall(tier, type, opts));
        return NextInsertId;
    }

    public Entry? Get(Tier tier, ContentType type, string id)
    {
        GetCalls.Add(new GetCall(tier, type, id));
        return Entries.TryGetValue((tier, type, id), out var e) ? e : null;
    }

    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts)
    {
        UpdateCalls.Add(new UpdateCall(tier, type, id, opts));
    }

    public void Delete(Tier tier, ContentType type, string id)
    {
        DeleteCalls.Add(new DeleteCall(tier, type, id));
        Entries.Remove((tier, type, id));
    }

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
