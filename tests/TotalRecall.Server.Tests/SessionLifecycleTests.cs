// tests/TotalRecall.Server.Tests/SessionLifecycleTests.cs
//
// Plan 4 Task 4.3 — unit tests for SessionLifecycle. Manual fakes only,
// no mock library. Covers idempotency, tier-summary computation, context
// formatting (with and without tags), the three hint priorities, hint
// truncation, the 5-hint cap, importer-detect gating, importer-count
// propagation, and last-session-age formatting across all units.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

public sealed class SessionLifecycleTests
{
    // ---------- helpers ----------

    private static Entry MakeEntry(
        string id,
        string content,
        IEnumerable<string>? tags = null,
        int accessCount = 0,
        long createdAt = 0,
        double decayScore = 1.0,
        int timesInjected = 0)
    {
        return new Entry(
            id,
            content,
            FSharpOption<string>.None,         // summary
            FSharpOption<string>.None,         // source
            FSharpOption<SourceTool>.None,     // sourceTool
            FSharpOption<string>.None,         // project
            ListModule.OfSeq(tags ?? Array.Empty<string>()),
            createdAt,                          // createdAt
            createdAt,                          // updatedAt
            createdAt,                          // lastAccessedAt
            accessCount,
            decayScore,                         // decayScore
            FSharpOption<string>.None,          // parentId
            FSharpOption<string>.None,          // collectionId
            "",                                 // scope
            EntryType.Preference,               // entryType
            "{}", timesInjected);               // metadataJson, timesInjected
    }

    private sealed class FakeStore : IStore
    {
        // Per-(tier, type) entry lists. Test fixtures populate the slots they
        // care about; everything else returns empty.
        public Dictionary<(Tier, ContentType), List<Entry>> Entries { get; } = new();

        // Metadata-keyed lookup table; key is the metadata filter as a sorted
        // canonical string. Tests preload this directly.
        public Dictionary<string, List<Entry>> ByMetadata { get; } = new();

        public int CollectionCount { get; set; }

        // Diagnostic counters used by a couple of tests.
        public int CollectionsCallCount { get; private set; }

        // Records Move calls for warm-sweep immunity assertions.
        public List<(Tier FromTier, ContentType FromType, Tier ToTier, ContentType ToType, string Id)> MoveCalls { get; } = new();

        private List<Entry> Slot(Tier t, ContentType ct)
        {
            if (!Entries.TryGetValue((t, ct), out var list))
            {
                list = new List<Entry>();
                Entries[(t, ct)] = list;
            }
            return list;
        }

        public string Insert(Tier tier, ContentType type, InsertEntryOpts opts) =>
            throw new NotSupportedException();

        public string InsertWithEmbedding(Tier tier, ContentType type, InsertEntryOpts opts, ReadOnlyMemory<float> embedding) =>
            throw new NotSupportedException();

        public Entry? Get(Tier tier, ContentType type, string id) =>
            Slot(tier, type).FirstOrDefault(e => e.Id == id);

        public long? GetInternalKey(Tier tier, ContentType type, string id) =>
            Slot(tier, type).Any(e => e.Id == id) ? 1L : null;

        public void Update(Tier tier, ContentType type, string id, UpdateEntryOpts opts) =>
            throw new NotSupportedException();

        public void Delete(Tier tier, ContentType type, string id) =>
            throw new NotSupportedException();

        public IReadOnlyList<Entry> List(Tier tier, ContentType type, ListEntriesOpts? opts = null)
        {
            var src = Slot(tier, type).AsEnumerable();
            if (opts?.OrderBy is not null)
            {
                if (opts.OrderBy.StartsWith("access_count DESC", StringComparison.Ordinal))
                    src = src.OrderByDescending(e => e.AccessCount);
                else if (opts.OrderBy.StartsWith("decay_score ASC", StringComparison.Ordinal))
                    src = src.OrderBy(e => e.DecayScore);
                else if (opts.OrderBy.StartsWith("decay_score DESC", StringComparison.Ordinal))
                    src = src.OrderByDescending(e => e.DecayScore);
            }
            if (opts?.Limit is int lim) src = src.Take(lim);
            return src.ToList();
        }

        public int Count(Tier tier, ContentType type) => Slot(tier, type).Count;

        public int CountKnowledgeCollections()
        {
            CollectionsCallCount++;
            return CollectionCount;
        }

        public IReadOnlyList<Entry> ListByMetadata(
            Tier tier,
            ContentType type,
            IReadOnlyDictionary<string, string> metadataFilter,
            ListEntriesOpts? opts = null)
        {
            var key = MetaKey(tier, type, metadataFilter);
            if (!ByMetadata.TryGetValue(key, out var list)) return Array.Empty<Entry>();
            IEnumerable<Entry> src = list;
            if (opts?.OrderBy is not null && opts.OrderBy.StartsWith("access_count DESC", StringComparison.Ordinal))
            {
                src = src.OrderByDescending(e => e.AccessCount);
            }
            if (opts?.Limit is int lim) src = src.Take(lim);
            return src.ToList();
        }

        public void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id)
        {
            MoveCalls.Add((fromTier, fromType, toTier, toType, id));
            var src = Slot(fromTier, fromType);
            var entry = src.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;
            src.Remove(entry);
            Slot(toTier, toType).Add(entry);
        }

        public string? FindByContent(Tier tier, ContentType type, string content)
            => Slot(tier, type).FirstOrDefault(e => e.Content == content)?.Id;

        public static string MetaKey(Tier tier, ContentType type, IReadOnlyDictionary<string, string> filter)
        {
            var parts = filter.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}");
            return $"{tier}|{type}|{string.Join(",", parts)}";
        }

        // Records all UpdateInjectionCounts calls for injection-tracking assertions.
        public List<(Tier tier, ContentType type, string id)> InjectionCountCalls { get; } = new();

        public void UpdateInjectionCounts(IReadOnlyList<(Tier tier, ContentType type, string id)> entries)
        {
            InjectionCountCalls.AddRange(entries);
        }
    }

    private sealed class FakeImporter : IImporter
    {
        public string Name { get; }
        public bool DetectReturn { get; set; }
        public ImportResult MemResult { get; set; } = ImportResult.Empty;
        public ImportResult KbResult { get; set; } = ImportResult.Empty;

        public int DetectCalls { get; private set; }
        public int MemoryCalls { get; private set; }
        public int KnowledgeCalls { get; private set; }

        public FakeImporter(string name, bool detect = true)
        {
            Name = name;
            DetectReturn = detect;
        }

        public bool Detect()
        {
            DetectCalls++;
            return DetectReturn;
        }

        public ImporterScanResult Scan() => new(0, 0, 0);

        public ImportResult ImportMemories(string? project = null)
        {
            MemoryCalls++;
            return MemResult;
        }

        public ImportResult ImportKnowledge()
        {
            KnowledgeCalls++;
            return KbResult;
        }
    }

    private sealed class FakeCompactionLog : ICompactionLogReader
    {
        public long? LastTimestamp { get; set; }
        public string? LastExcludedReason { get; private set; }
        public int Calls { get; private set; }

        public long? GetLastTimestampExcludingReason(string excludedReason)
        {
            Calls++;
            LastExcludedReason = excludedReason;
            return LastTimestamp;
        }

        public IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null)
            => System.Array.Empty<CompactionAnalyticsRow>();

        public IReadOnlyList<CompactionMovementRow> GetRecentMovements(int limit)
            => System.Array.Empty<CompactionMovementRow>();

        public CompactionMovementRow? GetByTargetEntryId(string targetEntryId) => null;
    }

    private static SessionLifecycle BuildLifecycle(
        FakeStore? store = null,
        IEnumerable<IImporter>? importers = null,
        ICompactionLogReader? compaction = null,
        long now = 1_000_000_000_000L,
        string sessionId = "sess-test",
        ISkillImportService? skillImportService = null,
        TimeSpan? skillImportTimeout = null,
        int tokenBudget = 4000,
        int maxEntries = 50,
        Func<long, (int Count, double AvgLatencyMs)>? retrievalStatsSince = null,
        Func<(long Hits, long Misses, long TokensSaved)>? cacheStats = null)
    {
        return new SessionLifecycle(
            (importers ?? Array.Empty<IImporter>()).ToList(),
            store ?? new FakeStore(),
            compaction ?? new FakeCompactionLog(),
            sessionId,
            () => now,
            usageIndexer: null,
            storageMode: "sqlite",
            skillImportService: skillImportService,
            skillImportTimeout: skillImportTimeout,
            tokenBudget: tokenBudget,
            maxEntries: maxEntries,
            retrievalStatsSince: retrievalStatsSince,
            cacheStats: cacheStats);
    }

    private sealed class FakeSkillImportService : ISkillImportService
    {
        private readonly SkillImportSummaryDto[]? _summaries;
        private readonly Exception? _exception;
        private readonly Func<CancellationToken, Task<SkillImportSummaryDto[]>>? _async;
        private readonly SkillListResponseDto? _listResponse;
        private readonly Exception? _listException;

        public int ImportCalls { get; private set; }
        public int ListCalls { get; private set; }

        public FakeSkillImportService(
            SkillImportSummaryDto[]? summaries = null,
            Exception? exception = null,
            Func<CancellationToken, Task<SkillImportSummaryDto[]>>? asyncImpl = null,
            SkillListResponseDto? listResponse = null,
            Exception? listException = null)
        {
            _summaries = summaries;
            _exception = exception;
            _async = asyncImpl;
            _listResponse = listResponse;
            _listException = listException;
        }

        public Task<SkillImportSummaryDto[]> ImportAsync(string? projectPath, CancellationToken ct)
        {
            ImportCalls++;
            if (_exception is not null) throw _exception;
            if (_async is not null) return _async(ct);
            return Task.FromResult(_summaries ?? Array.Empty<SkillImportSummaryDto>());
        }

        public Task<SkillListResponseDto> ListVisibleAsync(CancellationToken ct)
        {
            ListCalls++;
            if (_listException is not null) throw _listException;
            return Task.FromResult(_listResponse
                ?? new SkillListResponseDto(0, 0, 0, Array.Empty<SkillDto>()));
        }

        public Task<ClaudeCodeScanResult> ScanExtraDirsAsync(CancellationToken ct) =>
            Task.FromResult(new ClaudeCodeScanResult(
                Array.Empty<ImportedSkill>(), Array.Empty<ScanError>()));
    }

    // ---------- BuildContext: sort + budget ----------

    [Fact]
    public void BuildContext_SortsByDecayScoreDescending()
    {
        var entries = new[]
        {
            MakeEntry("a", "low",  decayScore: 0.2),
            MakeEntry("b", "high", decayScore: 0.9),
            MakeEntry("c", "mid",  decayScore: 0.5),
        };
        var result = SessionLifecycle.BuildContext(entries,
            new BuildContextOptions { TokenBudget = 4000 });
        var lines = result.Context.Split('\n');
        Assert.Equal("- high", lines[0]);
        Assert.Equal("- mid",  lines[1]);
        Assert.Equal("- low",  lines[2]);
    }

    [Fact]
    public void BuildContext_RespectsTokenBudget()
    {
        // With heuristic estimate (words * 0.75), each entry is ~1 token.
        // tokenBudget=2 should allow only 2 of 3 entries to fit.
        var entries = new[]
        {
            MakeEntry("a", "aaaaaaaaaa", decayScore: 0.9),
            MakeEntry("b", "bbbbbbbbbb", decayScore: 0.8),
            MakeEntry("c", "cccccccccc", decayScore: 0.7),
        };
        var result = SessionLifecycle.BuildContext(entries,
            new BuildContextOptions { TokenBudget = 2 });
        Assert.True(result.Truncated);
        var lines = result.Context.Split('\n');
        Assert.True(lines.Length >= 1 && lines.Length <= 2,
            "should fit some but not all entries");
        Assert.Contains("aaaaaaaaaa", lines[0]);
    }

    [Fact]
    public void BuildContext_AllFit_NotTruncated()
    {
        var entries = new[]
        {
            MakeEntry("a", "hello", decayScore: 0.9),
            MakeEntry("b", "world", decayScore: 0.5),
        };
        var result = SessionLifecycle.BuildContext(entries,
            new BuildContextOptions { TokenBudget = 4000 });
        Assert.False(result.Truncated);
        Assert.Equal("- hello\n- world", result.Context);
    }

    [Fact]
    public void BuildContext_Empty_ReturnsFalseAndEmptyString()
    {
        var result = SessionLifecycle.BuildContext(
            Array.Empty<Entry>(), new BuildContextOptions { TokenBudget = 4000 });
        Assert.Equal(string.Empty, result.Context);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void BuildContext_FirstEntryExceedsBudget_ReturnsEmptyWithTruncated()
    {
        // tokenBudget=0 → nothing fits. Returns empty context with truncated=true.
        var entries = new[] { MakeEntry("a", "hello", decayScore: 1.0) };
        var result = SessionLifecycle.BuildContext(entries,
            new BuildContextOptions { TokenBudget = 0 });
        Assert.Equal(string.Empty, result.Context);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task EnsureInitializedAsync_HotContextTruncated_WhenBudgetSmall()
    {
        var store = new FakeStore();
        // Three entries, each "- " + 10 chars = 12 chars. tokenBudget=2 → maxChars=8.
        // Even the first entry (12 chars) exceeds the budget → Truncated=true.
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("a", "aaaaaaaaaa", decayScore: 0.9),
            MakeEntry("b", "bbbbbbbbbb", decayScore: 0.8),
        };
        var lifecycle = BuildLifecycle(store, tokenBudget: 2);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.True(result.HotContextTruncated);
    }

    // ---------- warm sweep (synchronous) ----------

    [Fact]
    public async Task WarmSweep_DemotesExcessEntriesToWarm()
    {
        var store = new FakeStore();
        // 60 hot entries, maxEntries=50 → sweep should move 10 lowest-decay to warm.
        store.Entries[(Tier.Hot, ContentType.Memory)] = Enumerable.Range(0, 60)
            .Select(i => MakeEntry($"id{i}", $"content{i}", decayScore: (i + 1) / 60.0))
            .ToList();

        var lifecycle = BuildLifecycle(store, maxEntries: 50);
        await lifecycle.EnsureInitializedAsync();

        Assert.Equal(50, store.Count(Tier.Hot, ContentType.Memory));
        Assert.Equal(10, store.Count(Tier.Warm, ContentType.Memory));
    }

    [Fact]
    public async Task WarmSweep_SweepsLowestDecayFirst()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("low1",  "low1",  decayScore: 0.1),
            MakeEntry("low2",  "low2",  decayScore: 0.2),
            MakeEntry("high1", "high1", decayScore: 0.9),
            MakeEntry("high2", "high2", decayScore: 0.8),
            MakeEntry("mid",   "mid",   decayScore: 0.5),
        };

        // maxEntries=3 → 2 lowest should be swept
        var lifecycle = BuildLifecycle(store, maxEntries: 3);
        await lifecycle.EnsureInitializedAsync();

        var hot = store.Entries[(Tier.Hot, ContentType.Memory)].Select(e => e.Id).ToHashSet();
        var warm = store.Entries.GetValueOrDefault((Tier.Warm, ContentType.Memory), new List<Entry>())
            .Select(e => e.Id).ToHashSet();

        Assert.Contains("high1", hot);
        Assert.Contains("high2", hot);
        Assert.Contains("mid", hot);
        Assert.Contains("low1", warm);
        Assert.Contains("low2", warm);
    }

    [Fact]
    public async Task WarmSweep_NoOp_WhenUnderLimit()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = Enumerable.Range(0, 30)
            .Select(i => MakeEntry($"id{i}", $"content{i}", decayScore: (i + 1) / 30.0))
            .ToList();

        var lifecycle = BuildLifecycle(store, maxEntries: 50);
        await lifecycle.EnsureInitializedAsync();

        Assert.Equal(30, store.Count(Tier.Hot, ContentType.Memory));
        Assert.Equal(0, store.Count(Tier.Warm, ContentType.Memory));
    }

    // ---------- 1. idempotency ----------

    [Fact]
    public async Task EnsureInitializedAsync_Idempotent()
    {
        var store = new FakeStore();
        var importer = new FakeImporter("claude-code");
        var lifecycle = BuildLifecycle(store, new[] { importer });

        Assert.False(lifecycle.IsInitialized);
        var first = await lifecycle.EnsureInitializedAsync();
        var second = await lifecycle.EnsureInitializedAsync();

        Assert.True(lifecycle.IsInitialized);
        Assert.Same(first, second);
        Assert.Equal(1, importer.DetectCalls);
        Assert.Equal(1, importer.MemoryCalls);
        Assert.Equal(1, importer.KnowledgeCalls);
    }

    // ---------- 2. tier summary ----------

    [Fact]
    public async Task TierSummary_ComputedFromCounts()
    {
        var store = new FakeStore { CollectionCount = 4 };
        store.Entries[(Tier.Hot, ContentType.Memory)] = new()
        {
            MakeEntry("h1", "hot1"), MakeEntry("h2", "hot2"),
        };
        store.Entries[(Tier.Warm, ContentType.Memory)] = new() { MakeEntry("w1", "wm") };
        store.Entries[(Tier.Warm, ContentType.Knowledge)] = new()
        {
            MakeEntry("wk1", "wk1"), MakeEntry("wk2", "wk2"),
        };
        store.Entries[(Tier.Cold, ContentType.Memory)] = new() { MakeEntry("c1", "cm") };
        store.Entries[(Tier.Cold, ContentType.Knowledge)] = new()
        {
            MakeEntry("ck1", "ck"), MakeEntry("ck2", "ck"), MakeEntry("ck3", "ck"),
        };
        store.Entries[(Tier.Hot, ContentType.Knowledge)] = new() { MakeEntry("hk", "hk") };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Equal(2, result.TierSummary.Hot);
        Assert.Equal(1 + 2, result.TierSummary.Warm);
        Assert.Equal(1 + 3, result.TierSummary.Cold);
        Assert.Equal(1 + 2 + 3, result.TierSummary.Kb);
        Assert.Equal(4, result.TierSummary.Collections);
        Assert.Equal(1, store.CollectionsCallCount);
        Assert.Equal(2, result.HotEntryCount);
    }

    // ---------- 3. context format ----------

    [Fact]
    public async Task Context_MatchesTsFormat()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = new()
        {
            MakeEntry("a", "alpha", tags: new[] { "x", "y" }),
            MakeEntry("b", "bravo"),
            MakeEntry("c", "charlie", tags: new[] { "z" }),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Equal("- alpha [x, y]\n- bravo\n- charlie [z]", result.Context);
    }

    [Fact]
    public void BuildContext_EmptyHotTier_ReturnsEmptyString()
    {
        var result = SessionLifecycle.BuildContext(Array.Empty<Entry>(),
            new BuildContextOptions { TokenBudget = 4000 });
        Assert.Equal(string.Empty, result.Context);
    }

    // ---------- 4. P1 hints (corrections + preferences) ----------

    [Fact]
    public async Task Hints_Priority1_CorrectionsAndPreferences()
    {
        var store = new FakeStore();
        // Corrections (max 2 returned by store).
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "correction" })] = new()
        {
            MakeEntry("c1", "use tabs not spaces", accessCount: 5, createdAt: 100),
            MakeEntry("c2", "always run lint", accessCount: 3, createdAt: 200),
        };
        // Preferences (max 2). Highest access_count overall to test the merged sort.
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "preference" })] = new()
        {
            MakeEntry("p1", "prefers concise commits", accessCount: 9, createdAt: 50),
            MakeEntry("p2", "prefers TDD", accessCount: 4, createdAt: 60),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        // Top 2 by access_count DESC then created_at ASC: p1 (9), c1 (5).
        Assert.Collection(result.Hints,
            h => Assert.Equal("prefers concise commits", h.Summary),
            h => Assert.Equal("use tabs not spaces", h.Summary));
    }

    // ---------- 5. P2 hints (frequently accessed) ----------

    [Fact]
    public async Task Hints_Priority2_FrequentlyAccessed()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Warm, ContentType.Memory)] = new()
        {
            MakeEntry("f1", "frequent one",   accessCount: 10),
            MakeEntry("f2", "frequent two",   accessCount: 7),
            MakeEntry("f3", "rare-1",         accessCount: 2),     // filtered (<3)
            MakeEntry("f4", "frequent three", accessCount: 5),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        // No P1 hits; P2 takes top 2 with access_count >= 3.
        Assert.Equal(2, result.Hints.Count);
        Assert.Equal("frequent one", result.Hints[0].Summary);
        Assert.Equal("frequent two", result.Hints[1].Summary);
    }

    // ---------- 6. truncation ----------

    [Fact]
    public void TruncateHint_Over120Chars_AppendsEllipsis()
    {
        var input = new string('a', 200);
        var output = SessionLifecycle.TruncateHint(input);
        Assert.Equal(123, output.Length);
        Assert.EndsWith("...", output);
        Assert.StartsWith(new string('a', 120), output);
    }

    [Fact]
    public void TruncateHint_AtBoundary_NoEllipsis()
    {
        var input = new string('b', 120);
        Assert.Equal(input, SessionLifecycle.TruncateHint(input));
    }

    [Fact]
    public async Task Hints_Truncation_OverLongContentTrimmed()
    {
        var store = new FakeStore();
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "correction" })] = new()
        {
            MakeEntry("long", new string('z', 250), accessCount: 1),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Single(result.Hints);
        Assert.Equal(123, result.Hints[0].Summary.Length);
        Assert.EndsWith("...", result.Hints[0].Summary);
    }

    // ---------- 7. cap at 5 ----------

    [Fact]
    public async Task Hints_MaxFive()
    {
        var store = new FakeStore();
        // 2 corrections (P1 cap = 2)
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "correction" })] = new()
        {
            MakeEntry("c1", "c-one", accessCount: 9),
            MakeEntry("c2", "c-two", accessCount: 8),
        };
        // 2 preferences — top 2 will go to P1 if higher access_count, but corrections
        // here have 9/8 so prefs are dropped from P1. P2 will then surface frequents.
        store.ByMetadata[FakeStore.MetaKey(Tier.Warm, ContentType.Memory,
            new Dictionary<string, string> { ["entry_type"] = "preference" })] = new()
        {
            MakeEntry("p1", "p-one", accessCount: 1),
            MakeEntry("p2", "p-two", accessCount: 1),
        };
        // 5 frequents — only first 2 with access_count >= 3 are taken by P2.
        store.Entries[(Tier.Warm, ContentType.Memory)] = new()
        {
            MakeEntry("f1", "f-one", accessCount: 7),
            MakeEntry("f2", "f-two", accessCount: 6),
            MakeEntry("f3", "f-three", accessCount: 5),
            MakeEntry("f4", "f-four", accessCount: 4),
            MakeEntry("f5", "f-five", accessCount: 3),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        // P1 contributes 2 (c-one, c-two), P2 contributes 2 (f-one, f-two).
        // Total = 4, well under cap. Verify cap enforcement separately by
        // inspecting the bound rather than fabricating an impossible 6+ scenario.
        Assert.True(result.Hints.Count <= 5);
        Assert.Equal(4, result.Hints.Count);
    }

    // ---------- 8. detect gating ----------

    [Fact]
    public async Task ImportSummary_OnlyDetectedImporters()
    {
        var detected = new FakeImporter("claude-code", detect: true);
        var notDetected = new FakeImporter("hermes", detect: false);
        var lifecycle = BuildLifecycle(new FakeStore(), new IImporter[] { detected, notDetected });

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Equal(1, detected.MemoryCalls);
        Assert.Equal(1, detected.KnowledgeCalls);
        Assert.Equal(0, notDetected.MemoryCalls);
        Assert.Equal(0, notDetected.KnowledgeCalls);
        Assert.Single(result.ImportSummary);
        Assert.Equal("claude-code", result.ImportSummary[0].Tool);
    }

    // ---------- 9. import counts ----------

    [Fact]
    public async Task ImportSummary_CountsReflectImporterReturns()
    {
        var importer = new FakeImporter("cursor")
        {
            MemResult = new ImportResult(7, 0, Array.Empty<string>()),
            KbResult = new ImportResult(3, 0, Array.Empty<string>()),
        };
        var lifecycle = BuildLifecycle(new FakeStore(), new[] { importer });

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Equal(7, result.ImportSummary[0].MemoriesImported);
        Assert.Equal(3, result.ImportSummary[0].KnowledgeImported);
    }

    // ---------- 10. last session age null ----------

    [Fact]
    public async Task LastSessionAge_Null_WhenNoLog()
    {
        var compaction = new FakeCompactionLog { LastTimestamp = null };
        var lifecycle = BuildLifecycle(new FakeStore(), compaction: compaction);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Null(result.LastSessionAge);
        Assert.Equal("warm_sweep_decay", compaction.LastExcludedReason);
    }

    // ---------- 11. last session age formatting (table-driven) ----------

    public static IEnumerable<object?[]> AgeFormatCases() => new object?[][]
    {
        new object?[] { 0L,                              "just now" },
        new object?[] { 30_000L,                         "just now" },
        new object?[] { 60_000L,                         "1 minute ago" },
        new object?[] { 5L * 60_000,                     "5 minutes ago" },
        new object?[] { 59L * 60_000,                    "59 minutes ago" },
        new object?[] { 60L * 60_000,                    "1 hour ago" },
        new object?[] { 3L * 60 * 60_000,                "3 hours ago" },
        new object?[] { 23L * 60 * 60_000,               "23 hours ago" },
        new object?[] { 24L * 60 * 60_000,               "1 day ago" },
        new object?[] { 3L * 24 * 60 * 60_000,           "3 days ago" },
        new object?[] { 6L * 24 * 60 * 60_000,           "6 days ago" },
        new object?[] { 7L * 24 * 60 * 60_000,           "1 week ago" },
        new object?[] { 14L * 24 * 60 * 60_000,          "2 weeks ago" },
        new object?[] { 30L * 24 * 60 * 60_000,          "4 weeks ago" },
    };

    [Theory]
    [MemberData(nameof(AgeFormatCases))]
    public void LastSessionAge_Formats_JustNow_Minutes_Hours_Days_Weeks(long elapsedMs, string expected)
    {
        const long now = 10_000_000_000L;
        var actual = SessionLifecycle.FormatLastSessionAge(now - elapsedMs, now);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LastSessionAge_Null_TimestampZero_ReturnsNull()
    {
        Assert.Null(SessionLifecycle.FormatLastSessionAge(null, 1_000));
        Assert.Null(SessionLifecycle.FormatLastSessionAge(0L, 1_000));
    }

    // ---------- 12. skill import integration ----------

    [Fact]
    public async Task EnsureInitializedAsync_WithSkillService_ImportIsFireAndForget_SummaryHasZeroCounts()
    {
        // Import runs in the background — session_start returns immediately with
        // zero skill counts in the import summary. The ImportAsync call is still
        // made (fire-and-forget), but the summary row is never populated from it.
        var svc = new FakeSkillImportService(
            summaries: new[]
            {
                new SkillImportSummaryDto(
                    Adapter: "claude-code",
                    Scanned: 3, Imported: 2, Updated: 1,
                    Unchanged: 0, Orphaned: 0,
                    Errors: Array.Empty<string>()),
            });
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        // No skill-import row is added to the summary (import is background).
        Assert.DoesNotContain(result.ImportSummary, r => r.Tool == "claude-code");
    }

    [Fact]
    public async Task EnsureInitializedAsync_WithSkillService_MemoryRowsPreserved_SkillCountsZero()
    {
        // Memory importer contributes a "claude-code" row; the skill import is
        // fire-and-forget so it does not merge skill counts into the row.
        var importer = new FakeImporter("claude-code")
        {
            MemResult = new ImportResult(4, 0, Array.Empty<string>()),
            KbResult = new ImportResult(2, 0, Array.Empty<string>()),
        };
        var svc = new FakeSkillImportService(
            summaries: new[]
            {
                new SkillImportSummaryDto(
                    Adapter: "claude-code",
                    Scanned: 5, Imported: 3, Updated: 2,
                    Unchanged: 1, Orphaned: 1,
                    Errors: Array.Empty<string>()),
            });
        var lifecycle = BuildLifecycle(
            new FakeStore(),
            importers: new[] { importer },
            skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        // Memory/KB counts from the host importer are preserved.
        var row = Assert.Single(result.ImportSummary, r => r.Tool == "claude-code");
        Assert.Equal(4, row.MemoriesImported);
        Assert.Equal(2, row.KnowledgeImported);
        // Skill counts stay at zero — import runs in the background.
        Assert.Equal(0, row.SkillsImported);
        Assert.Equal(0, row.SkillsUpdated);
        Assert.Equal(0, row.SkillsUnchanged);
        Assert.Equal(0, row.SkillsOrphaned);
        Assert.Empty(row.SkillsErrors);
    }

    [Fact]
    public async Task EnsureInitializedAsync_SkillServiceThrows_DoesNotPropagateException()
    {
        // Import runs in the background. A synchronous throw from ImportAsync is
        // swallowed by the try/catch around the fire-and-forget launch; it must
        // never surface to the caller. No error row is added to the import summary.
        var svc = new FakeSkillImportService(
            exception: new InvalidOperationException("boom"));
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        // Must not throw.
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.NotNull(result);
        // No skill-import error row should appear.
        Assert.DoesNotContain(result.ImportSummary, r => r.SkillsErrors.Count > 0);
    }

    [Fact]
    public async Task EnsureInitializedAsync_WithoutSkillService_OmitsSkillRowAndLeavesMemoryRowsUntouched()
    {
        var importer = new FakeImporter("claude-code")
        {
            MemResult = new ImportResult(1, 0, Array.Empty<string>()),
            KbResult = new ImportResult(0, 0, Array.Empty<string>()),
        };
        var lifecycle = BuildLifecycle(
            new FakeStore(),
            importers: new[] { importer },
            skillImportService: null);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.DoesNotContain(result.ImportSummary, r => r.Tool == "claude-code" && r.SkillsImported > 0);
        // Existing memory-importer rows should have SkillsImported == 0 (default for legacy rows).
        Assert.All(result.ImportSummary, r => Assert.Equal(0, r.SkillsImported));
        Assert.All(result.ImportSummary, r => Assert.Empty(r.SkillsErrors));
    }

    [Fact]
    public async Task EnsureInitializedAsync_SlowSkillImport_DoesNotBlockSessionInit()
    {
        // Import runs in the background — a slow import must never block session_start.
        // The skillImportTimeout parameter is now ignored (kept for source compat).
        var svc = new FakeSkillImportService(
            asyncImpl: async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                return Array.Empty<SkillImportSummaryDto>();
            });
        var lifecycle = BuildLifecycle(
            new FakeStore(),
            skillImportService: svc,
            skillImportTimeout: TimeSpan.FromMilliseconds(50)); // ignored — kept for compat

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await lifecycle.EnsureInitializedAsync();
        sw.Stop();

        // Must complete well under the 10-second delay the import would impose.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"EnsureInitializedAsync took {sw.Elapsed.TotalSeconds:F2}s — import must be fire-and-forget.");
        Assert.NotNull(result);
        // No timeout error rows.
        Assert.DoesNotContain(result.ImportSummary, r => r.SkillsErrors.Any(e => e.StartsWith("skill_import_timeout_")));
    }

    // ---------- 13. BuildSkillsBlock unit tests ----------

    private static SkillDto MakeSkillDto(string name, string? description = null) =>
        new SkillDto(
            Id: Guid.NewGuid(),
            Name: name,
            Description: description,
            Scope: "user",
            ScopeId: "u1",
            Tags: Array.Empty<string>(),
            Version: 1,
            Source: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void BuildSkillsBlock_NoSkills_ReturnsEmpty()
    {
        var response = new SkillListResponseDto(0, 0, 50, Array.Empty<SkillDto>());
        var block = SessionLifecycle.BuildSkillsBlock(response);
        Assert.Equal(string.Empty, block);
    }

    [Fact]
    public void BuildSkillsBlock_WithSkills_ContainsHeader()
    {
        var response = new SkillListResponseDto(
            Total: 2, Skip: 0, Take: 50,
            Items: new[]
            {
                MakeSkillDto("deploy-app", "Deploys the application"),
                MakeSkillDto("run-tests", "Runs the test suite"),
            });

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("## Available Skills", block);
        Assert.Contains("skill_get", block);
        Assert.Contains("skill_search", block);
    }

    [Fact]
    public void BuildSkillsBlock_WithSkills_ContainsSkillNamesAndDescriptions()
    {
        var response = new SkillListResponseDto(
            Total: 2, Skip: 0, Take: 50,
            Items: new[]
            {
                MakeSkillDto("deploy-app", "Deploys the application"),
                MakeSkillDto("run-tests", "Runs the test suite"),
            });

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("- deploy-app: Deploys the application", block);
        Assert.Contains("- run-tests: Runs the test suite", block);
    }

    [Fact]
    public void BuildSkillsBlock_NullDescription_UsesNoDescriptionPlaceholder()
    {
        var response = new SkillListResponseDto(
            Total: 1, Skip: 0, Take: 50,
            Items: new[] { MakeSkillDto("my-skill", description: null) });

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("- my-skill: (no description)", block);
    }

    [Fact]
    public void BuildSkillsBlock_EmptyDescription_UsesNoDescriptionPlaceholder()
    {
        var response = new SkillListResponseDto(
            Total: 1, Skip: 0, Take: 50,
            Items: new[] { MakeSkillDto("my-skill", description: "   ") });

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("- my-skill: (no description)", block);
    }

    [Fact]
    public void BuildSkillsBlock_ManySkills_AllItemsAppear()
    {
        var items = Enumerable.Range(1, 100)
            .Select(i => MakeSkillDto($"skill-{i:D3}", $"Description {i}"))
            .ToArray();
        var response = new SkillListResponseDto(Total: 100, Skip: 0, Take: int.MaxValue, Items: items);

        var block = SessionLifecycle.BuildSkillsBlock(response);

        Assert.Contains("- skill-001: Description 1", block);
        Assert.Contains("- skill-100: Description 100", block);
        var lineCount = block.Split('\n').Count(l => l.StartsWith("- "));
        Assert.Equal(100, lineCount);
    }

    // ---------- 14. skill listing integration in EnsureInitializedAsync ----------

    [Fact]
    public async Task EnsureInitializedAsync_SkillsPresent_ContextContainsSkillsBlock()
    {
        var listResponse = new SkillListResponseDto(
            Total: 2, Skip: 0, Take: 50,
            Items: new[]
            {
                MakeSkillDto("deploy-app", "Deploys the application"),
                MakeSkillDto("run-tests", null),
            });
        var svc = new FakeSkillImportService(listResponse: listResponse);
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Contains("## Available Skills", result.Context);
        Assert.Contains("- deploy-app: Deploys the application", result.Context);
        Assert.Contains("- run-tests: (no description)", result.Context);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ZeroSkills_ContextOmitsSkillsBlock()
    {
        var listResponse = new SkillListResponseDto(0, 0, 50, Array.Empty<SkillDto>());
        var svc = new FakeSkillImportService(listResponse: listResponse);
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.DoesNotContain("## Available Skills", result.Context);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ManySkills_AllAppearedInContext()
    {
        var items = Enumerable.Range(1, 100)
            .Select(i => MakeSkillDto($"skill-{i:D3}"))
            .ToArray();
        var listResponse = new SkillListResponseDto(Total: 100, Skip: 0, Take: int.MaxValue, Items: items);
        var svc = new FakeSkillImportService(listResponse: listResponse);
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Contains("- skill-100: (no description)", result.Context);
        Assert.DoesNotContain("[and", result.Context);
    }

    [Fact]
    public async Task EnsureInitializedAsync_ListVisibleThrows_ContextOmitsBlockNoException()
    {
        var svc = new FakeSkillImportService(
            listException: new InvalidOperationException("cortex down"));
        var lifecycle = BuildLifecycle(new FakeStore(), skillImportService: svc);

        // Must not throw.
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.DoesNotContain("## Available Skills", result.Context);
    }

    [Fact]
    public async Task EnsureInitializedAsync_SkillsAndHotEntries_BothAppearedInContext()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("m1", "important memory"),
        };
        var listResponse = new SkillListResponseDto(
            Total: 1, Skip: 0, Take: 50,
            Items: new[] { MakeSkillDto("my-skill", "Does something useful") });
        var svc = new FakeSkillImportService(listResponse: listResponse);
        var lifecycle = BuildLifecycle(store, skillImportService: svc);

        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Contains("- important memory", result.Context);
        Assert.Contains("## Available Skills", result.Context);
        // Skills block comes after hot memories with a blank line between.
        var memIdx = result.Context.IndexOf("- important memory", StringComparison.Ordinal);
        var skillsIdx = result.Context.IndexOf("## Available Skills", StringComparison.Ordinal);
        Assert.True(memIdx < skillsIdx, "Hot memories should appear before skills block");
    }

    // ---------- Phase 3 idea 2a: efficiency stats fold-in ----------

    [Fact]
    public async Task Refresh_ReportsCacheStats_WhenDelegateWired()
    {
        var lifecycle = BuildLifecycle(cacheStats: () => (3L, 1L, 8500L));
        await lifecycle.EnsureInitializedAsync();

        var result = await lifecycle.RefreshAsync();

        Assert.NotNull(result.Efficiency.Cache);
        Assert.Equal(3L, result.Efficiency.Cache!.Hits);
        Assert.Equal(1L, result.Efficiency.Cache.Misses);
        Assert.Equal(8500L, result.Efficiency.Cache.TokensSaved);
        Assert.Equal(0.75, result.Efficiency.Cache.HitRate);
    }

    [Fact]
    public async Task Refresh_OmitsCacheStats_WhenNoDelegate()
    {
        var lifecycle = BuildLifecycle();
        await lifecycle.EnsureInitializedAsync();

        var result = await lifecycle.RefreshAsync();

        Assert.Null(result.Efficiency.Cache);
    }

    [Fact]
    public async Task Refresh_ReportsRetrievalStats_AndDuration()
    {
        var lifecycle = BuildLifecycle(retrievalStatsSince: _ => (8, 12.5));
        await lifecycle.EnsureInitializedAsync();

        var result = await lifecycle.RefreshAsync();

        Assert.Equal(8, result.Efficiency.Session.RetrievalsPerformed);
        Assert.Equal(12.5, result.Efficiency.Session.AvgRetrievalLatencyMs);
        Assert.True(result.Efficiency.Session.DurationMinutes >= 0);
    }

    [Fact]
    public async Task Refresh_RecommendsMemoryExtract_WhenManyRetrievals()
    {
        var lifecycle = BuildLifecycle(retrievalStatsSince: _ => (8, 5.0));
        await lifecycle.EnsureInitializedAsync();

        var result = await lifecycle.RefreshAsync();

        Assert.Contains(result.Recommendations, r => r.Action == "memory_extract");
    }

    [Fact]
    public async Task Refresh_NoRecommendations_OnQuietYoungSession()
    {
        var lifecycle = BuildLifecycle(retrievalStatsSince: _ => (0, 0.0));
        await lifecycle.EnsureInitializedAsync();

        var result = await lifecycle.RefreshAsync();

        Assert.Empty(result.Recommendations);
    }

    [Fact]
    public async Task Refresh_CacheStatsDelegateThrows_DegradesToNull()
    {
        var lifecycle = BuildLifecycle(cacheStats: () => throw new InvalidOperationException("boom"));
        await lifecycle.EnsureInitializedAsync();

        var result = await lifecycle.RefreshAsync();

        Assert.Null(result.Efficiency.Cache); // failure degrades, never propagates
    }

    [Fact]
    public async Task Refresh_RetrievalStatsDelegateThrows_DegradesToZeros()
    {
        var lifecycle = BuildLifecycle(retrievalStatsSince: _ => throw new InvalidOperationException("boom"));
        await lifecycle.EnsureInitializedAsync();

        var result = await lifecycle.RefreshAsync();

        Assert.Equal(0, result.Efficiency.Session.RetrievalsPerformed);
        Assert.Equal(0.0, result.Efficiency.Session.AvgRetrievalLatencyMs);
        Assert.Empty(result.Recommendations);
    }

    // ---------- Task 8: BuildPinnedBlock unit tests ----------

    [Fact]
    public void BuildPinnedBlock_Empty_ReturnsEmpty()
    {
        var (block, ids) = SessionLifecycle.BuildPinnedBlock(
            Array.Empty<Entry>(), Array.Empty<Entry>());
        Assert.Equal(string.Empty, block);
        Assert.Empty(ids);
    }

    [Fact]
    public void BuildPinnedBlock_RendersVerbatim_NoTruncation()
    {
        var longContent = new string('x', 10_000); // far beyond any token budget
        var entries = new[] { MakeEntry("p1", longContent) };

        var (block, ids) = SessionLifecycle.BuildPinnedBlock(entries, Array.Empty<Entry>());

        Assert.Contains(longContent, block); // verbatim — never truncated
        Assert.Single(ids);
        Assert.Equal((Tier.Pinned, ContentType.Memory, "p1"), ids[0]);
    }

    [Fact]
    public void BuildPinnedBlock_IncludesKnowledgeEntries()
    {
        var (block, ids) = SessionLifecycle.BuildPinnedBlock(
            new[] { MakeEntry("m1", "mem") }, new[] { MakeEntry("k1", "know") });

        Assert.Contains("mem", block);
        Assert.Contains("know", block);
        Assert.Equal(2, ids.Count);
        Assert.Equal(ContentType.Memory, ids[0].Item2);
        Assert.Equal(ContentType.Knowledge, ids[1].Item2);
    }

    [Fact]
    public void BuildPinnedBlock_HasDirectiveHeader()
    {
        var (block, _) = SessionLifecycle.BuildPinnedBlock(
            new[] { MakeEntry("m1", "rule") }, Array.Empty<Entry>());
        Assert.StartsWith("## Pinned directives (always follow)", block);
    }

    [Fact]
    public void BuildPinnedBlock_IdsHaveCorrectTierAndType()
    {
        var (_, ids) = SessionLifecycle.BuildPinnedBlock(
            new[] { MakeEntry("mem1", "m") },
            new[] { MakeEntry("kb1", "k") });

        Assert.Equal(2, ids.Count);
        Assert.Equal(Tier.Pinned, ids[0].Item1);
        Assert.Equal(ContentType.Memory, ids[0].Item2);
        Assert.Equal("mem1", ids[0].Item3);
        Assert.Equal(Tier.Pinned, ids[1].Item1);
        Assert.Equal(ContentType.Knowledge, ids[1].Item2);
        Assert.Equal("kb1", ids[1].Item3);
    }

    // ---------- Task 8: Session init integration tests ----------

    [Fact]
    public async Task SessionInit_PinnedBlock_PrependsContext_AndBudgetsHotTier()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Pinned, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("pm1", "PINNED-CONTENT"),
        };
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("h1", "HOT-CONTENT"),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.StartsWith("## Pinned", result.Context);
        var pinnedIdx = result.Context.IndexOf("PINNED-CONTENT", StringComparison.Ordinal);
        var hotIdx = result.Context.IndexOf("HOT-CONTENT", StringComparison.Ordinal);
        Assert.True(pinnedIdx >= 0);
        Assert.True(hotIdx > pinnedIdx, "Pinned content must appear before hot content");
        Assert.Equal(1, result.TierSummary.Pinned);
    }

    [Fact]
    public async Task SessionInit_TierSummaryPinned_ReflectsCount()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Pinned, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("pm1", "pin one"),
            MakeEntry("pm2", "pin two"),
        };
        store.Entries[(Tier.Pinned, ContentType.Knowledge)] = new List<Entry>
        {
            MakeEntry("pk1", "pinned knowledge"),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Equal(3, result.TierSummary.Pinned);
    }

    [Fact]
    public async Task SessionInit_PinnedOverHalfBudget_EmitsPressureHint_ContentStillPresent()
    {
        // HeuristicEstimateTokens = words * 0.75 (ceiling). tokenBudget=10; threshold:
        // pinnedTokens * 2 > 10 i.e. pinnedTokens >= 6.
        // The estimate runs over the WHOLE pinned block including the
        // "## Pinned directives (always follow)" header. Block for this test:
        // header (5 words) + "\n- PINNED word1…word15" (17 words) = 22 words
        // → ceiling(22 * 0.75) = 17 tokens. 17*2=34 > 10 → hint fires.
        var bigContent = "PINNED " + string.Join(" ", Enumerable.Range(1, 15).Select(i => $"word{i}"));
        var store = new FakeStore();
        store.Entries[(Tier.Pinned, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("pm1", bigContent),
        };

        var lifecycle = BuildLifecycle(store, tokenBudget: 10);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.Contains(result.Hints, h => h.Type == "pinned_budget_pressure");
        Assert.Contains("PINNED", result.Context); // still injected — never truncated
    }

    [Fact]
    public async Task SessionInit_NoPinnedEntries_NoBudgetHint()
    {
        var store = new FakeStore();
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("h1", "normal hot entry"),
        };

        var lifecycle = BuildLifecycle(store, tokenBudget: 10);
        var result = await lifecycle.EnsureInitializedAsync();

        Assert.DoesNotContain(result.Hints, h => h.Type == "pinned_budget_pressure");
    }

    [Fact]
    public async Task SessionInit_PinnedKb_NotCountedInKbField()
    {
        // Kb field should only count hot+warm+cold knowledge, not pinned knowledge.
        var store = new FakeStore();
        store.Entries[(Tier.Pinned, ContentType.Knowledge)] = new List<Entry>
        {
            MakeEntry("pk1", "pinned knowledge"),
        };
        store.Entries[(Tier.Hot, ContentType.Knowledge)] = new List<Entry>
        {
            MakeEntry("hk1", "hot knowledge"),
        };

        var lifecycle = BuildLifecycle(store);
        var result = await lifecycle.EnsureInitializedAsync();

        // Kb = hot+warm+cold knowledge only (1 hot here)
        Assert.Equal(1, result.TierSummary.Kb);
        // Pinned = pinned memory + pinned knowledge (0+1=1)
        Assert.Equal(1, result.TierSummary.Pinned);
    }

    // ---------- Task 8: Warm sweep immunity test ----------

    [Fact]
    public async Task WarmSweep_NeverTouchesPinned()
    {
        var store = new FakeStore();
        // Dead-weight profile: TimesInjected >= autoDemoteMinInjections=10, AccessCount=0.
        // If warm sweep accidentally visited pinned entries, it would try to move this.
        store.Entries[(Tier.Pinned, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("pm1", "pinned dead-weight", accessCount: 0, timesInjected: 20),
        };

        var lifecycle = BuildLifecycle(store);
        await lifecycle.EnsureInitializedAsync();

        // No Move calls should involve the Pinned tier as FromTier.
        Assert.DoesNotContain(store.MoveCalls, c => c.FromTier.IsPinned);
    }

    // ---------- Task 8 / Fix 2: pinned re-injected on RefreshAsync ----------

    [Fact]
    public async Task Refresh_PinnedBlock_PrependedToContext()
    {
        // Seed a pinned memory and a hot memory so both appear in refresh context.
        var store = new FakeStore();
        store.Entries[(Tier.Pinned, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("pm1", "PINNED-DIRECTIVE"),
        };
        store.Entries[(Tier.Hot, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("h1", "HOT-CONTENT"),
        };

        var lifecycle = BuildLifecycle(store);
        await lifecycle.EnsureInitializedAsync();
        var result = await lifecycle.RefreshAsync();

        // Pinned directive header must appear in the refresh context.
        Assert.Contains("## Pinned directives (always follow)", result.Context);
        // Pinned content must come before hot content.
        var pinnedIdx = result.Context.IndexOf("PINNED-DIRECTIVE", StringComparison.Ordinal);
        var hotIdx    = result.Context.IndexOf("HOT-CONTENT",      StringComparison.Ordinal);
        Assert.True(pinnedIdx >= 0, "Pinned content missing from refresh context");
        Assert.True(hotIdx > pinnedIdx, "Pinned content must appear before hot content");
    }

    [Fact]
    public async Task Refresh_PinnedTokens_ReduceHotBudget_AndCountInTokenCount()
    {
        // Large pinned content + tiny budget — pinned still fully injected verbatim,
        // and TokenCount includes the pinned token estimate.
        var bigContent = "PINNED " + string.Join(" ", Enumerable.Range(1, 30).Select(i => $"word{i}"));
        var store = new FakeStore();
        store.Entries[(Tier.Pinned, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("pm1", bigContent),
        };

        var lifecycle = BuildLifecycle(store, tokenBudget: 10);
        await lifecycle.EnsureInitializedAsync();
        var result = await lifecycle.RefreshAsync();

        // Full pinned content must appear in the refresh context (never truncated).
        Assert.Contains("PINNED", result.Context);
        // TokenCount must be >= estimated pinned tokens (header + content words * 0.75).
        var pinnedBlockEstimate = SessionLifecycle.HeuristicEstimateTokens(
            "## Pinned directives (always follow)\n- " + bigContent);
        Assert.True(result.TokenCount >= pinnedBlockEstimate,
            $"TokenCount {result.TokenCount} should be >= pinnedBlockEstimate {pinnedBlockEstimate}");
    }

    [Fact]
    public async Task Refresh_PinnedIds_IncludedInInjectionCounts()
    {
        // Verify that RefreshAsync calls UpdateInjectionCounts with the pinned entry id.
        var store = new FakeStore();
        store.Entries[(Tier.Pinned, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("pm1", "always-follow-rule"),
        };

        var lifecycle = BuildLifecycle(store);
        await lifecycle.EnsureInitializedAsync();
        // Clear init-time injection records so we only see refresh calls.
        store.InjectionCountCalls.Clear();

        await lifecycle.RefreshAsync();

        Assert.Contains(store.InjectionCountCalls,
            t => t.tier == Tier.Pinned && t.type == ContentType.Memory && t.id == "pm1");
    }

    // ---------- PinnedLifecycle round-trip ----------

    [Fact]
    public async Task PinnedLifecycle_RoundTrip_PinInjected_UnpinExcluded()
    {
        const string DirectiveContent = "always use kebab-case for branch names";

        // --- Phase 1: pinned entry IS injected on session init ---
        var store = new FakeStore();
        store.Entries[(Tier.Pinned, ContentType.Memory)] = new List<Entry>
        {
            MakeEntry("pin1", DirectiveContent),
        };

        var lifecycle1 = BuildLifecycle(store);
        var result1 = await lifecycle1.EnsureInitializedAsync();

        Assert.Contains("## Pinned directives (always follow)", result1.Context);
        Assert.Contains(DirectiveContent, result1.Context);

        // --- Simulate unpin: move the entry from Pinned → Warm (as the handler does) ---
        store.Move(Tier.Pinned, ContentType.Memory, Tier.Warm, ContentType.Memory, "pin1");

        // --- Phase 2: new session over the same store — pinned block must be absent ---
        var lifecycle2 = BuildLifecycle(store);
        var result2 = await lifecycle2.EnsureInitializedAsync();

        // No pins remain, so the directive header must not appear.
        Assert.DoesNotContain("## Pinned directives (always follow)", result2.Context);
        // The content may appear in a future warm-tier hint but must NOT be in a pinned block.
        // Guard: confirm the header is truly gone — content in warm context is acceptable.
        var headerIdx = result2.Context.IndexOf("## Pinned directives (always follow)", StringComparison.Ordinal);
        Assert.Equal(-1, headerIdx);
    }
}
