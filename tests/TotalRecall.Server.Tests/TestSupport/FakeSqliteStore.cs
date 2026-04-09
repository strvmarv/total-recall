// Lightweight ISqliteStore fake for Plan 4 handler tests. Records calls to
// Insert/Update/Delete and supports a pre-seeded Get lookup table keyed by
// (tier, type, id). Methods that individual handler tests do not exercise
// throw NotImplementedException so accidental dependencies show up loudly.
//
// Tasks 4.6/4.7 used only Insert; Task 4.8 (get/update/delete handlers)
// needs a functioning Get, Update, and Delete surface.

using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Synthetic rowid assigned to each seeded or inserted entry so
    /// <see cref="GetRowid"/> can return a stable value that behaves
    /// like the SQLite-allocated rowid in production. Monotonic; reused
    /// after deletion isn't modeled because no handler test needs it.
    /// </summary>
    public Dictionary<(Tier, ContentType, string), long> Rowids { get; } = new();
    private long _nextRowid = 1;

    /// <summary>
    /// Ordered entry lists per (tier, type) slot. Populated via
    /// <see cref="SeedList"/>; consumed by <see cref="List"/>. Kept separate
    /// from the id-keyed <see cref="Entries"/> dictionary so tests that only
    /// exercise Get/Insert/Delete are unaffected.
    /// </summary>
    public Dictionary<(Tier, ContentType), List<Entry>> ListSlots { get; } = new();

    public void Seed(Tier tier, ContentType type, Entry entry)
    {
        Entries[(tier, type, entry.Id)] = entry;
        if (!Rowids.ContainsKey((tier, type, entry.Id)))
            Rowids[(tier, type, entry.Id)] = _nextRowid++;
    }

    public void SeedList(Tier tier, ContentType type, params Entry[] entries)
    {
        if (!ListSlots.TryGetValue((tier, type), out var slot))
        {
            slot = new List<Entry>();
            ListSlots[(tier, type)] = slot;
        }
        slot.AddRange(entries);
        // Assign synthetic rowids so GetRowid works for SeedList-only entries
        // (KbRefreshHandlerTests and similar exercise children that are only
        // populated via SeedList, not Seed).
        foreach (var e in entries)
        {
            if (!Rowids.ContainsKey((tier, type, e.Id)))
                Rowids[(tier, type, e.Id)] = _nextRowid++;
        }
    }

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts)
    {
        InsertCalls.Add(new InsertCall(tier, type, opts));
        Rowids[(tier, type, NextInsertId)] = _nextRowid++;
        return NextInsertId;
    }

    public sealed record InsertWithEmbeddingCall(Tier Tier, ContentType Type, InsertEntryOpts Opts, float[] Embedding);
    public List<InsertWithEmbeddingCall> InsertWithEmbeddingCalls { get; } = new();

    public string InsertWithEmbedding(
        Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding)
    {
        InsertWithEmbeddingCalls.Add(new InsertWithEmbeddingCall(tier, type, opts, embedding.ToArray()));
        // Mirror the real store: allocate a rowid under the same monotonic
        // counter so GetRowid works on the fresh id. Does NOT model
        // transactional rollback — tests that need to exercise the rollback
        // path use the real SqliteStore against :memory:.
        Rowids[(tier, type, NextInsertId)] = _nextRowid++;
        return NextInsertId;
    }

    public Entry? Get(Tier tier, ContentType type, string id)
    {
        GetCalls.Add(new GetCall(tier, type, id));
        return Entries.TryGetValue((tier, type, id), out var e) ? e : null;
    }

    public long? GetRowid(Tier tier, ContentType type, string id)
    {
        return Rowids.TryGetValue((tier, type, id), out var r) ? r : null;
    }

    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts)
    {
        UpdateCalls.Add(new UpdateCall(tier, type, id, opts));
    }

    public void Delete(Tier tier, ContentType type, string id)
    {
        DeleteCalls.Add(new DeleteCall(tier, type, id));
        OrderLog?.Add("store.Delete");
        Entries.Remove((tier, type, id));
        Rowids.Remove((tier, type, id));
    }

    /// <summary>
    /// Opt-in cross-fake call-order log. When set (by a test), deletion
    /// methods on this fake and any sibling <see cref="FakeVectorSearch"/>
    /// sharing the same list append a tag each time they are invoked, so
    /// the test can assert relative ordering of operations across the
    /// store + vec boundary. Left null by default so existing tests see
    /// no behavior change.
    /// </summary>
    public List<string>? OrderLog { get; set; }

    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
    {
        if (!ListSlots.TryGetValue((tier, type), out var slot))
            return Array.Empty<Entry>();
        IEnumerable<Entry> src = slot;
        if (opts?.Limit is int lim) src = src.Take(lim);
        return src.ToList();
    }

    /// <summary>
    /// Pre-seeded counts per (tier, type). Consumed by <see cref="Count"/>.
    /// Task 4.11 StatusHandler relies on this. Defaults to 0 when not seeded.
    /// </summary>
    public Dictionary<(Tier, ContentType), int> Counts { get; } = new();

    /// <summary>
    /// Pre-seeded metadata-filtered slots. Key includes a sorted-string
    /// rendering of the filter so multiple filters on the same (tier, type)
    /// can coexist. Task 4.11 StatusHandler uses a
    /// <c>{"type":"collection"}</c> filter against cold_knowledge.
    /// </summary>
    public Dictionary<(Tier, ContentType, string), List<Entry>> ListByMetadataSlots { get; } = new();

    public void SeedCount(Tier tier, ContentType type, int count)
    {
        Counts[(tier, type)] = count;
    }

    public void SeedListByMetadata(
        Tier tier,
        ContentType type,
        IReadOnlyDictionary<string, string> metadataFilter,
        params Entry[] entries)
    {
        var key = (tier, type, FilterKey(metadataFilter));
        if (!ListByMetadataSlots.TryGetValue(key, out var slot))
        {
            slot = new List<Entry>();
            ListByMetadataSlots[key] = slot;
        }
        slot.AddRange(entries);
    }

    public int Count(Tier tier, ContentType type) =>
        Counts.TryGetValue((tier, type), out var n) ? n : 0;

    public int CountKnowledgeCollections() =>
        throw new NotImplementedException();

    public IReadOnlyList<Entry> ListByMetadata(
        Tier tier,
        ContentType type,
        IReadOnlyDictionary<string, string> metadataFilter,
        ListEntriesOpts? opts = null)
    {
        var key = (tier, type, FilterKey(metadataFilter));
        if (!ListByMetadataSlots.TryGetValue(key, out var slot))
            return Array.Empty<Entry>();
        IEnumerable<Entry> src = slot;
        if (opts?.Limit is int lim) src = src.Take(lim);
        return src.ToList();
    }

    private static string FilterKey(IReadOnlyDictionary<string, string> filter)
    {
        var parts = filter
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + kv.Value);
        return string.Join(";", parts);
    }

    public sealed record MoveCall(Tier FromTier, ContentType FromType, Tier ToTier, ContentType ToType, string Id);

    public List<MoveCall> MoveCalls { get; } = new();

    public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id)
    {
        MoveCalls.Add(new MoveCall(fromTier, fromType, toTier, toType, id));
        if (Entries.TryGetValue((fromTier, fromType, id), out var e))
        {
            Entries.Remove((fromTier, fromType, id));
            Entries[(toTier, toType, id)] = e;
        }
        // Mirror production store.Move: the row moves to a new table and
        // therefore gets a new rowid in that table. Reuse the same
        // monotonic counter so tests that compare rowids across moves
        // observe distinct values.
        if (Rowids.Remove((fromTier, fromType, id)))
            Rowids[(toTier, toType, id)] = _nextRowid++;
    }
}
