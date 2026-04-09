using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="HybridSearch"/> using hand-written fakes for
/// the three collaborators (<see cref="IVectorSearch"/>,
/// <see cref="IFtsSearch"/>, <see cref="ISqliteStore"/>). No database.
/// </summary>
public sealed class HybridSearchTests
{
    private static readonly IReadOnlyList<(Tier Tier, ContentType Type)> HotMem =
        new[] { (Tier.Hot, ContentType.Memory) };

    private static readonly ReadOnlyMemory<float> DummyEmbedding =
        new float[] { 1f, 0f, 0f };

    // --- fakes ------------------------------------------------------------

    private sealed class FakeVectorSearch : IVectorSearch
    {
        public Dictionary<(Tier, ContentType), List<VectorSearchResult>> ByTier { get; }
            = new();
        public List<VectorSearchOpts> CapturedOpts { get; } = new();

        public IReadOnlyList<VectorSearchResult> SearchByVector(
            Tier tier,
            ContentType type,
            ReadOnlyMemory<float> queryVec,
            VectorSearchOpts opts)
        {
            CapturedOpts.Add(opts);
            return ByTier.TryGetValue((tier, type), out var list)
                ? list
                : (IReadOnlyList<VectorSearchResult>)Array.Empty<VectorSearchResult>();
        }

        public void InsertEmbedding(Tier tier, ContentType type, string entryId, ReadOnlyMemory<float> embedding)
            => throw new NotImplementedException();
        public void DeleteEmbedding(Tier tier, ContentType type, long rowid)
            => throw new NotImplementedException();
        public IReadOnlyList<VectorSearchResult> SearchMultipleTiers(
            IReadOnlyList<(Tier Tier, ContentType Type)> targets,
            ReadOnlyMemory<float> queryVec,
            VectorSearchOpts opts)
            => throw new NotImplementedException();
    }

    private sealed class FakeFtsSearch : IFtsSearch
    {
        public Dictionary<(Tier, ContentType), List<FtsSearchResult>> ByTier { get; }
            = new();
        public List<FtsSearchOpts> CapturedOpts { get; } = new();

        public IReadOnlyList<FtsSearchResult> SearchByFts(
            Tier tier,
            ContentType type,
            string query,
            FtsSearchOpts opts)
        {
            CapturedOpts.Add(opts);
            return ByTier.TryGetValue((tier, type), out var list)
                ? list
                : (IReadOnlyList<FtsSearchResult>)Array.Empty<FtsSearchResult>();
        }
    }

    private sealed class FakeStore : ISqliteStore
    {
        public Dictionary<string, Entry> Entries { get; } = new();
        public HashSet<string> Touched { get; } = new();
        public int GetCalls { get; private set; }

        public Entry? Get(Tier tier, ContentType type, string id)
        {
            GetCalls++;
            return Entries.TryGetValue(id, out var e) ? e : null;
        }

        public long? GetRowid(Tier tier, ContentType type, string id)
            => Entries.ContainsKey(id) ? 1L : null;

        public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts)
        {
            if (opts.Touch) Touched.Add(id);
        }

        public string Insert(Tier tier, ContentType type, InsertEntryOpts opts)
            => throw new NotImplementedException();
        public void Delete(Tier tier, ContentType type, string id)
            => throw new NotImplementedException();
        public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
            => throw new NotImplementedException();
        public int Count(Tier tier, ContentType type)
            => throw new NotImplementedException();
        public int CountKnowledgeCollections()
            => throw new NotImplementedException();
        public IReadOnlyList<Entry> ListByMetadata(
            Tier tier,
            ContentType type,
            IReadOnlyDictionary<string, string> metadataFilter,
            ListEntriesOpts? opts = null)
            => throw new NotImplementedException();
        public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id)
            => throw new NotImplementedException();
    }

    // --- helpers ----------------------------------------------------------

    private static Entry MakeEntry(string id, string content = "c") =>
        new(
            id,
            content,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            ListModule.Empty<string>(),
            0L,
            0L,
            0L,
            0,
            1.0,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            "{}");

    private static (HybridSearch search, FakeVectorSearch v, FakeFtsSearch f, FakeStore s) NewFixture()
    {
        var v = new FakeVectorSearch();
        var f = new FakeFtsSearch();
        var s = new FakeStore();
        return (new HybridSearch(v, f, s), v, f, s);
    }

    private static void SeedStore(FakeStore s, params string[] ids)
    {
        foreach (var id in ids) s.Entries[id] = MakeEntry(id);
    }

    // --- tests ------------------------------------------------------------

    [Fact]
    public void Search_VectorOnly_ReturnsResultsOrderedByScore()
    {
        var (hs, v, _, s) = NewFixture();
        SeedStore(s, "a", "b", "c");
        v.ByTier[(Tier.Hot, ContentType.Memory)] = new()
        {
            new("a", 0.2),
            new("b", 0.9),
            new("c", 0.5),
        };

        var results = hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 3));

        Assert.Equal(3, results.Count);
        Assert.Equal("b", results[0].Entry.Id);
        Assert.Equal("c", results[1].Entry.Id);
        Assert.Equal("a", results[2].Entry.Id);
        // Fused score == vector score when fts is empty.
        Assert.Equal(0.9, results[0].Score, 10);
    }

    [Fact]
    public void Search_FtsOnly_ReturnsResultsOrderedByFtsWeight()
    {
        var (hs, _, f, s) = NewFixture();
        SeedStore(s, "a", "b");
        f.ByTier[(Tier.Hot, ContentType.Memory)] = new()
        {
            new("a", 0.4),
            new("b", 1.0),
        };

        var results = hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 2));

        Assert.Equal(2, results.Count);
        Assert.Equal("b", results[0].Entry.Id);
        Assert.Equal("a", results[1].Entry.Id);
        // Fused == 0 + 0.3 * fts.
        Assert.Equal(0.3, results[0].Score, 10);
        Assert.Equal(0.12, results[1].Score, 10);
    }

    [Fact]
    public void Search_BothSeams_MergesByIdAndFuses()
    {
        var (hs, v, f, s) = NewFixture();
        SeedStore(s, "A", "B");
        v.ByTier[(Tier.Hot, ContentType.Memory)] = new()
        {
            new("A", 0.5),
            new("B", 0.9),
        };
        f.ByTier[(Tier.Hot, ContentType.Memory)] = new()
        {
            new("A", 0.8),
        };

        var results = hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 5));

        // A = 0.5 + 0.3*0.8 = 0.74; B = 0.9 + 0.3*0 = 0.9
        Assert.Equal(2, results.Count);
        Assert.Equal("B", results[0].Entry.Id);
        Assert.Equal(0.9, results[0].Score, 10);
        Assert.Equal("A", results[1].Entry.Id);
        Assert.Equal(0.74, results[1].Score, 10);
    }

    [Fact]
    public void Search_DuplicateAcrossTiers_KeepsMaxVectorScore()
    {
        var (hs, v, _, s) = NewFixture();
        SeedStore(s, "dup");
        v.ByTier[(Tier.Hot, ContentType.Memory)] = new() { new("dup", 0.3) };
        v.ByTier[(Tier.Warm, ContentType.Memory)] = new() { new("dup", 0.8) };

        var results = hs.Search(
            new[]
            {
                (Tier.Hot, ContentType.Memory),
                (Tier.Warm, ContentType.Memory),
            },
            "q", DummyEmbedding, new HybridSearchOpts(TopK: 5));

        Assert.Single(results);
        Assert.Equal(0.8, results[0].Score, 10);
    }

    [Fact]
    public void Search_DuplicateAcrossTiers_KeepsMaxFtsScore()
    {
        var (hs, _, f, s) = NewFixture();
        SeedStore(s, "dup");
        f.ByTier[(Tier.Hot, ContentType.Memory)] = new() { new("dup", 0.2) };
        f.ByTier[(Tier.Warm, ContentType.Memory)] = new() { new("dup", 0.9) };

        var results = hs.Search(
            new[]
            {
                (Tier.Hot, ContentType.Memory),
                (Tier.Warm, ContentType.Memory),
            },
            "q", DummyEmbedding, new HybridSearchOpts(TopK: 5));

        Assert.Single(results);
        // Max fts = 0.9; fused = 0 + 0.3*0.9 = 0.27
        Assert.Equal(0.27, results[0].Score, 10);
    }

    [Fact]
    public void Search_OversamplesByTwo()
    {
        var (hs, v, f, s) = NewFixture();
        SeedStore(s);
        hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 7));

        Assert.Single(v.CapturedOpts);
        Assert.Equal(14, v.CapturedOpts[0].TopK);
        Assert.Single(f.CapturedOpts);
        Assert.Equal(14, f.CapturedOpts[0].TopK);
    }

    [Fact]
    public void Search_TopKTruncation_RespectsK()
    {
        var (hs, v, _, s) = NewFixture();
        var ids = Enumerable.Range(0, 10).Select(i => $"id{i}").ToArray();
        SeedStore(s, ids);
        v.ByTier[(Tier.Hot, ContentType.Memory)] =
            ids.Select((id, i) => new VectorSearchResult(id, 1.0 - i * 0.01)).ToList();

        var results = hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 3));

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Search_MissingEntryInStore_Skipped()
    {
        var (hs, v, _, s) = NewFixture();
        SeedStore(s, "kept");
        v.ByTier[(Tier.Hot, ContentType.Memory)] = new()
        {
            new("missing", 0.9),
            new("kept", 0.5),
        };

        var results = hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 5));

        Assert.Single(results);
        Assert.Equal("kept", results[0].Entry.Id);
        // Rank resumes from next — first kept entry still gets rank 1.
        Assert.Equal(1, results[0].Rank);
    }

    [Fact]
    public void Search_ReadEntries_TouchesEachOne()
    {
        var (hs, v, _, s) = NewFixture();
        SeedStore(s, "a", "b");
        v.ByTier[(Tier.Hot, ContentType.Memory)] = new()
        {
            new("a", 0.9),
            new("b", 0.5),
        };

        hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 5));

        Assert.Contains("a", s.Touched);
        Assert.Contains("b", s.Touched);
    }

    [Fact]
    public void Search_RanksAssignedInOrder()
    {
        var (hs, v, _, s) = NewFixture();
        SeedStore(s, "a", "b", "c");
        v.ByTier[(Tier.Hot, ContentType.Memory)] = new()
        {
            new("a", 0.9),
            new("b", 0.5),
            new("c", 0.7),
        };

        var results = hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 3));

        Assert.Equal(1, results[0].Rank);
        Assert.Equal(2, results[1].Rank);
        Assert.Equal(3, results[2].Rank);
    }

    [Fact]
    public void Search_DefaultFtsWeight_IsZeroPointThree()
    {
        var (hs, _, f, s) = NewFixture();
        SeedStore(s, "x");
        f.ByTier[(Tier.Hot, ContentType.Memory)] = new() { new("x", 1.0) };

        var results = hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 1));

        Assert.Equal(0.3, results[0].Score, 10);
    }

    [Fact]
    public void Search_CustomFtsWeight_IsRespected()
    {
        var (hs, _, f, s) = NewFixture();
        SeedStore(s, "x");
        f.ByTier[(Tier.Hot, ContentType.Memory)] = new() { new("x", 1.0) };

        var results = hs.Search(
            HotMem, "q", DummyEmbedding,
            new HybridSearchOpts(TopK: 1, FtsWeight: 0.5));

        Assert.Equal(0.5, results[0].Score, 10);
    }

    [Fact]
    public void Search_EmptySeams_ReturnsEmpty()
    {
        var (hs, _, _, s) = NewFixture();

        var results = hs.Search(HotMem, "q", DummyEmbedding, new HybridSearchOpts(TopK: 5));

        Assert.Empty(results);
        Assert.Equal(0, s.GetCalls);
    }

    [Fact]
    public void Search_MinScorePropagation_ReachesVectorSeam()
    {
        var (hs, v, f, s) = NewFixture();
        SeedStore(s);
        hs.Search(
            HotMem, "q", DummyEmbedding,
            new HybridSearchOpts(TopK: 4, MinScore: 0.4));

        Assert.Single(v.CapturedOpts);
        Assert.Equal(0.4, v.CapturedOpts[0].MinScore);
        // FTS must NOT receive any minScore (FtsSearchOpts has no such field).
        Assert.Single(f.CapturedOpts);
    }
}
