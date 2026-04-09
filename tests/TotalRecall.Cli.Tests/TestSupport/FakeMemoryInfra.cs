// tests/TotalRecall.Cli.Tests/TestSupport/FakeMemoryInfra.cs
//
// Plan 5 Task 5.4 — thin test doubles for ISqliteStore / IVectorSearch /
// IEmbedder used by the memory admin CLI verb tests. Deliberately minimal:
// only the methods actually invoked by promote/demote/inspect are
// implemented; the rest throw NotImplementedException so accidental
// surface expansion fails loudly.

using System;
using System.Collections.Generic;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Tests.TestSupport;

internal sealed class FakeSqliteStore : ISqliteStore
{
    private readonly Dictionary<(Tier, ContentType, string), Entry> _rows = new();
    private readonly Dictionary<(Tier, ContentType, string), long> _rowids = new();
    public List<(Tier FromTier, ContentType FromType, Tier ToTier, ContentType ToType, string Id)> MoveCalls { get; } = new();
    public List<(Tier Tier, ContentType Type, InsertEntryOpts Opts, string NewId)> InsertCalls { get; } = new();
    public List<(Tier Tier, ContentType Type, string Id)> DeleteCalls { get; } = new();
    private int _nextInsertId = 0;
    private long _nextRowid = 1;

    public void Seed(Tier tier, ContentType type, Entry e)
    {
        _rows[(tier, type, e.Id)] = e;
        if (!_rowids.ContainsKey((tier, type, e.Id)))
            _rowids[(tier, type, e.Id)] = _nextRowid++;
    }

    public Entry? Get(Tier tier, ContentType type, string id)
    {
        return _rows.TryGetValue((tier, type, id), out var e) ? e : null;
    }

    public long? GetRowid(Tier tier, ContentType type, string id)
    {
        return _rowids.TryGetValue((tier, type, id), out var r) ? r : null;
    }

    public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id)
    {
        MoveCalls.Add((fromTier, fromType, toTier, toType, id));
        if (!_rows.TryGetValue((fromTier, fromType, id), out var e))
            throw new InvalidOperationException($"no row at {fromTier}/{fromType}/{id}");
        _rows.Remove((fromTier, fromType, id));
        _rows[(toTier, toType, id)] = e;
        if (_rowids.Remove((fromTier, fromType, id)))
            _rowids[(toTier, toType, id)] = _nextRowid++;
    }

    public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
    {
        var results = new List<Entry>();
        foreach (var kvp in _rows)
        {
            if (kvp.Key.Item1.Equals(tier) && kvp.Key.Item2.Equals(type))
                results.Add(kvp.Value);
        }
        return results;
    }

    public string Insert(Tier tier, ContentType type, InsertEntryOpts opts)
    {
        var newId = $"new-{++_nextInsertId}";
        InsertCalls.Add((tier, type, opts, newId));
        var entry = EntryFactory.Make(
            id: newId,
            content: opts.Content,
            summary: opts.Summary,
            source: opts.Source,
            project: opts.Project,
            tags: opts.Tags,
            metadataJson: opts.MetadataJson ?? "");
        _rows[(tier, type, newId)] = entry;
        _rowids[(tier, type, newId)] = _nextRowid++;
        return newId;
    }

    public void Delete(Tier tier, ContentType type, string id)
    {
        DeleteCalls.Add((tier, type, id));
        _rows.Remove((tier, type, id));
        _rowids.Remove((tier, type, id));
    }

    // Minimal substring-based metadata filter for test harness coverage.
    // NOT production-grade json_extract — good enough to let kb list tests
    // filter collections by {"type":"collection"} via string matching on
    // the metadata JSON.
    public IReadOnlyList<Entry> ListByMetadata(
        Tier tier,
        ContentType type,
        IReadOnlyDictionary<string, string> metadataFilter,
        ListEntriesOpts? opts = null)
    {
        var results = new List<Entry>();
        foreach (var kvp in _rows)
        {
            if (!kvp.Key.Item1.Equals(tier) || !kvp.Key.Item2.Equals(type)) continue;
            var meta = kvp.Value.MetadataJson ?? "";
            var allMatch = true;
            foreach (var f in metadataFilter)
            {
                var needle = "\"" + f.Key + "\":\"" + f.Value + "\"";
                if (meta.IndexOf(needle, StringComparison.Ordinal) < 0)
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch) results.Add(kvp.Value);
        }
        return results;
    }

    // Unused surface — throw to catch accidental use.
    public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) => throw new NotImplementedException();

    // Plan 5 Task 5.9 (status) — real Count so the CLI StatusCommand can
    // be driven by a fake store without dragging in real Sqlite. Scans
    // the in-memory row dictionary; O(N) is fine for test fixtures.
    public int Count(Tier tier, ContentType type)
    {
        int n = 0;
        foreach (var kvp in _rows)
        {
            if (kvp.Key.Item1.Equals(tier) && kvp.Key.Item2.Equals(type)) n++;
        }
        return n;
    }

    public int CountKnowledgeCollections() => throw new NotImplementedException();
}

internal sealed class FakeVectorSearch : IVectorSearch
{
    public List<(Tier Tier, ContentType Type, long Rowid)> Deletes { get; } = new();
    public List<(Tier Tier, ContentType Type, string Id, float[] Embedding)> Inserts { get; } = new();

    public void InsertEmbedding(Tier tier, ContentType type, string entryId, ReadOnlyMemory<float> embedding)
    {
        Inserts.Add((tier, type, entryId, embedding.ToArray()));
    }

    public void DeleteEmbedding(Tier tier, ContentType type, long rowid)
    {
        Deletes.Add((tier, type, rowid));
    }

    public IReadOnlyList<VectorSearchResult> SearchByVector(Tier tier, ContentType type, ReadOnlyMemory<float> queryVec, VectorSearchOpts opts) => throw new NotImplementedException();
    public IReadOnlyList<VectorSearchResult> SearchMultipleTiers(IReadOnlyList<(Tier Tier, ContentType Type)> targets, ReadOnlyMemory<float> queryVec, VectorSearchOpts opts) => throw new NotImplementedException();
}

internal sealed class RecordingEmbedder : IEmbedder
{
    public List<string> Calls { get; } = new();
    public float[] Embed(string text)
    {
        Calls.Add(text);
        return new float[384];
    }
}

internal static class EntryFactory
{
    public static Entry Make(
        string id,
        string content = "hello world",
        string? summary = null,
        string? source = null,
        string? project = null,
        IEnumerable<string>? tags = null,
        long createdAt = 1_700_000_000_000L,
        long updatedAt = 1_700_000_000_000L,
        long lastAccessedAt = 1_700_000_000_000L,
        int accessCount = 0,
        double decayScore = 1.0,
        string metadataJson = "",
        string? parentId = null,
        string? collectionId = null)
    {
        return new Entry(
            id,
            content,
            summary is null ? FSharpOption<string>.None : FSharpOption<string>.Some(summary),
            source is null ? FSharpOption<string>.None : FSharpOption<string>.Some(source),
            FSharpOption<SourceTool>.None,
            project is null ? FSharpOption<string>.None : FSharpOption<string>.Some(project),
            ListModule.OfSeq(tags ?? Array.Empty<string>()),
            createdAt, updatedAt, lastAccessedAt, accessCount, decayScore,
            parentId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(parentId),
            collectionId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(collectionId),
            metadataJson);
    }
}
