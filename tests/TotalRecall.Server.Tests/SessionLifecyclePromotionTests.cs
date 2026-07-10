// tests/TotalRecall.Server.Tests/SessionLifecyclePromotionTests.cs
//
// Task 7 — access-earned warm->hot promotion + sticky-aware eviction in the
// warm sweep. Covers the "decay-default-1.0 auto-promotion trap": a
// brand-new warm entry has decay~1.0 but access_count 0, so promotion must
// require BOTH access_count >= promote_min_access AND
// decay_score >= promote_threshold. Also covers sticky-hot immunity from
// eviction during an over-capacity sweep.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

public sealed class SessionLifecyclePromotionTests
{
    private const long NowMs = 1_000_000_000_000L;

    private sealed class FakeEmbedder : IEmbedder
    {
        public float[] Embed(string text) => new float[] { 0.1f, 0.2f, 0.3f };
        public EmbedderDescriptor Descriptor { get; } =
            new("local", "fake-test-model", "", 3);
    }

    private sealed class FakeVectorSearch : IVectorSearch
    {
        public void InsertEmbedding(Tier tier, ContentType type, string entryId, ReadOnlyMemory<float> embedding) { }
        public void DeleteEmbedding(Tier tier, ContentType type, long rowid) { }
        public IReadOnlyList<VectorSearchResult> SearchByVector(Tier tier, ContentType type, ReadOnlyMemory<float> queryVec, VectorSearchOpts opts)
            => Array.Empty<VectorSearchResult>();
        public IReadOnlyList<VectorSearchResult> SearchMultipleTiers(IReadOnlyList<(Tier Tier, ContentType Type)> targets, ReadOnlyMemory<float> queryVec, VectorSearchOpts opts)
            => Array.Empty<VectorSearchResult>();
    }

    private static Entry MakeEntry(
        string id,
        int accessCount,
        long lastAccessedAt,
        double decayScore = 1.0,
        string content = "content")
    {
        return new Entry(
            id,
            content,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<SourceTool>.None,
            FSharpOption<string>.None,
            Microsoft.FSharp.Collections.ListModule.OfSeq(Array.Empty<string>()),
            lastAccessedAt,      // createdAt
            lastAccessedAt,      // updatedAt
            lastAccessedAt,      // lastAccessedAt
            accessCount,
            decayScore,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            "",
            EntryType.Preference,
            "{}", 0);
    }

    private static (SessionLifecycle Lifecycle, SessionLifecycleTests.FakeStore Store, object Cfg) MakeLifecycle(
        int promoteMinAccess = 5,
        double promoteThreshold = 0.7,
        int maxEntries = 50,
        int hotMaxContentChars = 0)
    {
        var store = new SessionLifecycleTests.FakeStore();
        var lifecycle = new SessionLifecycle(
            Array.Empty<TotalRecall.Infrastructure.Importers.IImporter>(),
            store,
            new FakeCompactionLog(),
            "sess-test",
            () => NowMs,
            storageMode: "sqlite",
            maxEntries: maxEntries,
            embedder: new FakeEmbedder(),
            vec: new FakeVectorSearch(),
            promoteMinAccess: promoteMinAccess,
            promoteThreshold: promoteThreshold,
            hotMaxContentChars: hotMaxContentChars);
        return (lifecycle, store, new object());
    }

    private sealed class FakeCompactionLog : ICompactionLogReader
    {
        public long? GetLastTimestampExcludingReason(string excludedReason) => null;
        public IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null)
            => Array.Empty<CompactionAnalyticsRow>();
        public IReadOnlyList<CompactionMovementRow> GetRecentMovements(int limit)
            => Array.Empty<CompactionMovementRow>();
        public CompactionMovementRow? GetByTargetEntryId(string targetEntryId) => null;
    }

    private static void SeedWarm(SessionLifecycleTests.FakeStore store, string id, int accessCount, long lastAccessed)
    {
        store.Entries.TryGetValue((Tier.Warm, ContentType.Memory), out var list);
        list ??= new List<Entry>();
        list.Add(MakeEntry(id, accessCount, lastAccessed));
        store.Entries[(Tier.Warm, ContentType.Memory)] = list;
    }

    private static void SeedHot(SessionLifecycleTests.FakeStore store, string id, double decay)
    {
        store.Entries.TryGetValue((Tier.Hot, ContentType.Memory), out var list);
        list ??= new List<Entry>();
        list.Add(MakeEntry(id, accessCount: 0, lastAccessedAt: NowMs, decayScore: decay));
        store.Entries[(Tier.Hot, ContentType.Memory)] = list;
    }

    private static void SeedStickyHot(SessionLifecycleTests.FakeStore store, string id, double decay)
    {
        SeedHot(store, id, decay);
        store.SetSticky(ContentType.Memory, id, true);
    }

    private static int ExistsInHot(SessionLifecycleTests.FakeStore store, string id) =>
        store.Entries.TryGetValue((Tier.Hot, ContentType.Memory), out var list)
            && list.Exists(e => e.Id == id)
            ? 1 : 0;

    private static int ExistsInWarm(SessionLifecycleTests.FakeStore store, string id) =>
        store.Entries.TryGetValue((Tier.Warm, ContentType.Memory), out var list)
            && list.Exists(e => e.Id == id)
            ? 1 : 0;

    [Fact]
    public void WarmSweep_PromotesHighAccessWarmEntryToHot()
    {
        var (lifecycle, store, _) = MakeLifecycle(promoteMinAccess: 5, promoteThreshold: 0.7);
        SeedWarm(store, id: "hot-candidate", accessCount: 6, lastAccessed: NowMs);
        SeedWarm(store, id: "cold-candidate", accessCount: 1, lastAccessed: NowMs);
        lifecycle.RunWarmSweepForTest();
        Assert.Equal(1, store.Count(Tier.Hot, ContentType.Memory));
        Assert.Equal(1, ExistsInHot(store, "hot-candidate"));
    }

    [Fact]
    public void WarmSweep_DoesNotPromoteFreshWarmEntry()
    {
        var (lifecycle, store, _) = MakeLifecycle(promoteMinAccess: 5, promoteThreshold: 0.7);
        SeedWarm(store, id: "brand-new", accessCount: 0, lastAccessed: NowMs); // decay~1.0 but access 0
        lifecycle.RunWarmSweepForTest();
        Assert.Equal(0, store.Count(Tier.Hot, ContentType.Memory)); // access gate blocks it
    }

    [Fact]
    public void WarmSweep_NeverEvictsStickyHot()
    {
        var (lifecycle, store, _) = MakeLifecycle(maxEntries: 1);
        SeedStickyHot(store, id: "sticky1", decay: 0.01); // lowest score, but sticky
        SeedHot(store, id: "earned1", decay: 0.9);
        lifecycle.RunWarmSweepForTest(); // hot count 2 > max 1
        Assert.Equal(1, ExistsInHot(store, "sticky1")); // sticky survives
    }

    // Non-vacuous companion to WarmSweep_NeverEvictsStickyHot: forces the
    // excess-eviction branch to actually run. maxEntries=2 with 3 NON-sticky
    // hot rows means the non-sticky count (3) alone exceeds the cap, so exactly
    // one non-sticky row is evicted. The sticky row has the lowest decay of all
    // and would be evicted first if it were in the candidate pool — it must
    // survive, and the lowest-decay NON-sticky row must be the one evicted.
    // (Fails if ExcludeSticky is removed from the toEvict listing: the sticky
    // row would then be the ASC-first candidate and get evicted.)
    [Fact]
    public void WarmSweep_EvictsLowestNonStickyButProtectsSticky()
    {
        var (lifecycle, store, _) = MakeLifecycle(maxEntries: 2);
        SeedStickyHot(store, id: "sticky-low", decay: 0.001); // lowest decay overall, sticky
        SeedHot(store, id: "nonsticky-lowest", decay: 0.10);  // lowest NON-sticky -> evicted
        SeedHot(store, id: "nonsticky-mid", decay: 0.50);
        SeedHot(store, id: "nonsticky-high", decay: 0.90);

        lifecycle.RunWarmSweepForTest(); // non-sticky count 3 > max 2 -> evict 1

        // (a) sticky survives despite lowest decay of all.
        Assert.Equal(1, ExistsInHot(store, "sticky-low"));
        // (b) the lowest-decay NON-sticky row is the one evicted (moved to warm).
        Assert.Equal(0, ExistsInHot(store, "nonsticky-lowest"));
        Assert.Equal(1, ExistsInWarm(store, "nonsticky-lowest"));
        // the two higher non-sticky rows stay.
        Assert.Equal(1, ExistsInHot(store, "nonsticky-mid"));
        Assert.Equal(1, ExistsInHot(store, "nonsticky-high"));
    }

    // I2: the hot char cap is enforced on promotion. A warm entry that passes
    // BOTH promotion gates (access_count and decay) but whose content exceeds
    // hotMaxContentChars must NOT be promoted; a contrasting under-cap entry
    // that passes the same gates DOES promote — proving the cap (not another
    // gate) is what blocks the oversized one.
    [Fact]
    public void WarmSweep_DoesNotPromoteWarmEntryExceedingHotCharCap()
    {
        const int cap = 50;
        var (lifecycle, store, _) = MakeLifecycle(
            promoteMinAccess: 5, promoteThreshold: 0.7, hotMaxContentChars: cap);

        // Oversized: passes both gates (access 6, fresh timestamp -> decay high)
        // but content length > cap.
        store.Entries.TryGetValue((Tier.Warm, ContentType.Memory), out var warm);
        warm ??= new List<Entry>();
        warm.Add(MakeEntry("too-big", accessCount: 6, lastAccessedAt: NowMs,
            content: new string('x', cap + 10)));
        // Under-cap control: same gates, short content -> promotes.
        warm.Add(MakeEntry("fits", accessCount: 6, lastAccessedAt: NowMs,
            content: new string('y', cap - 10)));
        store.Entries[(Tier.Warm, ContentType.Memory)] = warm;

        lifecycle.RunWarmSweepForTest();

        // Oversized stays in warm; under-cap promotes to hot.
        Assert.Equal(0, ExistsInHot(store, "too-big"));
        Assert.Equal(1, ExistsInWarm(store, "too-big"));
        Assert.Equal(1, ExistsInHot(store, "fits"));
    }
}
